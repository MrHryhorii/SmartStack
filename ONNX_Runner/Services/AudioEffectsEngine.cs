using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Buffers;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Server-side audio effects engine for TTS post-processing.
///
/// SERVER INTEGRATION MANIFESTO (CRITICAL):
///   - DI Lifecycle: This class is stateful. It MUST be registered as TRANSIENT or managed 
///     via ObjectPool per request. NEVER register as Singleton in a multi-tenant environment.
///   - Zero-Allocation: DSP loop generates zero garbage. String parsing is offloaded to the caller.
///
/// DSP ROUTING MANIFESTO:
///   - Power-Matched Crossfade: fixed Telephone, G.711, FM and TacticalRadio character with measured power interpolation.
///   - Ring Insert: internal carrier depth moves from tremolo to ring modulation; a scalar sets playback level.
///   - Crusher Insert: intensity controls degradation; measured buffer power sets playback level.
///   - Drive Insert: gradual asymmetric saturation and adaptive filters; a scalar sets playback level.
///   - Modulation Insert: complete Chorus/Flanger processors with prepared scalar playback gains.
///   - Tape Insert: intrinsic record/playback character, slow voice-only level correction, additive hiss.
///   - Strict Insert: wet -> Complete signal chain transformations; the amount-zero guard preserves bypass.
///   - Strict Crossfade: Dsp.Lerp(dry, wet, amount) -> For effects where the wet signal is not strictly additive.
/// </summary>
///
/// DESIGN PHILOSOPHY:
/// This engine targets high-throughput server-side TTS processing.
/// Algorithms prioritize:
///   - zero runtime allocations;
///   - deterministic CPU cost;
///   - single-pass streaming compatibility;
///   - perceptual authenticity over physical simulation.
///
/// OPTIMIZATION STRATEGY:
///   - Constant MathF.Exp/MathF.Sqrt work is hoisted out of sample loops where practical.
///   - Equal-power wet/dry gains are computed once per buffer, not once per sample.
///   - LFO phase increments (2pi * f / fs) are pre-computed per effect and stored in state structs.
///   - The ms-to-samples conversion factor is stored as a readonly field.
///   - G.711 uses integer segmented companding/expanding: no Log/Exp/Pow in the codec hot path.
public class AudioEffectsEngine(EffectsSettings config, int sampleRate)
{
    // All sample-domain tuning is calibrated to the same voice-model rate.
    private const int ReferenceSampleRate = 22050;
    private const float HissLevel = 0.8f;
    private const float HissHighPassHz = 1400f;
    private const float HissLowPassHz = 9000f;
    // Partial compensation of record-stage gain leaves about +1 dB at full playback.
    private const float TapePlaybackCompensation = 0.423369f;
    // Fixed output trims keep full effects near +1 dB on the reference Piper voice.
    // Apply after character generation; drive, compression and modulation stay intact.
    private const float OverdriveOutputGain = 0.5347782f;
    private const float TacticalOutputGain = 0.63f;
    private const float FmOutputBoostDb = 1.7f;
    private static readonly float FmOutputGain = .78f * MathF.Pow(10f, FmOutputBoostDb / 20f);
    private const float ThermalReferenceCoeff = 0.00021773577f;
    private const double CrusherMaximumLevelGain = 4;
    private readonly EffectsSettings _config = config;
    private readonly int _sampleRate = sampleRate > 0
        ? sampleRate : throw new ArgumentOutOfRangeException(nameof(sampleRate));
    private readonly float _sampleRateScale = sampleRate / (float)ReferenceSampleRate;
    private readonly float _msToSamples = sampleRate * 0.001f;
    private readonly float _inverseSampleRate = 1f / sampleRate;
    // Read-before-write taps include one causal sample. Keep its reference duration at other rates.
    private readonly float _preWriteReadOffset = sampleRate / (float)ReferenceSampleRate - 1f;

    // Reference coefficients retain the previous attack/release times at 22050 Hz.
    private readonly float _envAtt = ScaleReferenceCoefficient(0.3847673f, sampleRate);
    private readonly float _envRel = ScaleReferenceCoefficient(0.010852337f, sampleRate);
    private readonly float _glitchAtt = ScaleReferenceCoefficient(0.99334526f, sampleRate);
    private readonly float _glitchRel = ScaleReferenceCoefficient(0.0021755695f, sampleRate);
    private readonly float _tapeSlew = ScaleReferenceCoefficient(0.004348576f, sampleRate);
    private readonly float _thermalDriftCoeff = ScaleReferenceCoefficient(ThermalReferenceCoeff, sampleRate);
    private readonly float _thermalNoiseGain = OnePoleNoiseGain(ThermalReferenceCoeff,
        ScaleReferenceCoefficient(ThermalReferenceCoeff, sampleRate));
    private float _thermalValue;
    private int _pinkClockPhase;
    private float _pinkPrevious, _pinkCurrent;

    // Energy of unit white noise before/after the cassette speaker blend, including DC removal.
    private readonly record struct NoiseEnergy(double Dry, double Cross, double Wet)
    {
        public double At(float mix) => Dry * (1 - mix) * (1 - mix)
            + 2 * Cross * mix * (1 - mix) + Wet * mix * mix;
    }
    private static readonly NoiseEnergy ReferenceHissEnergy = TapeNoiseEnergy(ReferenceSampleRate, true);
    private static readonly NoiseEnergy ReferenceGrainEnergy = TapeNoiseEnergy(ReferenceSampleRate, false);

    private enum RoutingMode
    {
        PowerMatchedCrossfade,
        ModulationInsert,
        StrictInsert,
        StrictCrossfade,
        CrusherInsert,
        DriveInsert,
        RingInsert,
        TapeInsert
    }

    // =========================================================================
    // EFFECT STATE STRUCTURES (Zero-Allocation)
    // =========================================================================

    private struct DriveState
    {
        public float PreparedAmount;
        public float Drive;
        public float Trim;
        public float BiasScale;
        public float Gain;
        public float HighPassBlend;
        public float LowPassBlend;
    }

    private struct RingState
    {
        public float PreparedAmount;
        public float Depth;
        public float Makeup;
        public float SaturationDrive;
        public float InverseDrive;
        public WindowedPowerMatcher? Level;
        public float FilterBlend;
    }

    private struct ModulationState
    {
        public float PreparedAmount;
        public float Strength;
        public float Gain;
        public float SaturationDrive;
        public float InverseDrive;
        public float Base1, Base2, Depth1, Depth2;
        public float WetScale, Feedback;
        public bool FullStrength;
        public float RingPhase;
        public float FlangerPhase;
        public float ChorusPhase;
        public float ChorusPhase2;

        // Pre-computed LFO phase increments (2pi * f / fs) set in Setup, read per-sample.
        // Eliminates one multiply and one divide per LFO per sample in the hot path.
        public float FlangerPhaseInc;  // 0.45 Hz
        public float ChorusPhaseInc;   // 0.55 Hz
        public float ChorusPhaseInc2;  // 0.83 Hz
    }

    private struct BitcrusherState
    {
        public float Phase;
        public float Hold;
        public float PreparedAmount;
        public double Gain;
        public int GainRampSamples;
        public float ClockStep;
        public float Scale;
        public float InverseScale;
        public int MinValue;
        public int MaxValue;
    }

    private struct TapeState
    {
        public BiQuadFilter? PreHp;
        public float LofiPhase;
        public float LofiPhase2;
        public float FlutterPhase;
        public float PreState;
        public float DeState;
        public float HissLowState;
        public float HissState;
        public float FlutterNoiseState;
        public float PreCoeff;
        public float DeCoeff;
        public float HissLowCoeff;
        public float HissCoeff;
        public float FlutterNoiseCoeff;
        public float FlutterNoiseGain;
        public BiQuadFilter? HissColor;
        public float HissGain;
        public NoiseEnergy HissEnergy, GrainEnergy;

        // Per-buffer constants derived from amount. Prepared once in ApplyEffect so the
        // tape hot path avoids repeated divisions and invariant arithmetic per sample.
        // Playback character appears early; wear-related controls retain their existing curves.
        public float CharacterAmount;
        public float PreEmphScale;
        public float SatDrive;
        public float SatCompensation;
        public float CompThreshold;
        public float CompMakeup;
        public float DelayMs;
        public float HfCutScale;
        public float AzimuthMix;
        public float BiasGrainScale;
        public float HissAmount;
        public float ThermalHissScale;
        public float FlutterNoiseScale;
        public float SpeakerMix;
        public float PerceptualTrim;
        public float OutputGain;
        public float ReferenceSample;
        public float NoiseSample;
        public TapeVoiceNormalizer Level;
        public DelayBuffer? ReferenceDelay;
        public TapeNoiseFilter NoiseBump, NoiseResonance, NoiseHp, NoiseLp;
        public DcBlocker NoiseDc;

        // Pre-computed LFO phase increments: eliminates per-sample 2pi*f/fs multiply.
        public float LofiPhaseInc;     // 0.4 Hz   (primary wow)
        public float LofiPhaseInc2;    // 0.124 Hz (secondary wow)
        public float FlutterPhaseInc;  // 8.5 Hz   (flutter)
        public float AzimuthPhaseInc;  // 0.37 Hz (azimuth drift)

        public TapeDropout DropOut;
        public FeedForwardCompressor Compressor;
        public float CompAttackCoeff;
        public float CompReleaseCoeff;

        public BiQuadFilter? BoomboxBump;
        public BiQuadFilter? BoomboxResonance;
        public BiQuadFilter? BoomboxHp;
        public BiQuadFilter? BoomboxLp;
    }

    private struct AzimuthState
    {
        public float Phase;
        public Dsp.OnePoleCurve Curve;
        public float FilterState;
    }

