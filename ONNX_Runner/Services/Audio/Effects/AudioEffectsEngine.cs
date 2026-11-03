using ONNX_Runner.Services.Audio;
using ONNX_Runner.Services.Audio.Levels;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Models.Configuration;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Buffers;
using NAudio.Dsp;
using Profiles = ONNX_Runner.Services.Audio.Effects.AudioEffectProfiles;
using RoutingMode = ONNX_Runner.Services.Audio.Effects.AudioEffectProfiles.RoutingMode;

namespace ONNX_Runner.Services.Audio.Effects;

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
public class AudioEffectsEngine(EffectsSettings config, int sampleRate) : IDisposable
{
    private readonly EffectsSettings _config = config;
    private readonly int _sampleRate = sampleRate > 0
        ? sampleRate : throw new ArgumentOutOfRangeException(nameof(sampleRate));
    private readonly float _sampleRateScale = sampleRate / (float)Profiles.Common.ReferenceSampleRate;
    private readonly float _msToSamples = sampleRate * 0.001f;
    private readonly float _inverseSampleRate = 1f / sampleRate;
    // Read-before-write taps include one causal sample. Keep its reference duration at other rates.
    private readonly float _preWriteReadOffset = sampleRate / (float)Profiles.Common.ReferenceSampleRate - 1f;

    // Reference coefficients retain the previous attack/release times at 22050 Hz.
    private readonly float _envAtt = ScaleReferenceCoefficient(Profiles.Common.EnvelopeAttack, sampleRate);
    private readonly float _envRel = ScaleReferenceCoefficient(Profiles.Common.EnvelopeRelease, sampleRate);
    private readonly float _glitchAtt = ScaleReferenceCoefficient(Profiles.DecoderGlitch.AttackCoefficient, sampleRate);
    private readonly float _glitchRel = ScaleReferenceCoefficient(Profiles.DecoderGlitch.ReleaseCoefficient, sampleRate);
    private readonly float _tapeSlew = ScaleReferenceCoefficient(Profiles.Tape.SlewCoefficient, sampleRate);
    private readonly float _thermalDriftCoeff = ScaleReferenceCoefficient(Profiles.Common.ThermalCoefficient, sampleRate);
    private readonly float _thermalNoiseGain = OnePoleNoiseGain(Profiles.Common.ThermalCoefficient,
        ScaleReferenceCoefficient(Profiles.Common.ThermalCoefficient, sampleRate));
    private float _thermalValue;
    private int _pinkClockPhase;
    private float _pinkPrevious, _pinkCurrent;

    // Energy of unit white noise before/after the cassette speaker blend, including DC removal.
    private readonly record struct NoiseEnergy(double Dry, double Cross, double Wet)
    {
        public double At(float mix) => Dry * (1 - mix) * (1 - mix)
            + 2 * Cross * mix * (1 - mix) + Wet * mix * mix;
    }
    private static readonly NoiseEnergy ReferenceHissEnergy = TapeNoiseEnergy(Profiles.Common.ReferenceSampleRate, true);
    private static readonly NoiseEnergy ReferenceGrainEnergy = TapeNoiseEnergy(Profiles.Common.ReferenceSampleRate, false);

    // =========================================================================
    // EFFECT STATE STRUCTURES (Zero-Allocation)
    // =========================================================================

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
        public WindowedPowerMatcher? Level;
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
        public WindowedPowerMatcher? Level;
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

    private DelayBuffer? _delay;
    private DcBlocker _dcBlocker = new(sampleRate);
    private NoiseGenerator _noise = new();
    private ThermalDrift _thermal = new();

    private readonly BiQuadFilter[] _filters = new BiQuadFilter[5];
    private int _filterCount;
    private HarmonizerProcessor? _harmonizer;
    private RobotProcessor? _robot;
    private bool _disposed;
    private FmRadioProcessor? _fmProcessor;
    private float _fmAmount;
    private StreamingPowerMatchedMixer? _powerMixer;
    private VoiceEffectType _current = VoiceEffectType.None;

    private float[] _glitchCapture = [];
    private readonly int _glitchWindowSamples = Math.Max(1, sampleRate / Profiles.DecoderGlitch.DetectorWindowsPerSecond);


    // =========================================================================
    // EFFECT STATE INSTANCES
    // =========================================================================

    private ModulationState _mod;
    private RingState _ring;
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        _robot?.Dispose();
        _robot = null;
        _harmonizer?.Dispose();
        _harmonizer = null;
        _delay?.Clear();
        _dcBlocker.Reset();
        _fmProcessor?.Reset();
        _fmAmount = 0f;
        _powerMixer?.Reset();
        _bc.Level?.Reset();

        Array.Clear(_filters, 0, _filters.Length);
        _filterCount = 0;

        uint seed = fixedSeed ?? (uint)(DateTime.UtcNow.Ticks % uint.MaxValue);
        _noise.Seed(seed);
        _noise.Reset();
        _thermal.Reset();

        _current = VoiceEffectType.None;

        _mod = default;
        _ring.PreparedAmount = float.NaN;

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
    internal bool HasPendingOutput => _current == VoiceEffectType.Harmonizer
        ? _harmonizer?.PendingSamples > 0
        : _current == VoiceEffectType.Robot ? _robot?.PendingSamples > 0
        : _current == VoiceEffectType.FmRadio && _fmProcessor != null &&
        (_fmProcessor.PendingSamples > 0 || _dcBlocker.PendingMagnitude * Profiles.FmRadio.OutputGain *
            (_fmAmount < 1f ? Math.Max(1, _fmAmount * StreamingPowerMatchedMixer.MaximumGain) : 1) > Profiles.Common.CompletionSilenceFloor);