    private struct GlitchState
    {
        public int WritePos;
        public bool Frozen;
        public int LoopStart;
        public int LoopLen;
        public int PlayPos;
        public int Remain;
        public int Cooldown;
        public int TriggerWindowTimer;

        // ZCR detector
        public float Energy;
        public float Zcr;
        public float PrevSample;
        public int ZcrCount;

        // Accumulates missed trigger rolls to guarantee eventual glitch on sustained vowels.
        public float TriggerAccumulator;
        // Hoisted from per-trigger sqrt(amount)
        public float SqrtAmount;
    }

    private struct RadioState
    {
        // Discriminator: identifies which G.711 variant is active (shared struct, set in Setup).
        public bool G711ALaw; // false = mu-law, true = A-law

        // TacticalRadio: full CVSD-based signal chain.
        public float TacticalEnvelope;
        public FeedForwardCompressor TacticalCompressor;
        public float CompAttackCoeff;
        public float CompReleaseCoeff;

        // CVSD encoder/decoder state
        public uint CvsdHistory;
        public float CvsdStepSize;
        public float CvsdAccumulator;

        public float AccumDecay;   // ~25ms leak
        public float StepDecay;    // ~4ms RC step decay
        public float ReconCoeff;   // reconstruction LP at 2400 Hz
        public float ReconState;

        public float MinStep;
        public float MaxStep;
        public float StepGrow;
        public int CvsdClockPhase;
        public float CvsdPrevious;

    }

    // =========================================================================
    // CORE DSP PRIMITIVES & BUFFERS
    // =========================================================================

    private readonly DelayBuffer _delay = new(DelayCapacity(sampleRate));
    private DcBlocker _dcBlocker = new(sampleRate);
    private NoiseGenerator _noise = new();
    private ThermalDrift _thermal = new();

    private readonly BiQuadFilter[] _filters = new BiQuadFilter[5];
    private int _filterCount;
    private FmRadioProcessor? _fmProcessor;
    private float _fmAmount;
    private const float CompletionSilenceFloor = 1e-7f;
    private VoiceEffectType _current = VoiceEffectType.None;

    private const int GlitchReferenceCaptureSamples = 4096;
    // Keep the original rolling capture duration without rounding it to a power-of-two size.
    private readonly float[] _glitchCapture = new float[Math.Max(1, (int)Math.Round(
        GlitchReferenceCaptureSamples * (double)sampleRate / ReferenceSampleRate))];
    private readonly int _glitchWindowSamples = Math.Max(1, sampleRate / 100);


    // =========================================================================
    // EFFECT STATE INSTANCES
    // =========================================================================

    private ModulationState _mod;
    private RingState _ring;
    private DriveState _drive;
    private BitcrusherState _bc;
    private TapeState _tape;
    private AzimuthState _azimuth;
    private GlitchState _glitch;
    private RadioState _radio;

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Resets all internal states of the effects engine.
    /// Should be called at the start of each new audio generation request to ensure deterministic
    /// behavior and prevent state bleed between requests.
    /// Optionally accepts a fixed random seed for noise generators to allow reproducible effects.
    /// If no seed is provided, uses the current time for non-deterministic noise patterns.
    /// </summary>
    public void Reset(uint? fixedSeed = null)
    {
        _delay.Clear();
        _dcBlocker.Reset();
        _fmProcessor?.Reset();
        _fmAmount = 0f;

        Array.Clear(_filters, 0, _filters.Length);
        _filterCount = 0;

        uint seed = fixedSeed ?? (uint)(DateTime.UtcNow.Ticks % uint.MaxValue);
        _noise.Seed(seed);
        _noise.Reset();
        _thermal.Reset();

        _current = VoiceEffectType.None;

        _mod = default;
        _ring.PreparedAmount = float.NaN;
        _drive.PreparedAmount = float.NaN;

        _bc.Phase = 1f;
        _bc.Hold = 0f;
        _bc.PreparedAmount = float.NaN;

        _tape.DropOut.Reset();
        _tape.Compressor.Reset();
        _tape.LofiPhase = 0f;
        _tape.LofiPhase2 = 0f;
        _tape.FlutterPhase = 0f;
        _tape.PreState = 0f;
        _tape.DeState = 0f;
        _tape.HissLowState = 0f;
        _tape.HissState = 0f;
        _tape.FlutterNoiseState = 0f;

        _azimuth = default;

        _glitch = default;
        _glitch.TriggerWindowTimer = _glitchWindowSamples;
        Array.Clear(_glitchCapture, 0, _glitchCapture.Length);

        _thermalValue = 0f;
        _pinkClockPhase = 0;
        _pinkPrevious = _pinkCurrent = 0f;

        _radio = default;
        _radio.TacticalCompressor.Reset();
    }

    /// <summary>Reports retained audio for effects that support finite request completion.</summary>
    internal bool HasPendingOutput => _current == VoiceEffectType.FmRadio && _fmProcessor != null &&
        (_fmProcessor.PendingSamples > 0 || _dcBlocker.PendingMagnitude * FmOutputGain > CompletionSilenceFloor);