    /// <summary>Writes retained effect audio only; repeated calls stop when its filters are drained.</summary>
    internal int Complete(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current == VoiceEffectType.Harmonizer) return _harmonizer?.Complete(destination) ?? 0;
        if (_current == VoiceEffectType.Robot) return _robot?.Complete(destination) ?? 0;
        if (destination.IsEmpty || !HasPendingOutput) return 0;
        if (_fmAmount >= 1f) return CompleteFmWet(destination, Span<float>.Empty);
        _powerMixer ??= new StreamingPowerMatchedMixer(_sampleRate);

        float[] rented = ArrayPool<float>.Shared.Rent(destination.Length);
        try
        {
            Span<float> dry = rented.AsSpan(0, destination.Length);
            int written = CompleteFmWet(destination, dry);
            _powerMixer!.Mix(dry[..written], destination[..written], _fmAmount);
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
        return dry * 0f + wet * Profiles.FmRadio.OutputGain;
    }

    /// <summary>
    /// Applies the specified audio effect to the input buffer in-place.
    /// The amount parameter controls the intensity of the effect (0 = no effect, 1 = full effect).
    /// External blends retain the request amount; most wet algorithms receive an eased strength.
    /// FM, Telephone, G.711 and TacticalRadio keep fixed wet settings while intensity controls their blends.
    /// Returns the ready prefix length; buffered effects retain priming audio until completion.
    /// Optimized for real-time processing with zero managed allocations after preparation.
    /// </summary>
    public int ApplyEffect(Span<float> buffer, VoiceEffectType type, float amount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_config.EnableGlobalEffects || type == VoiceEffectType.None)
            return buffer.Length;

        // Fast-path bypass for CPU savings in server environments.
        // Keep the guard to skip DSP work and preserve bit-identical bypass, including warm state.
        if (amount <= 0.001f)
            return buffer.Length;

        if (_current != type)
        {
            Setup(type);
            _delay?.Clear();
            _dcBlocker.Reset();
            _current = type;
        }

        if (type == VoiceEffectType.Harmonizer)
        {
            if (buffer.IsEmpty) return 0;
            _harmonizer ??= new HarmonizerProcessor(_sampleRate);
            return _harmonizer.Process(buffer, amount);
        }

        if (type == VoiceEffectType.Robot)
        {
            if (buffer.IsEmpty) return 0;
            _robot ??= new RobotProcessor(_sampleRate);
            return _robot.Process(buffer, amount);
        }

        RoutingMode mode = Profiles.GetRoutingMode(type);
        if (mode == RoutingMode.PowerMatchedCrossfade)
        {
            ApplyPowerMatched(buffer, type, amount);
            return buffer.Length;
        }

        if (mode == RoutingMode.TapeInsert)
        {
            ApplyTape(buffer, amount);
            return buffer.Length;
        }

        if (mode == RoutingMode.CrusherInsert)
        {
            ApplyBitcrusher(buffer, amount);
            return buffer.Length;
        }

        if (mode == RoutingMode.ModulationInsert)
        {
            PrepareModulation(type, amount);
            bool chorus = type == VoiceEffectType.Chorus;
            WindowedPowerMatcher level = _mod.Level!;
            for (int i = 0; i < buffer.Length; i++)
            {
                _thermal.Update(ref _noise, _thermalDriftCoeff);
                _thermalValue = _thermal.State * _thermalNoiseGain;
                float reference = buffer[i];
                float processed = chorus ? Chorus(reference) : Flanger(reference);
                buffer[i] = level.Process(reference, processed);
            }
            return buffer.Length;
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
            return buffer.Length;
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
        return buffer.Length;
    }

    // =========================================================================
    // HOST DISPATCH & SETUP
    // =========================================================================