    /// <summary>Writes retained effect audio only; repeated calls stop when its filters are drained.</summary>
    internal int Complete(Span<float> destination)
    {
        if (destination.IsEmpty || !HasPendingOutput) return 0;
        if (_fmAmount >= 1f) return CompleteFmWet(destination, Span<float>.Empty);

        float[] rented = ArrayPool<float>.Shared.Rent(destination.Length);
        try
        {
            Span<float> dry = rented.AsSpan(0, destination.Length);
            int written = CompleteFmWet(destination, dry);
            PowerMatchedMixer.Mix(dry[..written], destination[..written], _fmAmount);
            return written;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    /// <summary>Drains the fixed wet state conservatively; the late wet-only mix cannot exceed this level.</summary>
    private int CompleteFmWet(Span<float> destination, Span<float> dry)
    {
        int written = 0;
        if (dry.IsEmpty)
        {
            while (written < destination.Length && HasPendingOutput)
                destination[written++] = ProcessFmWet(0f, out _);
            return written;
        }
        while (written < destination.Length && HasPendingOutput)
        {
            destination[written] = ProcessFmWet(0f, out float delayed);
            dry[written] = delayed;
            written++;
        }
        return written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ProcessFmWet(float sample, out float dry)
    {
        float wet = _fmProcessor!.Process(Dsp.KillDenormal(sample), out dry);
        wet = _dcBlocker.Process(wet);
        // Preserve the accepted full-effect multiplication and signed-zero expression.
        return dry * 0f + wet * FmOutputGain;
    }

    /// <summary>
    /// Applies the specified audio effect to the input buffer in-place.
    /// The amount parameter controls the intensity of the effect (0 = no effect, 1 = full effect).
    /// External blends retain the request amount; most wet algorithms receive an eased strength.
    /// FM, Telephone, G.711 and TacticalRadio keep fixed wet settings while intensity controls their blends.
    /// Optimized for real-time processing with zero allocations in the audio thread.
    /// </summary>
    public void ApplyEffect(Span<float> buffer, VoiceEffectType type, float amount)
    {
        if (!_config.EnableGlobalEffects || type == VoiceEffectType.None)
            return;

        // Fast-path bypass for CPU savings in server environments.
        // Keep the guard to skip DSP work and preserve bit-identical bypass, including warm state.
        if (amount <= 0.001f)
            return;

        if (_current != type)
        {
            Setup(type);
            _delay.Clear();
            _dcBlocker.Reset();
            _current = type;
        }

        RoutingMode mode = GetRoutingMode(type);
        if (mode == RoutingMode.PowerMatchedCrossfade)
        {
            ApplyPowerMatched(buffer, type, amount);
            return;
        }

        if (mode == RoutingMode.TapeInsert)
        {
            ApplyTape(buffer, amount);
            return;
        }

        if (mode == RoutingMode.CrusherInsert)
        {
            ApplyBitcrusher(buffer, amount);
            return;
        }

        if (mode == RoutingMode.ModulationInsert)
        {
            PrepareModulation(type, amount);
            bool chorus = type == VoiceEffectType.Chorus;
            for (int i = 0; i < buffer.Length; i++)
            {
                _thermal.Update(ref _noise, _thermalDriftCoeff);
                _thermalValue = _thermal.State * _thermalNoiseGain;
                buffer[i] = chorus ? Chorus(buffer[i]) : Flanger(buffer[i]);
            }
            return;
        }

        if (mode == RoutingMode.RingInsert)
        {
            PrepareRing(amount);
            BiQuadFilter highPass = _filters[0];
            float filterBlend = _ring.FilterBlend;
            bool fullFiltering = filterBlend >= 1f;
            WindowedPowerMatcher level = _ring.Level!;
            for (int i = 0; i < buffer.Length; i++)
            {
                _thermal.Update(ref _noise, _thermalDriftCoeff);
                _thermalValue = _thermal.State * _thermalNoiseGain;
                float input = Dsp.KillDenormal(buffer[i]);
                // Fade only below the stable biquad corner, before modulation of the entire signal.
                float filtered = highPass.Transform(input);
                if (!fullFiltering) filtered = Dsp.Lerp(input, filtered, filterBlend);
                float processed = _dcBlocker.Process(RingMod(filtered));
                buffer[i] = level.Process(input, processed);
            }
            return;
        }

        if (mode == RoutingMode.DriveInsert)
        {
            PrepareDrive(amount);
            BiQuadFilter highPass = _filters[0], lowPass = _filters[1];
            float hpBlend = _drive.HighPassBlend, lpBlend = _drive.LowPassBlend;
            bool fullHighPass = hpBlend >= 1f, fullLowPass = lpBlend >= 1f;
            for (int i = 0; i < buffer.Length; i++)
            {
                _thermal.Update(ref _noise, _thermalDriftCoeff);
                _thermalValue = _thermal.State * _thermalNoiseGain;
                float input = Dsp.KillDenormal(buffer[i]);
                float filtered = highPass.Transform(input);
                if (!fullHighPass) filtered = Dsp.Lerp(input, filtered, hpBlend);
                float limited = lowPass.Transform(filtered);
                if (!fullLowPass) limited = Dsp.Lerp(filtered, limited, lpBlend);
                buffer[i] = _dcBlocker.Process(Overdrive(limited)) * _drive.Gain;
            }
            return;
        }

        // Strengthen the wet algorithm before its contribution is reduced by the outer blend.
        // Tape and rolling glitches retain their own authored intensity laws.
        float effectAmount = mode is RoutingMode.StrictInsert
            ? amount : amount * (2f - amount);
        int filterCount = _filterCount;
        BiQuadFilter[] filters = _filters;

        // Per-buffer constants used by effect hot paths.
        switch (type)
        {
            case VoiceEffectType.DecoderGlitch:
                // Hoist sqrt(amount) above the sample loop for Glitch duration.
                _glitch.SqrtAmount = MathF.Sqrt(amount);
                break;

        }

        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];

            float filtered = Dsp.KillDenormal(dry);

            _thermal.Update(ref _noise, _thermalDriftCoeff);
            _thermalValue = _thermal.State * _thermalNoiseGain;

            if (filterCount > 0)
                for (int f = 0; f < filterCount; f++)
                    filtered = filters[f].Transform(filtered);

            float wet = Process(type, filtered, effectAmount);
            // Tape blends DC removal internally; rolling glitches preserve their captured samples.
            wet = type is not (VoiceEffectType.DecoderGlitch or VoiceEffectType.LoFiTape)
                ? _dcBlocker.Process(wet) : wet;

            // The early amount guard handles bypass; active routing follows the effect's signal structure.
            switch (mode)
            {
                case RoutingMode.StrictInsert:
                    buffer[i] = wet;
                    break;

                case RoutingMode.StrictCrossfade:
                    buffer[i] = Dsp.Lerp(dry, wet, amount);
                    break;

            }
        }
    }

    // =========================================================================
    // HOST DISPATCH & SETUP
    // =========================================================================

    /// <summary>Selects the wet/dry routing policy used to mix each voice effect with the source signal.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RoutingMode GetRoutingMode(VoiceEffectType type)
    {
        return type switch
        {
            VoiceEffectType.Telephone => RoutingMode.PowerMatchedCrossfade,
            VoiceEffectType.G711MuLaw => RoutingMode.PowerMatchedCrossfade,
            VoiceEffectType.G711ALaw => RoutingMode.PowerMatchedCrossfade,
            VoiceEffectType.Overdrive => RoutingMode.DriveInsert,
            VoiceEffectType.Bitcrusher => RoutingMode.CrusherInsert,
            VoiceEffectType.RingModulator => RoutingMode.RingInsert,
            VoiceEffectType.Flanger => RoutingMode.ModulationInsert,
            VoiceEffectType.Chorus => RoutingMode.ModulationInsert,
            VoiceEffectType.LoFiTape => RoutingMode.TapeInsert,
            VoiceEffectType.DecoderGlitch => RoutingMode.StrictInsert,
            VoiceEffectType.TacticalRadio => RoutingMode.PowerMatchedCrossfade,
            VoiceEffectType.FmRadio => RoutingMode.PowerMatchedCrossfade,
            _ => RoutingMode.StrictInsert
        };
    }


    /// <summary>
    /// Configures effect-specific filters and coefficients that remain constant for the effect type.
    /// Per-buffer constants that depend on amount are prepared in ApplyEffect before the sample loop.
    /// </summary>
    private void Setup(VoiceEffectType type)
    {
        _filterCount = 0;
        _dcBlocker.SetCutoff(35f);

        float nyq = _sampleRate * 0.45f;
        // Caps filter frequencies at the configured Nyquist safety limit.
        float Safe(float f) => Math.Min(f, nyq);

        // Standard LFO phase increment: 2pi * f / fs.
        // Defined as a local helper to make Setup self-documenting.
        float PhaseInc(float hz) => 2f * MathF.PI * hz / _sampleRate;

        switch (type)
        {
            case VoiceEffectType.Telephone:
                // Classic POTS equalization: tight 380-3400 Hz bandpass with dual cavity
                // resonance peaks (820/950 Hz) for handset body coloring and a carbon
                // microphone presence peak (1800 Hz). Intentionally vintage character
                // to distinguish it from the cleaner G.711 codec effects.
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(380f), 1.0f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(820f), 6.0f, 8.0f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(950f), 4.5f, 7.5f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(1800f), 2.2f, 6.0f);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(3400f), 1.0f);
                break;

            case VoiceEffectType.Overdrive:
                _drive.PreparedAmount = float.NaN;
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(180f), 0.8f);
                // Keep the reference top cut in Hz; low-rate models still obey the Nyquist cap.
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(ReferenceSampleRate * 0.45f), 0.707f);
                break;

            case VoiceEffectType.Bitcrusher:
                _bc.PreparedAmount = float.NaN;
                _bc.Gain = 1;
                _bc.GainRampSamples = Math.Max(2, (int)Math.Round(_sampleRate * .005));
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(130f), 0.707f);
                break;

            case VoiceEffectType.RingModulator:
                _ring.PreparedAmount = float.NaN;
                // Average speech-level changes without following the 30 Hz modulation.
                _ring.Level ??= new WindowedPowerMatcher(_sampleRate, windowMs: 1200f, maximumRiseDbPerSecond: 24);
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(130f), 0.707f);
                break;

            case VoiceEffectType.Flanger:
                _mod.PreparedAmount = float.NaN;
                _mod.FlangerPhaseInc = PhaseInc(0.45f);
                break;

            case VoiceEffectType.Chorus:
                _mod.PreparedAmount = float.NaN;
                _mod.ChorusPhaseInc = PhaseInc(0.55f);
                _mod.ChorusPhaseInc2 = PhaseInc(0.83f);
                break;

            case VoiceEffectType.LoFiTape:
                _tape.PreHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(80f), 0.707f);

                // Approximate Type-I cassette HF EQ corner derived from the 120 us time constant
                // (~1326 Hz). This is a perceptual pre/de-emphasis stage, not a full IEC replay curve.
                float iecFc = 1326f;
                _tape.PreCoeff = 1f - MathF.Exp(-2f * MathF.PI * iecFc / _sampleRate);
                _tape.DeCoeff = _tape.PreCoeff;

                // Cassette hiss is concentrated toward the upper spectrum rather than being
                // full-band white noise. Remove low-frequency noise below ~1.4 kHz, then
                // gently roll off the extreme top above ~9 kHz to keep the hiss analog/soft.
                _tape.HissLowCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(HissHighPassHz) / _sampleRate);
                _tape.HissCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(HissLowPassHz) / _sampleRate);

                // Band-limit the random component of flutter so delay modulation changes
                // smoothly instead of jumping independently on every sample.
                _tape.FlutterNoiseCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(35f) / _sampleRate);
                float referenceFlutterCoeff = 1f - MathF.Exp(-2f * MathF.PI * 35f / ReferenceSampleRate);
                _tape.FlutterNoiseGain = OnePoleNoiseGain(referenceFlutterCoeff, _tape.FlutterNoiseCoeff);
                _tape.HissColor = CreateTapeHissColor(_sampleRate);
                _tape.HissEnergy = TapeNoiseEnergy(_sampleRate, true, true);
                _tape.GrainEnergy = TapeNoiseEnergy(_sampleRate, false);

                _tape.PreState = _tape.DeState = 0f;
                _tape.HissLowState = _tape.HissState = 0f;
                _tape.FlutterNoiseState = 0f;

                _tape.DropOut.Reset();
                _tape.Compressor.Reset();
                _tape.CompAttackCoeff = FeedForwardCompressor.TimeToCoeff(2f, _sampleRate);
                _tape.CompReleaseCoeff = FeedForwardCompressor.TimeToCoeff(200f, _sampleRate);

                float referenceHigh = 1f - MathF.Exp(-2f * MathF.PI * (ReferenceSampleRate * .45f) / ReferenceSampleRate);
                float referenceLow = 1f - MathF.Exp(-2f * MathF.PI * 4500f / ReferenceSampleRate);
                _azimuth.Curve = Dsp.MatchOnePoleResponse(_sampleRate, ReferenceSampleRate, referenceHigh, referenceLow, 4500f);
                _azimuth.Phase = 0f;
                _azimuth.FilterState = 0f;

                _tape.BoomboxBump = BiQuadFilter.PeakingEQ(_sampleRate, Safe(120f), 0.8f, 4.0f);
                _tape.BoomboxResonance = CreateTapeResonance(_sampleRate);
                _tape.BoomboxHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(70f), 0.707f);
                _tape.BoomboxLp = BiQuadFilter.LowPassFilter(_sampleRate, Safe(7500f), 0.707f);
                // Independent filter histories keep playback hiss outside voice level correction.
                _tape.NoiseBump = TapeNoiseFilter.Peak(_sampleRate, Safe(120f), 0.8f, 4.0f);
                _tape.NoiseResonance = TapeNoiseFilter.Peak(_sampleRate, Safe(700f), 1f, 1.5f);
                _tape.NoiseHp = TapeNoiseFilter.HighPass(_sampleRate, Safe(70f), 0.707f);
                _tape.NoiseLp = TapeNoiseFilter.LowPass(_sampleRate, Safe(7500f), 0.707f);
                _tape.NoiseDc = new DcBlocker(_sampleRate);
                int referenceCapacity = (int)BitOperations.RoundUpToPowerOf2(
                    (uint)Math.Max(16, (int)Math.Ceiling(_sampleRate * .004) + 2));
                _tape.ReferenceDelay = new DelayBuffer(referenceCapacity);
                _tape.Level = new TapeVoiceNormalizer(_sampleRate);
                _tape.ReferenceSample = _tape.NoiseSample = 0;

                // Pre-compute independent LFO phase increments for both wow components,
                // flutter, and azimuth modulation. The secondary wow oscillator needs its own
                // continuously wrapped phase; deriving it from the wrapped primary phase would
                // introduce a discontinuity every primary LFO cycle.
                _tape.LofiPhaseInc = PhaseInc(0.4f);
                _tape.LofiPhaseInc2 = PhaseInc(0.124f);
                _tape.FlutterPhaseInc = PhaseInc(8.5f);
                _tape.AzimuthPhaseInc = PhaseInc(0.37f);
                break;

            case VoiceEffectType.DecoderGlitch:
                _glitch = default;
                _glitch.TriggerWindowTimer = _glitchWindowSamples; // Approximately 10 ms.
                break;

            case VoiceEffectType.TacticalRadio:
                // Tactical / military radio: author's perceptual model of SINCGARS / AN/PRC-class voice.
                // All CVSD timing constants pre-computed here; zero MathF.Exp in the sample loop.
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(320f), 0.707f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(1100f), 1.4f, 3.0f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(2100f), 1.8f, 5.0f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(2800f), 1.4f, 3.0f);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(3400f), 0.707f);

                _radio = default;
                _radio.TacticalCompressor.Reset();

                _radio.CompAttackCoeff = FeedForwardCompressor.TimeToCoeff(1f, _sampleRate);
                _radio.CompReleaseCoeff = FeedForwardCompressor.TimeToCoeff(80f, _sampleRate);

                _radio.ReconCoeff = 1f - MathF.Exp(-2f * MathF.PI * 2400f / ReferenceSampleRate);
                _radio.AccumDecay = MathF.Exp(-1f / (0.025f * ReferenceSampleRate));
                _radio.StepDecay = MathF.Exp(-1f / (0.004f * ReferenceSampleRate));

                // A fixed virtual bit clock keeps the four-bit history window and adaptation
                // independent of the host rate. These are the existing 22050 Hz step sizes.
                _radio.MinStep = 0.0043537416f;
                _radio.MaxStep = 0.26122448f;
                _radio.StepGrow = 0.017414967f;

                _radio.CvsdStepSize = _radio.MinStep;
                _radio.CvsdAccumulator = 0f;
                _radio.ReconState = 0f;
                break;

            case VoiceEffectType.FmRadio:
                _fmProcessor ??= new FmRadioProcessor(_sampleRate);
                _fmProcessor.Reset();
                break;

            case VoiceEffectType.G711MuLaw:
            case VoiceEffectType.G711ALaw:
                // Both G.711 variants share a fixed 300-3400 Hz wet path.
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(300f), 0.707f);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(3400f), 0.707f);

                _radio = default;
                _radio.G711ALaw = type == VoiceEffectType.G711ALaw;
                break;
        }
    }

    /// <summary>Prepares linear bit depth, an eased logarithmic update rate, and independent gradual bass filtering.</summary>
    private void PrepareBitcrusher(float amount)
    {
        if (_bc.PreparedAmount == amount) return;
        _bc.PreparedAmount = amount;
        float strength = Dsp.CrushStrength(amount);
        float bits = Math.Clamp(16f - amount * 12f, 4f, 16f);
        _bc.Scale = MathF.Pow(2f, bits - 1f);
        _bc.InverseScale = 1f / _bc.Scale;
        // Fractional depths retain uniform integer codes within the signed PCM range.
        // Integer depths preserve the existing negative rail and one-code-short positive rail.
        _bc.MinValue = -(int)MathF.Floor(_bc.Scale);
        _bc.MaxValue = (int)MathF.Ceiling(_bc.Scale) - 1;
        float fullClockStep = Math.Min(1f, 11025f / _sampleRate);
        _bc.ClockStep = MathF.Pow(fullClockStep, Dsp.CrushClock(amount));
        _filters[0].SetHighPassFilter(_sampleRate, Math.Min(130f * strength, _sampleRate * .45f), .707f);
        _dcBlocker.SetCutoff(35f * strength);
    }

    /// <summary>Prepares cassette component amounts and playback trim once per buffer.</summary>
    private void PrepareTape(float amount)
    {
        float amountSq = amount * amount;
        // Quadratic ease-out has finite initial slope; sqrt(amount) would exaggerate tiny settings.
        _tape.CharacterAmount = amount * (2f - amount);
        float character = _tape.CharacterAmount;
        _tape.PreEmphScale = 0.8f * character;
        _tape.SatDrive = 1f + 2.0f * character;
        _tape.SatCompensation = 1f / (1f + 0.35f * character);
        _tape.CompThreshold = Dsp.Lerp(1.0f, 0.38f, character);
        _tape.CompMakeup = Dsp.Lerp(1f, 0.90f, character);
        _tape.DelayMs = 0.1f * _tape.CharacterAmount + 1.3f * amount;
        _tape.HfCutScale = 0.50f * character;
        _tape.AzimuthMix = amountSq * 0.45f;
        _tape.HissGain = (float)Math.Sqrt(ReferenceHissEnergy.At(amountSq) / _tape.HissEnergy.At(amountSq));
        float grainGain = _sampleRate == ReferenceSampleRate ? 1f
            : (float)Math.Sqrt(ReferenceGrainEnergy.At(amountSq) / _tape.GrainEnergy.At(amountSq));
        _tape.BiasGrainScale = 0.0020f * amount * grainGain;
        // Factor the final amount out of the noise filter so its warm history also fades to zero.
        _tape.HissAmount = (0.0025f + 0.0075f * amount) * HissLevel * _tape.HissGain;
        // The final output amount yields CharacterAmount without division near zero.
        _tape.ThermalHissScale = 2f - amount;
        _tape.FlutterNoiseScale = 0.50f * amount;
        _tape.SpeakerMix = amountSq;
        _tape.PerceptualTrim = 1f - 0.25f * amountSq;
        // Retain the authored playback/noise trim while record character now develops earlier.
        // The voice-only detector corrects residual level differences independently of this floor.
        float nominalRecordGain = (1f + 2f * amount) / (1f + .35f * amount);
        float saturationLift = amount * (nominalRecordGain - 1f);
        _tape.OutputGain = 1f / (1f + TapePlaybackCompensation * saturationLift);
    }

    /// <summary>Dispatches one sample to the selected voice effect, retaining its per-request DSP state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Process(VoiceEffectType type, float x, float amount)
    {
        return type switch
        {
            VoiceEffectType.Bitcrusher => Bitcrusher(x, amount),
            VoiceEffectType.LoFiTape => LoFiTape(x, amount),
            VoiceEffectType.DecoderGlitch => DecoderGlitch(x, amount),
            _ => x
        };
    }

    /// <summary>Preserves the physical decay time of a one-pole coefficient tuned at the reference rate.</summary>
    private static float ScaleReferenceCoefficient(float coefficient, int sampleRate)
    {
        if (sampleRate == ReferenceSampleRate) return coefficient;
        return 1f - MathF.Pow(1f - coefficient, ReferenceSampleRate / (float)sampleRate);
    }

    /// <summary>Preserves the steady-state variance of filtered white noise when its pole is retimed.</summary>
    private static float OnePoleNoiseGain(float reference, float actual)
    {
        return MathF.Sqrt(reference / (2f - reference) / (actual / (2f - actual)));
    }

    /// <summary>Fits the longest chorus tap (below 32 ms), keeping the original minimum capacity.</summary>
    private static int DelayCapacity(int sampleRate)
    {
        uint required = (uint)Math.Ceiling(32.0 * sampleRate / 1000) + 2;
        return (int)BitOperations.RoundUpToPowerOf2(Math.Max(4096u, required));
    }

    /// <summary>Runs the authored pink-noise filter at the reference clock, interpolating between ticks.</summary>
    private float NextReferencePink()
    {
        if (_sampleRate == ReferenceSampleRate) return _noise.NextPink();
        _pinkClockPhase += ReferenceSampleRate;
        while (_pinkClockPhase >= _sampleRate)
        {
            _pinkClockPhase -= _sampleRate;
            _pinkPrevious = _pinkCurrent;
            _pinkCurrent = _noise.NextPink();
        }
        return Dsp.Lerp(_pinkPrevious, _pinkCurrent, _pinkClockPhase * _inverseSampleRate);
    }

    /// <summary>Softens the hiss mid-band without changing voice or grain, with a Nyquist-safe center.</summary>
    private static BiQuadFilter CreateTapeHissColor(int sampleRate)
    {
        return BiQuadFilter.PeakingEQ(sampleRate, Math.Min(4500f, sampleRate * 0.45f), 1f, -2f);
    }

    /// <summary>Creates the broad enclosure resonance shared by playback and noise calibration.</summary>
    private static BiQuadFilter CreateTapeResonance(int sampleRate)
    {
        return BiQuadFilter.PeakingEQ(sampleRate, Math.Min(700f, sampleRate * 0.45f), 1f, 1.5f);
    }

    /// <summary>Measures linear noise-path energy once at setup, including the speaker blend and DC blocker.</summary>
    private static NoiseEnergy TapeNoiseEnergy(int sampleRate, bool hiss, bool colorHiss = false)
    {
        float Safe(float hz) => Math.Min(hz, sampleRate * 0.45f);
        var bump = BiQuadFilter.PeakingEQ(sampleRate, Safe(120f), 0.8f, 4f);
        var resonance = CreateTapeResonance(sampleRate);
        var hp = BiQuadFilter.HighPassFilter(sampleRate, Safe(70f), 0.707f);
        var lp = BiQuadFilter.LowPassFilter(sampleRate, Safe(7500f), 0.707f);
        var dryDc = new DcBlocker(sampleRate);
        var wetDc = new DcBlocker(sampleRate);
        float lowCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(HissHighPassHz) / sampleRate);
        float highCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(HissLowPassHz) / sampleRate);
        var color = hiss && colorHiss ? CreateTapeHissColor(sampleRate) : null;
        float low = 0, high = 0;
        double dryEnergy = 0, crossEnergy = 0, wetEnergy = 0;
        for (int i = 0; i < 1024; i++)
        {
            float value = i == 0 ? 1f : 0f;
            if (hiss)
            {
                low += lowCoeff * (value - low);
                high += highCoeff * (value - low - high);
                value = color?.Transform(high) ?? high;
            }
            float colored = lp.Transform(hp.Transform(resonance.Transform(bump.Transform(value))));
            float dry = dryDc.Process(value);
            float wet = wetDc.Process(colored);
            dryEnergy += (double)dry * dry;
            crossEnergy += (double)dry * wet;
            wetEnergy += (double)wet * wet;
        }
        return new NoiseEnergy(dryEnergy, crossEnergy, wetEnergy);
    }

    // =========================================================================
    // EFFECT ALGORITHMS
    // =========================================================================

    /// <summary>Generates fixed full-strength PCM, then mixes and matches the supplied buffer.</summary>
    private void ApplyPowerMatched(Span<float> buffer, VoiceEffectType type, float amount)
    {
        if (type == VoiceEffectType.FmRadio) _fmAmount = amount;
        if (buffer.IsEmpty) return;
        if (amount >= 1f)
        {
            ProcessPowerMatchedWet(buffer, type);
            return;
        }

        float[] rented = ArrayPool<float>.Shared.Rent(buffer.Length);
        try
        {
            Span<float> dry = rented.AsSpan(0, buffer.Length);
            ProcessPowerMatchedWet(buffer, type, dry);
            PowerMatchedMixer.Mix(dry, buffer, amount);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    /// <summary>Selects the fixed processor once per buffer, outside the sample loop.</summary>
    private void ProcessPowerMatchedWet(Span<float> buffer, VoiceEffectType type, Span<float> dry = default)
    {
        if (type == VoiceEffectType.FmRadio)
        {
            ProcessFmWet(buffer, dry);
            return;
        }
        if (!dry.IsEmpty) buffer.CopyTo(dry);
        if (type == VoiceEffectType.Telephone)
        {
            ProcessTelephoneWet(buffer);
            return;
        }
        if (type == VoiceEffectType.TacticalRadio)
        {
            ProcessTacticalWet(buffer);
            return;
        }
        ProcessG711Wet(buffer);
    }

    /// <summary>Generates the fixed radio endpoint and its equally delayed clean reference.</summary>
    private void ProcessFmWet(Span<float> buffer, Span<float> dry)
    {
        if (dry.IsEmpty)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = ProcessFmWet(buffer[i], out _);
            return;
        }
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = ProcessFmWet(buffer[i], out float delayed);
            dry[i] = delayed;
        }
    }

    /// <summary>Retains the accepted full-strength filters, noise, saturation and DC-removal state.</summary>
    private void ProcessTelephoneWet(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];
            float filtered = Dsp.KillDenormal(dry);
            _thermal.Update(ref _noise, _thermalDriftCoeff);
            _thermalValue = _thermal.State * _thermalNoiseGain;
            for (int f = 0; f < _filterCount; f++) filtered = _filters[f].Transform(filtered);
            float wet = _dcBlocker.Process(Telephone(filtered));
            // Preserve the accepted full-output floating-point expression, including signed zero.
            buffer[i] = dry * 0f + wet;
        }
    }

    /// <summary>
    /// Simulates the characteristic distortion and line noise of a POTS telephone connection.
    /// Pink noise is thermally modulated to replicate the unpredictable hiss of aging hardware.
    /// Returns a pure wet signal.
    /// </summary>
    private float Telephone(float x)
    {
        const float drive = 3.5f;
        float noise = NextReferencePink() * (0.003f + _thermalValue * 0.002f + 0.018f);
        float wet = Dsp.AsymmetricSaturation(x * drive + noise);
        return wet / drive;
    }

    /// <summary>
    /// Simulates the warm harmonic saturation of an analog tube or transistor overdrive stage.
    /// Thermal bias drift replicates the slow operating-point shift of a warming tube amplifier.
    /// Returns a pure wet signal.
    /// </summary>
    private float Overdrive(float x)
    {
        float bias = _thermalValue * _drive.BiasScale;
        float wet = Dsp.AsymmetricSaturation((x * _drive.Drive) + bias);
        return wet * _drive.Trim;
    }

    /// <summary>Prepares gradual saturation and rate-aware filters only when intensity changes.</summary>
    private void PrepareDrive(float amount)
    {
        if (_drive.PreparedAmount == amount) return;
        _drive.PreparedAmount = amount;
        float depth = Math.Clamp(amount, 0f, 1f);
        float strength = depth * (2f - depth);
        const float fullDrive = 1f + 4.808956f;
        float fullMakeup = MathF.Sqrt(fullDrive);
        _drive.Drive = fullDrive * strength;
        // S(d*x)/d approaches identity. Preserve the original multiplication at full strength.
        _drive.Trim = depth >= 1f ? 1f / fullMakeup
            : Dsp.Lerp(1f, fullMakeup, strength) / _drive.Drive;
        _drive.BiasScale = .06f * strength * strength;
        float trim = depth >= 1f ? OverdriveOutputGain : Dsp.Lerp(1f, OverdriveOutputGain, depth);
        _drive.Gain = trim * Dsp.DriveGain(depth);

        float fullHighPass = Math.Min(180f, _sampleRate * .45f);
        float hpCutoff = fullHighPass * strength;
        float hpFloor = Math.Min(fullHighPass, _sampleRate * .0001f);
        _drive.HighPassBlend = Math.Min(1f, hpCutoff / hpFloor);
        _filters[0].SetHighPassFilter(_sampleRate, Math.Max(hpCutoff, hpFloor), .8f);

        float nyquist = _sampleRate * .5f;
        float fullLowPass = Math.Min(ReferenceSampleRate * .45f, _sampleRate * .45f);
        float lpCutoff = depth >= 1f ? fullLowPass : nyquist * MathF.Pow(fullLowPass / nyquist, strength);
        float lpGapFloor = _sampleRate * .0001f;
        _drive.LowPassBlend = Math.Min(1f, (nyquist - lpCutoff) / lpGapFloor);
        // Avoid float feedback instability at DC and Nyquist; introduce the tiny boundary region before saturation.
        _filters[1].SetLowPassFilter(_sampleRate, Math.Min(lpCutoff, nyquist - lpGapFloor), .707f);
        _dcBlocker.SetCutoff(35f * strength);
    }

    /// <summary>Runs intrinsic degradation once, then matches mean-square power over the supplied buffer.</summary>
    private void ApplyBitcrusher(Span<float> buffer, float amount)
    {
        PrepareBitcrusher(amount);
        if (buffer.IsEmpty) return;
        BiQuadFilter highPass = _filters[0];
        double referenceEnergy = 0, processedEnergy = 0;
        bool hasQuantizedSignal = false;
        for (int i = 0; i < buffer.Length; i++)
        {
            float reference = buffer[i];
            float filtered = Dsp.KillDenormal(reference);
            _thermal.Update(ref _noise, _thermalDriftCoeff);
            _thermalValue = _thermal.State * _thermalNoiseGain;
            float quantized = Bitcrusher(highPass.Transform(filtered), amount);
            hasQuantizedSignal |= quantized != 0f;
            float processed = _dcBlocker.Process(quantized);
            referenceEnergy += reference * (double)reference;
            processedEnergy += processed * (double)processed;
            buffer[i] = processed;
        }
        // A decaying DC-filter tail is not recoverable voice when the quantizer emitted only zeros.
        if (!hasQuantizedSignal)
        {
            for (int i = 0; i < buffer.Length; i++) buffer[i] = (float)(buffer[i] * _bc.Gain);
            return;
        }
        // The bound prevents rejected bands or rare quantizer codes from becoming excessive makeup gain.
        _bc.Gain = PowerMatcher.MatchRamped(buffer, referenceEnergy / buffer.Length,
            processedEnergy / buffer.Length, _bc.Gain, _bc.GainRampSamples, CrusherMaximumLevelGain).Gain;
    }

    /// <summary>
    /// Simulates the true aliasing and quantization artifacts of a lo-fi bitcrusher.
    /// Strictly relies on Zero-Order Hold (decimation) and amplitude quantization.
    /// The metallic character arises naturally from foldover frequencies and staircase waveforms.
    /// Bit depth and the decimation rate vary continuously.
    /// Silence remains exactly zero; full intensity retains the signed 4-bit / 16-level signal.
    /// </summary>
    private float Bitcrusher(float x, float amount)
    {
        float jitter = _thermalValue * 0.005f * amount;
        float step = Math.Min(_bc.ClockStep * (1f + jitter), 1f);

        _bc.Phase += step;

        if (_bc.Phase >= 1f)
        {
            _bc.Phase -= 1f;

            int quantized = Math.Clamp(
                (int)MathF.Round(Math.Clamp(x, -1f, 1f) * _bc.Scale),
                _bc.MinValue,
                _bc.MaxValue);

            _bc.Hold = quantized * _bc.InverseScale;
        }

        return _bc.Hold;
    }

    /// <summary>Prepares carrier depth, gradual filtering and the initial linear-carrier power estimate.</summary>
    private void PrepareRing(float amount)
    {
        if (_ring.PreparedAmount == amount) return;
        _ring.PreparedAmount = amount;
        float depth = Math.Clamp(amount, 0f, 1f);
        _ring.Depth = depth;
        _ring.Makeup = 1f + .8f * depth;
        _ring.SaturationDrive = depth;
        _ring.InverseDrive = 1f / depth;
        double carrierPower = (1d - depth) * (1d - depth) + .5 * depth * depth;
        _ring.Level!.Reset(1d / (_ring.Makeup * Math.Sqrt(carrierPower)));
        float fullCutoff = Math.Min(130f, _sampleRate * .45f);
        float cutoff = fullCutoff * depth;
        float minimumCutoff = Math.Min(fullCutoff, _sampleRate * .0001f);
        _ring.FilterBlend = Math.Min(1f, cutoff / minimumCutoff);
        _filters[0].SetHighPassFilter(_sampleRate, Math.Max(cutoff, minimumCutoff), .707f);
        _dcBlocker.SetCutoff(35f * depth);
    }

    /// <summary>
    /// Modulates one signal without an outer clean branch. Half depth retains a unipolar tremolo
    /// carrier; greater depths introduce polarity reversal and reach the accepted full ring character.
    /// </summary>
    private float RingMod(float x)
    {
        float freq = 30f + _thermalValue * 1.5f;
        _mod.RingPhase = Dsp.AdvancePhase(_mod.RingPhase, freq, _sampleRate);
        float carrier = Dsp.Lerp(1f, Dsp.Sine(_mod.RingPhase), _ring.Depth);
        return Dsp.ModulationSaturate(x * carrier * _ring.Makeup, _ring.SaturationDrive, _ring.InverseDrive);
    }

    /// <summary>Prepares the complete modulation path only when intensity changes.</summary>
    private void PrepareModulation(VoiceEffectType type, float amount)
    {
        if (_mod.PreparedAmount == amount) return;
        _mod.PreparedAmount = amount;
        float strength = Dsp.ModulationStrength(amount);
        _mod.Strength = strength;
        _mod.SaturationDrive = strength;
        _mod.InverseDrive = 1f / strength;
        _mod.FullStrength = amount >= 1f;
        bool chorus = type == VoiceEffectType.Chorus;
        _mod.Gain = chorus ? Dsp.ChorusGain(amount) : Dsp.FlangerGain(amount);
        // Keep the authored full delay; intensity controls modulation depth and delayed level.
        float baseStrength = amount >= 1f ? strength : 1f;
        _mod.Base1 = .1f + (chorus ? 14.9f : 1.7f) * baseStrength;
        _mod.Base2 = .1f + 23.9f * baseStrength;
        _mod.Depth1 = (chorus ? 6f : 1.1f) * strength;
        _mod.Depth2 = 7f * strength;
        _mod.WetScale = (chorus ? .45f : .72f) * strength;
        _mod.Feedback = .75f * strength;
    }

    /// <summary>Runs the complete flanger, including its feedback, DC removal and output scalar.</summary>
    private float Flanger(float dry)
    {
        _mod.FlangerPhase += _mod.FlangerPhaseInc;
        if (_mod.FlangerPhase >= 2f * MathF.PI) _mod.FlangerPhase -= 2f * MathF.PI;
        float delayMs = _mod.Base1 + Dsp.Sine(_mod.FlangerPhase) * _mod.Depth1;
        float delayed = _delay.Read(Math.Max(0f, delayMs * _msToSamples + _preWriteReadOffset));
        float feedbackInput = Dsp.KillDenormal(dry) + delayed * _mod.Feedback;
        _delay.Write(Dsp.ModulationSaturate(feedbackInput, _mod.SaturationDrive, _mod.InverseDrive));
        return FinishModulation(dry, _dcBlocker.Process(delayed * _mod.WetScale));
    }

    /// <summary>Runs the complete two-voice chorus without an additional outer crossfade.</summary>
    private float Chorus(float dry)
    {
        _mod.ChorusPhase += _mod.ChorusPhaseInc;
        _mod.ChorusPhase2 += _mod.ChorusPhaseInc2;
        if (_mod.ChorusPhase >= 2f * MathF.PI) _mod.ChorusPhase -= 2f * MathF.PI;
        if (_mod.ChorusPhase2 >= 2f * MathF.PI) _mod.ChorusPhase2 -= 2f * MathF.PI;
        float wow = _thermalValue * .006f * _mod.Strength;
        float d1 = _mod.Base1 + Dsp.Sine(_mod.ChorusPhase) * _mod.Depth1 + wow;
        float d2 = _mod.Base2 + Dsp.Sine(_mod.ChorusPhase2) * _mod.Depth2 - wow;
        float s1 = _delay.Read(Math.Max(0f, d1 * _msToSamples + _preWriteReadOffset));
        float s2 = _delay.Read(Math.Max(0f, d2 * _msToSamples + _preWriteReadOffset));
        _delay.Write(Dsp.KillDenormal(dry));
        return FinishModulation(dry, _dcBlocker.Process((s1 + s2) * _mod.WetScale));
    }

    /// <summary>Adds the delayed component and applies gradual saturation followed by static gain.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float FinishModulation(float dry, float wet)
    {
        float shaped = Dsp.ModulationSaturate(dry + wet * .85f, _mod.SaturationDrive, _mod.InverseDrive);
        // Preserve the accepted full-strength expression's final floating-point rounding.
        if (_mod.FullStrength) shaped = dry + (shaped - dry);
        return shaped * _mod.Gain;
    }

    /// <summary>Runs cassette voice correction before independently colored background noise is added.</summary>
    private void ApplyTape(Span<float> buffer, float amount)
    {
        PrepareTape(amount);
        _tape.Level.SetAmount(amount);
        for (int i = 0; i < buffer.Length; i++)
        {
            _thermal.Update(ref _noise, _thermalDriftCoeff);
            _thermalValue = _thermal.State * _thermalNoiseGain;
            float voice = LoFiTape(Dsp.KillDenormal(buffer[i]), amount);
            buffer[i] = _tape.Level.Process(voice, _tape.ReferenceSample) + _tape.NoiseSample;
        }
    }

    /// <summary>Runs record, transport and playback voice stages; exposes independent playback noise.</summary>
    private float LoFiTape(float x, float amount)
    {
        // Rumble filter: cuts subsonic noise before the saturation stage.
        float hpf = _tape.PreHp != null ? _tape.PreHp.Transform(x) : x;
        float input = Dsp.Lerp(x, hpf, amount);

        // Moderate HF pre-emphasis feeds the saturation stage; it is not a standalone de-esser.
        _tape.PreState += _tape.PreCoeff * (input - _tape.PreState);
        _tape.PreState = Dsp.KillDenormal(_tape.PreState);
        float preEmph = input + (input - _tape.PreState) * _tape.PreEmphScale;

        // Tape saturation: magnetic oxide overdrive.
        float sat = Dsp.SoftClip(preEmph * _tape.SatDrive);
        float saturated = Dsp.Lerp(preEmph, sat * _tape.SatCompensation, _tape.CharacterAmount);

        // Tape/record-level compression: broadband dynamics for cassette-like density.
        // This is not a Dolby noise-reduction encoder/decoder model.
        float comp = _tape.Compressor.Process(saturated, _tape.CompThreshold, 3.5f,
                                  _tape.CompAttackCoeff, _tape.CompReleaseCoeff);

        // Mild record-stage trim complements the amplitude compressor.
        float recorded = Dsp.Lerp(saturated, comp * _tape.CompMakeup, _tape.CharacterAmount);

        _delay.Write(recorded);
        _tape.ReferenceDelay!.Write(input);

        // Wow and Flutter: pitch instability from motor defect and tape friction.
        // Phase advanced by pre-computed increments: no per-sample division.
        _tape.LofiPhase += _tape.LofiPhaseInc;
        _tape.LofiPhase2 += _tape.LofiPhaseInc2;
        _tape.FlutterPhase += _tape.FlutterPhaseInc;
        if (_tape.LofiPhase >= 2f * MathF.PI) _tape.LofiPhase -= 2f * MathF.PI;
        if (_tape.LofiPhase2 >= 2f * MathF.PI) _tape.LofiPhase2 -= 2f * MathF.PI;
        if (_tape.FlutterPhase >= 2f * MathF.PI) _tape.FlutterPhase -= 2f * MathF.PI;

        float wow = (Dsp.Sine(_tape.LofiPhase) * 0.8f + Dsp.Sine(_tape.LofiPhase2) * 0.4f) * amount;

        float flutterNoise = _noise.NextWhite();
        _tape.FlutterNoiseState += _tape.FlutterNoiseCoeff * (flutterNoise * _tape.FlutterNoiseGain - _tape.FlutterNoiseState);
        _tape.FlutterNoiseState = Dsp.KillDenormal(_tape.FlutterNoiseState);

        float flutter = Dsp.Sine(_tape.FlutterPhase) * (0.15f * amount)
                      + _tape.FlutterNoiseState * _tape.FlutterNoiseScale;

        float modulatedDelayMs = MathF.Max(0f, _tape.DelayMs + wow + flutter);
        float pitchWarped = _delay.Read(modulatedDelayMs * _msToSamples);

        // Dropout: stochastic volume dip simulating oxide shedding.
        float dropped = _tape.DropOut.Process(pitchWarped, amount, ref _noise, _sampleRate, _tapeSlew);
        _tape.ReferenceSample = _tape.ReferenceDelay!.Read(modulatedDelayMs * _msToSamples)
            * _tape.DropOut.Gain;

        // De-emphasis: restores spectral balance after playback head.
        _tape.DeState += _tape.DeCoeff * (dropped - _tape.DeState);
        _tape.DeState = Dsp.KillDenormal(_tape.DeState);
        float hfCut = (dropped - _tape.DeState) * _tape.HfCutScale;
        float deEmph = dropped - hfCut;

        // Azimuth drift: dynamic HF loss from head misalignment.
        _azimuth.Phase += _tape.AzimuthPhaseInc;
        if (_azimuth.Phase >= 2f * MathF.PI) _azimuth.Phase -= 2f * MathF.PI;

        float azimuthMod = 0.5f + 0.5f * Dsp.Sine(_azimuth.Phase);
        float currentAzimuthCoeff = _azimuth.Curve.At(azimuthMod * amount);

        _azimuth.FilterState += currentAzimuthCoeff * (deEmph - _azimuth.FilterState);
        _azimuth.FilterState = Dsp.KillDenormal(_azimuth.FilterState);

        float azimuthSignal = Dsp.Lerp(deEmph, _azimuth.FilterState, _tape.AzimuthMix);

        // Oxide grain and hiss approximate the additive cassette/playback noise floor.
        // This audio-band texture does not simulate the ultrasonic record-bias oscillator.
        // Hiss is shaped into an upper-band "shhh" rather than full-band digital white noise.
        float biasGrain = _noise.NextWhite() * _tape.BiasGrainScale;
        float rawHiss = _noise.NextWhite() * (_tape.HissAmount
            + _thermalValue * 0.003f * HissLevel * _tape.HissGain * _tape.ThermalHissScale);

        _tape.HissLowState += _tape.HissLowCoeff * (rawHiss - _tape.HissLowState);
        _tape.HissLowState = Dsp.KillDenormal(_tape.HissLowState);
        float highPassedHiss = rawHiss - _tape.HissLowState;

        _tape.HissState += _tape.HissCoeff * (highPassedHiss - _tape.HissState);
        _tape.HissState = Dsp.KillDenormal(_tape.HissState);

        float coloredHiss = _tape.HissColor?.Transform(_tape.HissState) ?? _tape.HissState;
        float background = biasGrain + coloredHiss * amount;
        float tapeSignal = azimuthSignal;

        // Speaker simulation: cheap plastic enclosure coloring (quadratic blend).
        if (_tape.BoomboxBump != null && _tape.BoomboxResonance != null
            && _tape.BoomboxHp != null && _tape.BoomboxLp != null)
        {
            float bumped = _tape.BoomboxBump.Transform(tapeSignal);
            bumped = _tape.BoomboxResonance.Transform(bumped);
            float filtered = _tape.BoomboxLp.Transform(_tape.BoomboxHp.Transform(bumped));
            tapeSignal = Dsp.Lerp(tapeSignal, filtered, _tape.SpeakerMix);
        }

        float trimmed = tapeSignal * _tape.PerceptualTrim;
        float playback = Dsp.Lerp(trimmed, Dsp.SoftClip(trimmed), amount);
        float dcFiltered = _dcBlocker.Process(playback);

        float output = _tape.CharacterAmount >= 1f ? dcFiltered
            : Dsp.Lerp(playback, dcFiltered, _tape.CharacterAmount);

        // Playback electronics color the additive floor without voice-driven saturation or gain.
        float coloredNoise = _tape.NoiseLp.Transform(_tape.NoiseHp.Transform(
            _tape.NoiseResonance.Transform(_tape.NoiseBump.Transform(background))));
        background = Dsp.Lerp(background, coloredNoise, _tape.SpeakerMix) * _tape.PerceptualTrim;
        float noiseDc = _tape.NoiseDc.Process(background);
        _tape.NoiseSample = (_tape.CharacterAmount >= 1f ? noiseDc
            : Dsp.Lerp(background, noiseDc, _tape.CharacterAmount)) * _tape.OutputGain;
        return output * _tape.OutputGain;
    }

    /// <summary>
    /// Simulates a VoIP packet loss or AI decoder failure.
    /// Prefers sustained, low-ZCR sounds using a simple energy detector.
    /// Repeats a region of the continuously written capture buffer; new audio can replace it mid-stutter.
    /// Capture duration and ZCR units retain the original 22050 Hz character at other sample rates.
    /// </summary>
    private float DecoderGlitch(float x, float amount)
    {
        // ---------------------------------------------------------------------
        // 1. Capture Buffer: Always save the latest audio for a potential loop.
        // ---------------------------------------------------------------------
        _glitchCapture[_glitch.WritePos] = x;
        if (++_glitch.WritePos >= _glitchCapture.Length) _glitch.WritePos = 0;

        // ---------------------------------------------------------------------
        // 2. Vowel Detector: Find loud and stable sounds using Energy and ZCR.
        // ---------------------------------------------------------------------
        float xSq = x * x;
        _glitch.Energy += (xSq > _glitch.Energy ? _glitchAtt : _glitchRel) * (xSq - _glitch.Energy);

        if ((x >= 0f) != (_glitch.PrevSample >= 0f)) _glitch.ZcrCount++;
        _glitch.PrevSample = x;

        // Check the detector every 10 milliseconds
        if (--_glitch.TriggerWindowTimer <= 0)
        {
            float rawZcr = (float)_glitch.ZcrCount / _glitchWindowSamples * _sampleRateScale;
            _glitch.Zcr = 0.85f * _glitch.Zcr + 0.15f * rawZcr;
            _glitch.ZcrCount = 0;
            _glitch.TriggerWindowTimer = _glitchWindowSamples;

            // If not currently glitching, cooldown is over, and we found a loud vowel:
            if (!_glitch.Frozen && _glitch.Cooldown <= 0
                && _glitch.Energy > 0.004f
                && _glitch.Zcr < 0.08f)
            {
                // -------------------------------------------------------------
                // 3. Trigger Logic: Pseudo-random chance to start a glitch.
                // -------------------------------------------------------------
                float roll = _noise.NextWhite() + 0.5f;
                float currentChance = 0.01f + _glitch.TriggerAccumulator;

                if (roll < currentChance)
                {
                    // Glitch triggered successfully
                    _glitch.Frozen = true;
                    _glitch.TriggerAccumulator = 0f;

                    // ---------------------------------------------------------
                    // 4. Glitch Parameters: Setup loop size and duration.
                    // ---------------------------------------------------------

                    // Total duration: Scales up to 600ms based on amount.
                    float durationMs = _glitch.SqrtAmount * 600f;
                    _glitch.Remain = (int)(durationMs * _sampleRate / 1000f);

                    // Loop size (stutter speed): From 5ms (high pitch) to 33ms (classic robot).
                    const float kneeAmount = 0.1f;
                    const float loopMin = 5f;
                    const float loopMax = 33f;

                    float loopMs = amount < kneeAmount
                        ? loopMin + (loopMax - loopMin) * (amount / kneeAmount)
                        : loopMax;

                    // Safety: Make sure the loop fits at least twice inside the duration.
                    loopMs = Math.Min(loopMs, Math.Max(2f, durationMs * 0.5f));

                    // Select the rolling capture region; writing continues during playback.
                    _glitch.LoopLen = Math.Max(1, (int)(loopMs * _sampleRate / 1000f));
                    _glitch.LoopStart = _glitch.WritePos - _glitch.LoopLen;
                    if (_glitch.LoopStart < 0) _glitch.LoopStart += _glitchCapture.Length;
                    _glitch.PlayPos = 0;

                    // Cooldown: Prevent new glitches for a short time.
                    float amountSq = amount * amount;
                    float cooldownMs = 200f + (200f * (1f - amountSq));
                    _glitch.Cooldown = _glitch.Remain + (int)(cooldownMs * _sampleRate / 1000f);
                }
                else
                {
                    // Accumulate chance: Guarantees a glitch will eventually happen on long sounds.
                    _glitch.TriggerAccumulator += amount * amount * amount * 0.001f;
                }
            }
        }

        if (_glitch.Cooldown > 0) _glitch.Cooldown--;

        // If no glitch is active, return the normal live audio
        if (!_glitch.Frozen) return x;

        // ---------------------------------------------------------------------
        // 5. Frozen Playback: Play the looped audio buffer.
        // ---------------------------------------------------------------------
        if (--_glitch.Remain <= 0)
        {
            _glitch.Frozen = false;
            return x;
        }

        int captureIndex = _glitch.LoopStart + _glitch.PlayPos;
        if (captureIndex >= _glitchCapture.Length) captureIndex -= _glitchCapture.Length;
        float frozen = _glitchCapture[captureIndex];

        // Increase saturation based on amount to simulate a broken decoder
        frozen = Dsp.SoftClip(frozen * Dsp.Lerp(1.0f, 1.15f, amount));

        // Apply a 2ms Hann window fade at the edges of the loop to prevent audio clicks
        int targetFade = _sampleRate * 2 / 1000;
        int fadeLen = Math.Min(targetFade, _glitch.LoopLen / 2);

        if (fadeLen > 0)
        {
            int samplesLeft = _glitch.LoopLen - _glitch.PlayPos;
            if (samplesLeft < fadeLen)
                frozen *= Dsp.HannWindow(samplesLeft, fadeLen * 2);
            else if (_glitch.PlayPos < fadeLen)
                frozen *= Dsp.HannWindow(_glitch.PlayPos, fadeLen * 2);
        }

        _glitch.PlayPos = (_glitch.PlayPos + 1) % _glitch.LoopLen;

        // Final release fade (25ms): Smoothly crossfade back to live audio at the end of the glitch
        int releaseSamples = Math.Min(_sampleRate * 25 / 1000, _glitch.LoopLen);
        if (_glitch.Remain < releaseSamples && releaseSamples > 0)
        {
            float tFade = (float)_glitch.Remain / releaseSamples;
            frozen = frozen * tFade + x * (1f - tFade);
        }

        return frozen;
    }

    /// <summary>
    /// Tactical / military radio: author's perceptual model of SINCGARS / AN/PRC-class voice.
    /// All CVSD timing constants are pre-computed in Setup: zero MathF.Exp in the hot path.
    /// The step size adapts to codec history; request intensity is handled by PowerMatchedMixer.
    /// </summary>
    private float TacticalRadio(float x)
    {
        float absX = MathF.Abs(x);

        // Compander: two-state gain for intelligibility under noise.
        _radio.TacticalEnvelope += (absX > _radio.TacticalEnvelope ? _envAtt : _envRel)
                                   * (absX - _radio.TacticalEnvelope);
        _radio.TacticalEnvelope = Dsp.KillDenormal(_radio.TacticalEnvelope);

        float companderGain = _radio.TacticalEnvelope < 0.1f ? 1.15f : 0.92f;
        float companded = x * companderGain;

        // Amplitude-domain compression with 1 ms attack / 80 ms release; ratio is not a dB slope.
        float clipped = Dsp.SoftClip(companded * 2.0f);
        float compressed = _radio.TacticalCompressor.Process(
            clipped, 0.25f, 4f,
            _radio.CompAttackCoeff,
            _radio.CompReleaseCoeff);

        float rfSignal = compressed * 1.5f;

        // The entire codec advances at the reference clock, including its feedback and decay.
        // Below that rate, several virtual ticks share the available host input sample.
        _radio.CvsdClockPhase += ReferenceSampleRate;
        while (_radio.CvsdClockPhase >= _sampleRate)
        {
            _radio.CvsdClockPhase -= _sampleRate;
            _radio.CvsdPrevious = _radio.ReconState;
            bool bit = rfSignal > _radio.ReconState;
            _radio.CvsdHistory = ((_radio.CvsdHistory << 1) | (bit ? 1u : 0u)) & 0x0Fu;
            if (_radio.CvsdHistory == 0x0Fu || _radio.CvsdHistory == 0x00u)
                _radio.CvsdStepSize += _radio.StepGrow;
            else
                _radio.CvsdStepSize *= _radio.StepDecay;
            _radio.CvsdStepSize = Math.Clamp(_radio.CvsdStepSize, _radio.MinStep, _radio.MaxStep);
            _radio.CvsdAccumulator += bit ? _radio.CvsdStepSize : -_radio.CvsdStepSize;
            _radio.CvsdAccumulator *= _radio.AccumDecay;
            _radio.CvsdAccumulator = Math.Clamp(_radio.CvsdAccumulator, -1.2f, 1.2f);
            _radio.CvsdAccumulator = Dsp.KillDenormal(_radio.CvsdAccumulator);
            _radio.ReconState += _radio.ReconCoeff * (_radio.CvsdAccumulator - _radio.ReconState);
            _radio.ReconState = Dsp.KillDenormal(_radio.ReconState);
        }
        float reconstructed = _sampleRate == ReferenceSampleRate ? _radio.ReconState
            : Dsp.Lerp(_radio.CvsdPrevious, _radio.ReconState, _radio.CvsdClockPhase * _inverseSampleRate);

        // AsymmetricSaturation adds odd-harmonic gravel of an overdriven RF stage.
        const float outputTrim = 0.75f;
        float saturated = Dsp.AsymmetricSaturation(reconstructed * 1.4f) / 1.4f;
        return Dsp.SoftClip(saturated * outputTrim);
    }

    /// <summary>Runs the accepted full TacticalRadio input, adaptive codec and playback trim.</summary>
    private void ProcessTacticalWet(Span<float> buffer)
    {
        int filterCount = _filterCount;
        BiQuadFilter[] filters = _filters;
        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];
            float filtered = Dsp.KillDenormal(dry);
            _thermal.Update(ref _noise, _thermalDriftCoeff);
            _thermalValue = _thermal.State * _thermalNoiseGain;
            for (int f = 0; f < filterCount; f++) filtered = filters[f].Transform(filtered);
            // Preserve the full-strength input rounding: the CVSD feedback is sensitive to it.
            float wet = _dcBlocker.Process(TacticalRadio(Dsp.Lerp(dry, filtered, 1f)));
            buffer[i] = dry * 0f + wet * TacticalOutputGain;
        }
    }

    /// <summary>
    /// ITU-T G.711 PCM codec simulation.
    /// Converts normalized float PCM to signed 16-bit linear PCM, runs a real 8-bit
    /// segmented mu-law or A-law encode/decode round-trip, then converts back to float.
    /// The 300-3400 Hz telephone bandwidth is applied by Setup before this stage.
    /// Intensity is handled exclusively by PowerMatchedMixer, so the codec itself always
    /// has the same transfer characteristic and does not act as a volume control.
    /// </summary>
    private float G711Codec(float x)
    {
        float input = Math.Clamp(x, -1.0f, 1.0f);

        // Preserve the full signed PCM16 range: -1.0 -> -32768, +1.0 -> +32767.
        int pcmInt = input >= 0f
            ? (int)MathF.Round(input * 32767f)
            : (int)MathF.Round(input * 32768f);

        short pcm = (short)Math.Clamp(pcmInt, short.MinValue, short.MaxValue);
        short decoded = _radio.G711ALaw
            ? DecodeALaw(EncodeALaw(pcm))
            : DecodeMuLaw(EncodeMuLaw(pcm));

        // Standard signed PCM16 normalization. No makeup gain or extra saturation here:
        // final server volume/limiting is intentionally handled later in the DSP pipeline.
        return decoded / 32768f;
    }

    /// <summary>Retains the full codec filters, state and accepted endpoint rounding.</summary>
    private void ProcessG711Wet(Span<float> buffer)
    {
        int filterCount = _filterCount;
        BiQuadFilter[] filters = _filters;
        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];
            float filtered = Dsp.KillDenormal(dry);
            _thermal.Update(ref _noise, _thermalDriftCoeff);
            _thermalValue = _thermal.State * _thermalNoiseGain;
            for (int f = 0; f < filterCount; f++) filtered = filters[f].Transform(filtered);
            float wet = _dcBlocker.Process(G711Codec(filtered));
            // Keep the original full-effect floating-point expression at every intensity.
            buffer[i] = Dsp.Lerp(dry, wet, 1f);
        }
    }

    /// <summary>Finds the magnitude segment used by G.711 A-law companding.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindALawSegment(int value)
    {
        if (value <= 0x01F) return 0;
        if (value <= 0x03F) return 1;
        if (value <= 0x07F) return 2;
        if (value <= 0x0FF) return 3;
        if (value <= 0x1FF) return 4;
        if (value <= 0x3FF) return 5;
        if (value <= 0x7FF) return 6;
        if (value <= 0xFFF) return 7;
        return 8;
    }

    /// <summary>Finds the magnitude segment used by G.711 mu-law companding.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindMuLawSegment(int value)
    {
        if (value <= 0x03F) return 0;
        if (value <= 0x07F) return 1;
        if (value <= 0x0FF) return 2;
        if (value <= 0x1FF) return 3;
        if (value <= 0x3FF) return 4;
        if (value <= 0x7FF) return 5;
        if (value <= 0xFFF) return 6;
        if (value <= 0x1FFF) return 7;
        return 8;
    }

    /// <summary>Encode 16-bit linear PCM to an 8-bit ITU-T G.711 A-law codeword.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte EncodeALaw(short pcmValue)
    {
        int pcm = pcmValue >> 3;
        int mask;

        if (pcm >= 0)
        {
            mask = 0xD5;
        }
        else
        {
            mask = 0x55;
            pcm = -pcm - 1;
        }

        int segment = FindALawSegment(pcm);
        if (segment >= 8)
            return (byte)(0x7F ^ mask);

        int value = segment << 4;
        value |= segment < 2
            ? (pcm >> 1) & 0x0F
            : (pcm >> segment) & 0x0F;

        return (byte)(value ^ mask);
    }

    /// <summary>Decode an 8-bit ITU-T G.711 A-law codeword to 16-bit linear PCM.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short DecodeALaw(byte value)
    {
        int a = value ^ 0x55;
        int t = (a & 0x0F) << 4;
        int segment = (a & 0x70) >> 4;

        switch (segment)
        {
            case 0:
                t += 8;
                break;
            case 1:
                t += 0x108;
                break;
            default:
                t += 0x108;
                t <<= segment - 1;
                break;
        }

        return (short)((a & 0x80) != 0 ? t : -t);
    }

    /// <summary>Encode 16-bit linear PCM to an 8-bit ITU-T G.711 mu-law codeword.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte EncodeMuLaw(short pcmValue)
    {
        const int Bias = 0x84;
        const int Clip = 8159;

        int pcm = pcmValue >> 2;
        int mask;

        if (pcm < 0)
        {
            // One's-complement magnitude retains the ITU-T G.711 negative decision boundaries.
            pcm = -pcm - 1;
            mask = 0x7F;
        }
        else
        {
            mask = 0xFF;
        }

        if (pcm > Clip)
            pcm = Clip;

        pcm += Bias >> 2;

        int segment = FindMuLawSegment(pcm);
        if (segment >= 8)
            return (byte)(0x7F ^ mask);

        int value = (segment << 4) | ((pcm >> (segment + 1)) & 0x0F);
        return (byte)(value ^ mask);
    }

    /// <summary>Decode an 8-bit ITU-T G.711 mu-law codeword to 16-bit linear PCM.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short DecodeMuLaw(byte value)
    {
        const int Bias = 0x84;

        int u = (~value) & 0xFF;
        int t = ((u & 0x0F) << 3) + Bias;
        t <<= (u & 0x70) >> 4;

        return (short)((u & 0x80) != 0
            ? Bias - t
            : t - Bias);
    }

}