    /// <summary>
    /// Configures effect-specific filters and coefficients that remain constant for the effect type.
    /// Per-buffer constants that depend on amount are prepared in ApplyEffect before the sample loop.
    /// </summary>
    private void Setup(VoiceEffectType type)
    {
        _robot?.Dispose();
        _robot = null;
        _harmonizer?.Dispose();
        _harmonizer = null;
        _filterCount = 0;
        _powerMixer?.Reset();
        _dcBlocker.SetCutoff(Profiles.Common.DcCutoffHz);

        float nyq = _sampleRate * Profiles.Common.NyquistFactor;
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
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(Profiles.Telephone.HighPassHz), Profiles.Telephone.BandPassQ);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(Profiles.Telephone.BodyPeakHz), Profiles.Telephone.BodyPeakQ, Profiles.Telephone.BodyPeakGainDb);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(Profiles.Telephone.CavityPeakHz), Profiles.Telephone.CavityPeakQ, Profiles.Telephone.CavityPeakGainDb);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(Profiles.Telephone.PresenceHz), Profiles.Telephone.PresenceQ, Profiles.Telephone.PresenceGainDb);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(Profiles.Telephone.LowPassHz), Profiles.Telephone.BandPassQ);
                break;

            case VoiceEffectType.Overdrive:
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(Profiles.Overdrive.HighPassHz), Profiles.Overdrive.HighPassQ);
                // Keep the reference top cut in Hz; low-rate models still obey the Nyquist cap.
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(Profiles.Common.ReferenceSampleRate * Profiles.Common.NyquistFactor), Profiles.Overdrive.LowPassQ);
                break;

            case VoiceEffectType.Bitcrusher:
                _bc.PreparedAmount = float.NaN;
                _bc.Level ??= new WindowedPowerMatcher(_sampleRate,
                    maximumGain: Profiles.Bitcrusher.MaximumLevelGain, minimumPower: Profiles.Bitcrusher.MinimumPower);
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(Profiles.Bitcrusher.HighPassHz), Profiles.Bitcrusher.HighPassQ);
                break;

            case VoiceEffectType.RingModulator:
                _ring.PreparedAmount = float.NaN;
                // Average speech-level changes without following the 30 Hz modulation.
                _ring.Level ??= new WindowedPowerMatcher(_sampleRate, windowMs: Profiles.RingModulator.LevelWindowMs, maximumRiseDbPerSecond: Profiles.RingModulator.MaximumRiseDbPerSecond);
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(Profiles.RingModulator.HighPassHz), Profiles.RingModulator.HighPassQ);
                break;

            case VoiceEffectType.Flanger:
                _delay ??= new DelayBuffer(DelayCapacity(_sampleRate));
                _mod.PreparedAmount = float.NaN;
                // Average one LFO cycle without tracking its intended level movement.
                _mod.Level = new WindowedPowerMatcher(_sampleRate, windowMs: 1000f / Profiles.Flanger.LfoHz);
                _mod.FlangerPhaseInc = PhaseInc(Profiles.Flanger.LfoHz);
                break;

            case VoiceEffectType.Chorus:
                _delay ??= new DelayBuffer(DelayCapacity(_sampleRate));
                _mod.PreparedAmount = float.NaN;
                // Two slow-voice cycles also span approximately three faster-voice cycles.
                _mod.Level = new WindowedPowerMatcher(_sampleRate, windowMs: 2000f / Profiles.Chorus.LfoHz);
                _mod.ChorusPhaseInc = PhaseInc(Profiles.Chorus.LfoHz);
                _mod.ChorusPhaseInc2 = PhaseInc(Profiles.Chorus.SecondLfoHz);
                break;

            case VoiceEffectType.LoFiTape:
                _delay ??= new DelayBuffer(DelayCapacity(_sampleRate));
                _tape.PreHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(Profiles.Tape.HighPassHz), Profiles.Tape.HighPassQ);

                // Approximate Type-I cassette HF EQ corner derived from the 120 us time constant
                // (~1326 Hz). This is a perceptual pre/de-emphasis stage, not a full IEC replay curve.
                float iecFc = Profiles.Tape.EqualizationHz;
                _tape.PreCoeff = 1f - MathF.Exp(-2f * MathF.PI * iecFc / _sampleRate);
                _tape.DeCoeff = _tape.PreCoeff;

                // Cassette hiss is concentrated toward the upper spectrum rather than being
                // full-band white noise. Remove low-frequency noise below ~1.4 kHz, then
                // gently roll off the extreme top above ~9 kHz to keep the hiss analog/soft.
                _tape.HissLowCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(Profiles.Tape.HissHighPassHz) / _sampleRate);
                _tape.HissCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(Profiles.Tape.HissLowPassHz) / _sampleRate);

                // Band-limit the random component of flutter so delay modulation changes
                // smoothly instead of jumping independently on every sample.
                _tape.FlutterNoiseCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(Profiles.Tape.FlutterNoiseHz) / _sampleRate);
                float referenceFlutterCoeff = 1f - MathF.Exp(-2f * MathF.PI * Profiles.Tape.FlutterNoiseHz / Profiles.Common.ReferenceSampleRate);
                _tape.FlutterNoiseGain = OnePoleNoiseGain(referenceFlutterCoeff, _tape.FlutterNoiseCoeff);
                _tape.HissColor = CreateTapeHissColor(_sampleRate);
                _tape.HissEnergy = TapeNoiseEnergy(_sampleRate, true, true);
                _tape.GrainEnergy = TapeNoiseEnergy(_sampleRate, false);

                _tape.PreState = _tape.DeState = 0f;
                _tape.HissLowState = _tape.HissState = 0f;
                _tape.FlutterNoiseState = 0f;

                _tape.DropOut.Reset();
                _tape.Compressor.Reset();
                _tape.CompAttackCoeff = FeedForwardCompressor.TimeToCoeff(Profiles.Tape.CompressorAttackMs, _sampleRate);
                _tape.CompReleaseCoeff = FeedForwardCompressor.TimeToCoeff(Profiles.Tape.CompressorReleaseMs, _sampleRate);

                float referenceHigh = 1f - MathF.Exp(-2f * MathF.PI * (Profiles.Common.ReferenceSampleRate * Profiles.Common.NyquistFactor) / Profiles.Common.ReferenceSampleRate);
                float referenceLow = 1f - MathF.Exp(-2f * MathF.PI * Profiles.Tape.AzimuthLowPassHz / Profiles.Common.ReferenceSampleRate);
                _azimuth.Curve = Dsp.MatchOnePoleResponse(_sampleRate, Profiles.Common.ReferenceSampleRate, referenceHigh, referenceLow, Profiles.Tape.AzimuthLowPassHz);
                _azimuth.Phase = 0f;
                _azimuth.FilterState = 0f;

                _tape.BoomboxBump = BiQuadFilter.PeakingEQ(_sampleRate, Safe(Profiles.Tape.SpeakerBumpHz), Profiles.Tape.SpeakerBumpQ, Profiles.Tape.SpeakerBumpGainDb);
                _tape.BoomboxResonance = CreateTapeResonance(_sampleRate);
                _tape.BoomboxHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(Profiles.Tape.SpeakerHighPassHz), Profiles.Tape.SpeakerHighPassQ);
                _tape.BoomboxLp = BiQuadFilter.LowPassFilter(_sampleRate, Safe(Profiles.Tape.SpeakerLowPassHz), Profiles.Tape.SpeakerLowPassQ);
                // Independent filter histories keep playback hiss outside voice level correction.
                _tape.NoiseBump = TapeNoiseFilter.Peak(_sampleRate, Safe(Profiles.Tape.SpeakerBumpHz), Profiles.Tape.SpeakerBumpQ, Profiles.Tape.SpeakerBumpGainDb);
                _tape.NoiseResonance = TapeNoiseFilter.Peak(_sampleRate, Safe(Profiles.Tape.SpeakerResonanceHz), Profiles.Tape.SpeakerResonanceQ, Profiles.Tape.SpeakerResonanceGainDb);
                _tape.NoiseHp = TapeNoiseFilter.HighPass(_sampleRate, Safe(Profiles.Tape.SpeakerHighPassHz), Profiles.Tape.SpeakerHighPassQ);
                _tape.NoiseLp = TapeNoiseFilter.LowPass(_sampleRate, Safe(Profiles.Tape.SpeakerLowPassHz), Profiles.Tape.SpeakerLowPassQ);
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
                _tape.LofiPhaseInc = PhaseInc(Profiles.Tape.WowHz);
                _tape.LofiPhaseInc2 = PhaseInc(Profiles.Tape.SecondWowHz);
                _tape.FlutterPhaseInc = PhaseInc(Profiles.Tape.FlutterHz);
                _tape.AzimuthPhaseInc = PhaseInc(Profiles.Tape.AzimuthHz);
                break;

            case VoiceEffectType.DecoderGlitch:
                // Preserve the reference capture duration and its original ring arithmetic.
                if (_glitchCapture.Length == 0)
                    _glitchCapture = new float[Math.Max(1, (int)Math.Round(
                        Profiles.DecoderGlitch.ReferenceCaptureSamples * (double)_sampleRate / Profiles.Common.ReferenceSampleRate))];
                _glitch = default;
                _glitch.TriggerWindowTimer = _glitchWindowSamples; // Approximately 10 ms.
                break;

            case VoiceEffectType.TacticalRadio:
                // Tactical / military radio: author's perceptual model of SINCGARS / AN/PRC-class voice.
                // All CVSD timing constants pre-computed here; zero MathF.Exp in the sample loop.
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(Profiles.TacticalRadio.HighPassHz), Profiles.TacticalRadio.BandPassQ);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(Profiles.TacticalRadio.FirstPeakHz), Profiles.TacticalRadio.FirstPeakQ, Profiles.TacticalRadio.FirstPeakGainDb);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(Profiles.TacticalRadio.SecondPeakHz), Profiles.TacticalRadio.SecondPeakQ, Profiles.TacticalRadio.SecondPeakGainDb);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(Profiles.TacticalRadio.ThirdPeakHz), Profiles.TacticalRadio.ThirdPeakQ, Profiles.TacticalRadio.ThirdPeakGainDb);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(Profiles.TacticalRadio.LowPassHz), Profiles.TacticalRadio.BandPassQ);

                _radio = default;
                _radio.TacticalCompressor.Reset();

                _radio.CompAttackCoeff = FeedForwardCompressor.TimeToCoeff(Profiles.TacticalRadio.CompressorAttackMs, _sampleRate);
                _radio.CompReleaseCoeff = FeedForwardCompressor.TimeToCoeff(Profiles.TacticalRadio.CompressorReleaseMs, _sampleRate);

                _radio.ReconCoeff = 1f - MathF.Exp(-2f * MathF.PI * Profiles.TacticalRadio.ReconstructionHz / Profiles.Common.ReferenceSampleRate);
                _radio.AccumDecay = MathF.Exp(-1f / (Profiles.TacticalRadio.AccumulatorDecaySeconds * Profiles.Common.ReferenceSampleRate));
                _radio.StepDecay = MathF.Exp(-1f / (Profiles.TacticalRadio.StepDecaySeconds * Profiles.Common.ReferenceSampleRate));

                // A fixed virtual bit clock keeps the four-bit history window and adaptation
                // independent of the host rate. These are the existing 22050 Hz step sizes.
                _radio.MinStep = Profiles.TacticalRadio.MinimumStep;
                _radio.MaxStep = Profiles.TacticalRadio.MaximumStep;
                _radio.StepGrow = Profiles.TacticalRadio.StepGrowth;

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
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(Profiles.G711.HighPassHz), Profiles.G711.BandPassQ);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(Profiles.G711.LowPassHz), Profiles.G711.BandPassQ);

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
        _bc.Level!.Reset();
        float strength = Dsp.CrushStrength(amount);
        float bits = Math.Clamp(Profiles.Bitcrusher.MaximumBits - amount * Profiles.Bitcrusher.BitReduction, Profiles.Bitcrusher.MinimumBits, Profiles.Bitcrusher.MaximumBits);
        _bc.Scale = MathF.Pow(2f, bits - 1f);
        _bc.InverseScale = 1f / _bc.Scale;
        // Fractional depths retain uniform integer codes within the signed PCM range.
        // Integer depths preserve the existing negative rail and one-code-short positive rail.
        _bc.MinValue = -(int)MathF.Floor(_bc.Scale);
        _bc.MaxValue = (int)MathF.Ceiling(_bc.Scale) - 1;
        float fullClockStep = Math.Min(1f, Profiles.Bitcrusher.FullClockHz / _sampleRate);
        _bc.ClockStep = MathF.Pow(fullClockStep, Dsp.CrushClock(amount));
        _filters[0].SetHighPassFilter(_sampleRate, Math.Min(Profiles.Bitcrusher.HighPassHz * strength, _sampleRate * Profiles.Common.NyquistFactor), Profiles.Bitcrusher.HighPassQ);
        _dcBlocker.SetCutoff(Profiles.Common.DcCutoffHz * strength);
    }

    /// <summary>Prepares cassette component amounts and playback trim once per buffer.</summary>
    private void PrepareTape(float amount)
    {
        float amountSq = amount * amount;
        // Quadratic ease-out has finite initial slope; sqrt(amount) would exaggerate tiny settings.
        _tape.CharacterAmount = amount * (2f - amount);
        float character = _tape.CharacterAmount;
        _tape.PreEmphScale = Profiles.Tape.PreEmphasisScale * character;
        _tape.SatDrive = 1f + Profiles.Tape.SaturationDrive * character;
        _tape.SatCompensation = 1f / (1f + Profiles.Tape.SaturationCompensation * character);
        _tape.CompThreshold = Dsp.Lerp(1.0f, Profiles.Tape.CompressorThreshold, character);
        _tape.CompMakeup = Dsp.Lerp(1f, Profiles.Tape.CompressorMakeup, character);
        _tape.DelayMs = Profiles.Tape.CharacterDelayMs * _tape.CharacterAmount + Profiles.Tape.WearDelayMs * amount;
        _tape.HfCutScale = Profiles.Tape.HighFrequencyCut * character;
        _tape.AzimuthMix = amountSq * Profiles.Tape.AzimuthMix;
        _tape.HissGain = (float)Math.Sqrt(ReferenceHissEnergy.At(amountSq) / _tape.HissEnergy.At(amountSq));
        float grainGain = _sampleRate == Profiles.Common.ReferenceSampleRate ? 1f
            : (float)Math.Sqrt(ReferenceGrainEnergy.At(amountSq) / _tape.GrainEnergy.At(amountSq));
        _tape.BiasGrainScale = Profiles.Tape.GrainScale * amount * grainGain;
        // Factor the final amount out of the noise filter so its warm history also fades to zero.
        _tape.HissAmount = (Profiles.Tape.BaseHiss + Profiles.Tape.WearHiss * amount) * Profiles.Tape.HissLevel * _tape.HissGain;
        // The final output amount yields CharacterAmount without division near zero.
        _tape.ThermalHissScale = 2f - amount;
        _tape.FlutterNoiseScale = Profiles.Tape.FlutterNoiseScale * amount;
        _tape.SpeakerMix = amountSq;
        _tape.PerceptualTrim = 1f - Profiles.Tape.PerceptualTrim * amountSq;
        // Retain the authored playback/noise trim while record character now develops earlier.
        // The voice-only detector corrects residual level differences independently of this floor.
        float nominalRecordGain = (1f + Profiles.Tape.SaturationDrive * amount) / (1f + Profiles.Tape.SaturationCompensation * amount);
        float saturationLift = amount * (nominalRecordGain - 1f);
        _tape.OutputGain = 1f / (1f + Profiles.Tape.PlaybackCompensation * saturationLift);
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
        if (sampleRate == Profiles.Common.ReferenceSampleRate) return coefficient;
        return 1f - MathF.Pow(1f - coefficient, Profiles.Common.ReferenceSampleRate / (float)sampleRate);
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
        if (_sampleRate == Profiles.Common.ReferenceSampleRate) return _noise.NextPink();
        _pinkClockPhase += Profiles.Common.ReferenceSampleRate;
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
        return BiQuadFilter.PeakingEQ(sampleRate, Math.Min(Profiles.Tape.HissColorHz, sampleRate * Profiles.Common.NyquistFactor), Profiles.Tape.HissColorQ, Profiles.Tape.HissColorGainDb);
    }

    /// <summary>Creates the broad enclosure resonance shared by playback and noise calibration.</summary>
    private static BiQuadFilter CreateTapeResonance(int sampleRate)
    {
        return BiQuadFilter.PeakingEQ(sampleRate, Math.Min(Profiles.Tape.SpeakerResonanceHz, sampleRate * Profiles.Common.NyquistFactor), Profiles.Tape.SpeakerResonanceQ, Profiles.Tape.SpeakerResonanceGainDb);
    }

    /// <summary>Measures linear noise-path energy once at setup, including the speaker blend and DC blocker.</summary>
    private static NoiseEnergy TapeNoiseEnergy(int sampleRate, bool hiss, bool colorHiss = false)
    {
        float Safe(float hz) => Math.Min(hz, sampleRate * Profiles.Common.NyquistFactor);
        var bump = BiQuadFilter.PeakingEQ(sampleRate, Safe(Profiles.Tape.SpeakerBumpHz), Profiles.Tape.SpeakerBumpQ, Profiles.Tape.SpeakerBumpGainDb);
        var resonance = CreateTapeResonance(sampleRate);
        var hp = BiQuadFilter.HighPassFilter(sampleRate, Safe(Profiles.Tape.SpeakerHighPassHz), Profiles.Tape.SpeakerHighPassQ);
        var lp = BiQuadFilter.LowPassFilter(sampleRate, Safe(Profiles.Tape.SpeakerLowPassHz), Profiles.Tape.SpeakerLowPassQ);
        var dryDc = new DcBlocker(sampleRate);
        var wetDc = new DcBlocker(sampleRate);
        float lowCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(Profiles.Tape.HissHighPassHz) / sampleRate);
        float highCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(Profiles.Tape.HissLowPassHz) / sampleRate);
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
        _powerMixer?.Prepare(amount);
        if (buffer.IsEmpty) return;
        if (amount >= 1f)
        {
            ProcessPowerMatchedWet(buffer, type);
            return;
        }

        _powerMixer ??= new StreamingPowerMatchedMixer(_sampleRate);
        float[] rented = ArrayPool<float>.Shared.Rent(buffer.Length);
        try
        {
            Span<float> dry = rented.AsSpan(0, buffer.Length);
            ProcessPowerMatchedWet(buffer, type, dry);
            _powerMixer.Mix(dry, buffer, amount);
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
        if (type == VoiceEffectType.Overdrive)
        {
            ProcessOverdriveWet(buffer);
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
        const float drive = Profiles.Telephone.Drive;
        float noise = NextReferencePink() * (Profiles.Telephone.BaseNoise + _thermalValue * Profiles.Telephone.ThermalNoise + Profiles.Telephone.LineNoise);
        float wet = Dsp.AsymmetricSaturation(x * drive + noise);
        return wet / drive;
    }

    /// <summary>Retains the full-strength saturation, filters, thermal bias and output trim.</summary>
    private void ProcessOverdriveWet(Span<float> buffer)
    {
        BiQuadFilter highPass = _filters[0], lowPass = _filters[1];
        for (int i = 0; i < buffer.Length; i++)
        {
            _thermal.Update(ref _noise, _thermalDriftCoeff);
            _thermalValue = _thermal.State * _thermalNoiseGain;
            float input = Dsp.KillDenormal(buffer[i]);
            float filtered = lowPass.Transform(highPass.Transform(input));
            float bias = _thermalValue * Profiles.Overdrive.ThermalBias;
            float wet = Dsp.AsymmetricSaturation(filtered * Profiles.Overdrive.Drive + bias) * Profiles.Overdrive.Trim;
            buffer[i] = _dcBlocker.Process(wet) * Profiles.Overdrive.OutputGain;
        }
    }

    /// <summary>Runs intrinsic degradation and causal gain correction on one fixed sample clock.</summary>
    private void ApplyBitcrusher(Span<float> buffer, float amount)
    {
        PrepareBitcrusher(amount);
        BiQuadFilter highPass = _filters[0];
        WindowedPowerMatcher level = _bc.Level!;
        for (int i = 0; i < buffer.Length; i++)
        {
            float reference = buffer[i];
            float filtered = Dsp.KillDenormal(reference);
            _thermal.Update(ref _noise, _thermalDriftCoeff);
            _thermalValue = _thermal.State * _thermalNoiseGain;
            float quantized = Bitcrusher(highPass.Transform(filtered), amount);
            float processed = _dcBlocker.Process(quantized);
            buffer[i] = level.Process(reference, processed, quantized != 0f);
        }
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
        float jitter = _thermalValue * Profiles.Bitcrusher.ThermalJitter * amount;
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
        _ring.Makeup = 1f + Profiles.RingModulator.MakeupDepth * depth;
        _ring.SaturationDrive = depth;
        _ring.InverseDrive = 1f / depth;
        double carrierPower = (1d - depth) * (1d - depth) + .5 * depth * depth;
        _ring.Level!.Reset(1d / (_ring.Makeup * Math.Sqrt(carrierPower)));
        float fullCutoff = Math.Min(Profiles.RingModulator.HighPassHz, _sampleRate * Profiles.Common.NyquistFactor);
        float cutoff = fullCutoff * depth;
        float minimumCutoff = Math.Min(fullCutoff, _sampleRate * .0001f);
        _ring.FilterBlend = Math.Min(1f, cutoff / minimumCutoff);
        _filters[0].SetHighPassFilter(_sampleRate, Math.Max(cutoff, minimumCutoff), Profiles.RingModulator.HighPassQ);
        _dcBlocker.SetCutoff(Profiles.Common.DcCutoffHz * depth);
    }

    /// <summary>
    /// Modulates one signal without an outer clean branch. Half depth retains a unipolar tremolo
    /// carrier; greater depths introduce polarity reversal and reach the accepted full ring character.
    /// </summary>
    private float RingMod(float x)
    {
        float freq = Profiles.RingModulator.CarrierHz + _thermalValue * Profiles.RingModulator.ThermalPitchHz;
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
        // A new intensity must not inherit the previous intensity's makeup gain.
        _mod.Level!.Reset();
        // Keep the authored full delay; intensity controls modulation depth and delayed level.
        float baseStrength = amount >= 1f ? strength : 1f;
        _mod.Base1 = Profiles.Modulation.DelayOffsetMs + (chorus ? Profiles.Chorus.BaseDelayMs : Profiles.Flanger.BaseDelayMs) * baseStrength;
        _mod.Base2 = Profiles.Modulation.DelayOffsetMs + Profiles.Chorus.SecondBaseDelayMs * baseStrength;
        _mod.Depth1 = (chorus ? Profiles.Chorus.DepthMs : Profiles.Flanger.DepthMs) * strength;
        _mod.Depth2 = Profiles.Chorus.SecondDepthMs * strength;
        _mod.WetScale = (chorus ? Profiles.Chorus.WetScale : Profiles.Flanger.WetScale) * strength;
        _mod.Feedback = Profiles.Flanger.Feedback * strength;
    }

    /// <summary>Runs the complete flanger, including its feedback and DC removal.</summary>
    private float Flanger(float dry)
    {
        _mod.FlangerPhase += _mod.FlangerPhaseInc;
        if (_mod.FlangerPhase >= 2f * MathF.PI) _mod.FlangerPhase -= 2f * MathF.PI;
        float delayMs = _mod.Base1 + Dsp.Sine(_mod.FlangerPhase) * _mod.Depth1;
        float delayed = _delay!.Read(Math.Max(0f, delayMs * _msToSamples + _preWriteReadOffset));
        float feedbackInput = Dsp.KillDenormal(dry) + delayed * _mod.Feedback;
        _delay!.Write(Dsp.ModulationSaturate(feedbackInput, _mod.SaturationDrive, _mod.InverseDrive));
        return FinishModulation(dry, _dcBlocker.Process(delayed * _mod.WetScale));
    }

    /// <summary>Runs the complete two-voice chorus without an additional outer crossfade.</summary>
    private float Chorus(float dry)
    {
        _mod.ChorusPhase += _mod.ChorusPhaseInc;
        _mod.ChorusPhase2 += _mod.ChorusPhaseInc2;
        if (_mod.ChorusPhase >= 2f * MathF.PI) _mod.ChorusPhase -= 2f * MathF.PI;
        if (_mod.ChorusPhase2 >= 2f * MathF.PI) _mod.ChorusPhase2 -= 2f * MathF.PI;
        float wow = _thermalValue * Profiles.Chorus.ThermalDelayMs * _mod.Strength;
        float d1 = _mod.Base1 + Dsp.Sine(_mod.ChorusPhase) * _mod.Depth1 + wow;
        float d2 = _mod.Base2 + Dsp.Sine(_mod.ChorusPhase2) * _mod.Depth2 - wow;
        float s1 = _delay!.Read(Math.Max(0f, d1 * _msToSamples + _preWriteReadOffset));
        float s2 = _delay!.Read(Math.Max(0f, d2 * _msToSamples + _preWriteReadOffset));
        _delay!.Write(Dsp.KillDenormal(dry));
        return FinishModulation(dry, _dcBlocker.Process((s1 + s2) * _mod.WetScale));
    }

    /// <summary>Adds the delayed component and applies gradual saturation before output power matching.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float FinishModulation(float dry, float wet)
    {
        float shaped = Dsp.ModulationSaturate(dry + wet * Profiles.Modulation.OutputWetScale, _mod.SaturationDrive, _mod.InverseDrive);
        // Preserve the accepted full-strength expression's final floating-point rounding.
        if (_mod.FullStrength) shaped = dry + (shaped - dry);
        return shaped;
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
        float comp = _tape.Compressor.Process(saturated, _tape.CompThreshold, Profiles.Tape.CompressorRatio,
                                  _tape.CompAttackCoeff, _tape.CompReleaseCoeff);

        // Mild record-stage trim complements the amplitude compressor.
        float recorded = Dsp.Lerp(saturated, comp * _tape.CompMakeup, _tape.CharacterAmount);

        _delay!.Write(recorded);
        _tape.ReferenceDelay!.Write(input);

        // Wow and Flutter: pitch instability from motor defect and tape friction.
        // Phase advanced by pre-computed increments: no per-sample division.
        _tape.LofiPhase += _tape.LofiPhaseInc;
        _tape.LofiPhase2 += _tape.LofiPhaseInc2;
        _tape.FlutterPhase += _tape.FlutterPhaseInc;
        if (_tape.LofiPhase >= 2f * MathF.PI) _tape.LofiPhase -= 2f * MathF.PI;
        if (_tape.LofiPhase2 >= 2f * MathF.PI) _tape.LofiPhase2 -= 2f * MathF.PI;
        if (_tape.FlutterPhase >= 2f * MathF.PI) _tape.FlutterPhase -= 2f * MathF.PI;

        float wow = (Dsp.Sine(_tape.LofiPhase) * Profiles.Tape.WowDepthMs + Dsp.Sine(_tape.LofiPhase2) * Profiles.Tape.SecondWowDepthMs) * amount;

        float flutterNoise = _noise.NextWhite();
        _tape.FlutterNoiseState += _tape.FlutterNoiseCoeff * (flutterNoise * _tape.FlutterNoiseGain - _tape.FlutterNoiseState);
        _tape.FlutterNoiseState = Dsp.KillDenormal(_tape.FlutterNoiseState);

        float flutter = Dsp.Sine(_tape.FlutterPhase) * (Profiles.Tape.FlutterDepthMs * amount)
                      + _tape.FlutterNoiseState * _tape.FlutterNoiseScale;

        float modulatedDelayMs = MathF.Max(0f, _tape.DelayMs + wow + flutter);
        float pitchWarped = _delay!.Read(modulatedDelayMs * _msToSamples);

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
            + _thermalValue * Profiles.Tape.ThermalHiss * Profiles.Tape.HissLevel * _tape.HissGain * _tape.ThermalHissScale);

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
            _glitch.Zcr = Profiles.DecoderGlitch.ZcrMemory * _glitch.Zcr + Profiles.DecoderGlitch.ZcrInputWeight * rawZcr;
            _glitch.ZcrCount = 0;
            _glitch.TriggerWindowTimer = _glitchWindowSamples;

            // If not currently glitching, cooldown is over, and we found a loud vowel:
            if (!_glitch.Frozen && _glitch.Cooldown <= 0
                && _glitch.Energy > Profiles.DecoderGlitch.MinimumEnergy
                && _glitch.Zcr < Profiles.DecoderGlitch.MaximumZcr)
            {
                // -------------------------------------------------------------
                // 3. Trigger Logic: Pseudo-random chance to start a glitch.
                // -------------------------------------------------------------
                float roll = _noise.NextWhite() + 0.5f;
                float currentChance = Profiles.DecoderGlitch.BaseChance + _glitch.TriggerAccumulator;

                if (roll < currentChance)
                {
                    // Glitch triggered successfully
                    _glitch.Frozen = true;
                    _glitch.TriggerAccumulator = 0f;

                    // ---------------------------------------------------------
                    // 4. Glitch Parameters: Setup loop size and duration.
                    // ---------------------------------------------------------

                    // Total duration: Scales up to 600ms based on amount.
                    float durationMs = _glitch.SqrtAmount * Profiles.DecoderGlitch.DurationMs;
                    _glitch.Remain = (int)(durationMs * _sampleRate / 1000f);

                    // Loop size (stutter speed): From 5ms (high pitch) to 33ms (classic robot).
                    const float kneeAmount = Profiles.DecoderGlitch.KneeAmount;
                    const float loopMin = Profiles.DecoderGlitch.MinimumLoopMs;
                    const float loopMax = Profiles.DecoderGlitch.MaximumLoopMs;

                    float loopMs = amount < kneeAmount
                        ? loopMin + (loopMax - loopMin) * (amount / kneeAmount)
                        : loopMax;

                    // Safety: Make sure the loop fits at least twice inside the duration.
                    loopMs = Math.Min(loopMs, Math.Max(Profiles.DecoderGlitch.LoopFloorMs, durationMs * 0.5f));

                    // Select the rolling capture region; writing continues during playback.
                    _glitch.LoopLen = Math.Max(1, (int)(loopMs * _sampleRate / 1000f));
                    _glitch.LoopStart = _glitch.WritePos - _glitch.LoopLen;
                    if (_glitch.LoopStart < 0) _glitch.LoopStart += _glitchCapture.Length;
                    _glitch.PlayPos = 0;

                    // Cooldown: Prevent new glitches for a short time.
                    float amountSq = amount * amount;
                    float cooldownMs = Profiles.DecoderGlitch.CooldownMs + (Profiles.DecoderGlitch.ExtraCooldownMs * (1f - amountSq));
                    _glitch.Cooldown = _glitch.Remain + (int)(cooldownMs * _sampleRate / 1000f);
                }
                else
                {
                    // Accumulate chance: Guarantees a glitch will eventually happen on long sounds.
                    _glitch.TriggerAccumulator += amount * amount * amount * Profiles.DecoderGlitch.ChanceAccumulation;
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
        frozen = Dsp.SoftClip(frozen * Dsp.Lerp(1.0f, Profiles.DecoderGlitch.PlaybackDrive, amount));

        // Apply a 2ms Hann window fade at the edges of the loop to prevent audio clicks
        int targetFade = _sampleRate * Profiles.DecoderGlitch.LoopFadeMs / 1000;
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
        int releaseSamples = Math.Min(_sampleRate * Profiles.DecoderGlitch.ReleaseFadeMs / 1000, _glitch.LoopLen);
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

        float companderGain = _radio.TacticalEnvelope < Profiles.TacticalRadio.CompanderThreshold ? Profiles.TacticalRadio.QuietGain : Profiles.TacticalRadio.LoudGain;
        float companded = x * companderGain;

        // Amplitude-domain compression with 1 ms attack / 80 ms release; ratio is not a dB slope.
        float clipped = Dsp.SoftClip(companded * Profiles.TacticalRadio.InputDrive);
        float compressed = _radio.TacticalCompressor.Process(
            clipped, Profiles.TacticalRadio.CompressorThreshold, Profiles.TacticalRadio.CompressorRatio,
            _radio.CompAttackCoeff,
            _radio.CompReleaseCoeff);

        float rfSignal = compressed * Profiles.TacticalRadio.RfGain;

        // The entire codec advances at the reference clock, including its feedback and decay.
        // Below that rate, several virtual ticks share the available host input sample.
        _radio.CvsdClockPhase += Profiles.Common.ReferenceSampleRate;
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
            _radio.CvsdAccumulator = Math.Clamp(_radio.CvsdAccumulator, -Profiles.TacticalRadio.AccumulatorLimit, Profiles.TacticalRadio.AccumulatorLimit);
            _radio.CvsdAccumulator = Dsp.KillDenormal(_radio.CvsdAccumulator);
            _radio.ReconState += _radio.ReconCoeff * (_radio.CvsdAccumulator - _radio.ReconState);
            _radio.ReconState = Dsp.KillDenormal(_radio.ReconState);
        }
        float reconstructed = _sampleRate == Profiles.Common.ReferenceSampleRate ? _radio.ReconState
            : Dsp.Lerp(_radio.CvsdPrevious, _radio.ReconState, _radio.CvsdClockPhase * _inverseSampleRate);

        // AsymmetricSaturation adds odd-harmonic gravel of an overdriven RF stage.
        const float outputTrim = Profiles.TacticalRadio.OutputTrim;
        float saturated = Dsp.AsymmetricSaturation(reconstructed * Profiles.TacticalRadio.SaturationDrive) / Profiles.TacticalRadio.SaturationDrive;
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
            buffer[i] = dry * 0f + wet * Profiles.TacticalRadio.OutputGain;
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

    /// <summary>Releases request-owned native processing state, including canceled requests.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _robot?.Dispose();
        _robot = null;
        _harmonizer?.Dispose();
        _harmonizer = null;
        _current = VoiceEffectType.None;
        GC.SuppressFinalize(this);
    }

}
