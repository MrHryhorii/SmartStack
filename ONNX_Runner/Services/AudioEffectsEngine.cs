using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
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
///   - Parallel Crossfade: Dsp.EqualPowerCrossfade(dry, wet, amount) -> Spectral effects.
///   - Parallel Add: Dsp.Lerp(dry, Dsp.SoftClip(dry + wet * headroom), amount) -> Safe summation.
///   - Strict Insert: wet -> Complete signal chain transformations. Mathematically neutral at amount=0.
///   - Strict Crossfade: Dsp.Lerp(dry, wet, amount) -> For effects where the "wet" signal is not strictly additive (e.g., Hologram).
/// </summary>
/// 
/// /// DESIGN PHILOSOPHY:
/// This engine targets high-throughput server-side TTS processing.
/// Algorithms prioritize:
///   - zero runtime allocations;
///   - deterministic CPU cost;
///   - single-pass streaming compatibility;
///   - perceptual authenticity over physical simulation.
///
/// Some classic studio DSP techniques are intentionally simplified
/// to preserve real-time performance and horizontal scalability.
public class AudioEffectsEngine(EffectsSettings config, int sampleRate)
{
    private readonly EffectsSettings _config = config;
    private readonly int _sampleRate = sampleRate;

    // Pre-scaled coefficients for envelope followers and modulators to avoid per-sample calculations.
    private readonly float _envAtt = Dsp.ScaleCoeff(0.2f, sampleRate);
    private readonly float _envRel = Dsp.ScaleCoeff(0.005f, sampleRate);
    private readonly float _agcAtt = Dsp.ScaleCoeff(0.05f, sampleRate);
    private readonly float _agcRel = Dsp.ScaleCoeff(0.001f, sampleRate);
    private readonly float _glitchAtt = Dsp.ScaleCoeff(0.9f, sampleRate);
    private readonly float _glitchRel = Dsp.ScaleCoeff(0.001f, sampleRate);
    private readonly float _tapeSlew = Dsp.ScaleCoeff(0.002f, sampleRate);
    private readonly float _thermalDriftCoeff = Dsp.ScaleCoeff(0.0001f, sampleRate);
    private readonly float _cvsdRateScale = 48000f / sampleRate;

    private enum RoutingMode
    {
        ParallelCrossfade,
        ParallelAdd,
        StrictInsert,
        StrictCrossfade
    }

    // =========================================================================
    // EFFECT STATE STRUCTURES (Zero-Allocation)
    // =========================================================================

    private struct ModulationState
    {
        public float RingPhase;
        public float FlangerPhase;
        public float ChorusPhase;
        public float ChorusPhase2;
    }

    private struct BitcrusherState
    {
        public float Phase;
        public float Hold;
    }

    private struct TapeState
    {
        public BiQuadFilter? PreHp;
        public float LofiPhase;
        public float FlutterPhase;
        public float PreState;
        public float DeState;
        public float HissState;
        public float PreCoeff;
        public float DeCoeff;
        public float HissCoeff;

        public TapeDropout DropOut;
        public FeedForwardCompressor Compressor;
        public float CompAttackCoeff;
        public float CompReleaseCoeff;

        public BiQuadFilter? BoomboxBump;
        public BiQuadFilter? BoomboxHp;
        public BiQuadFilter? BoomboxLp;
    }

    private struct AzimuthState
    {
        public float Phase;
        public float CoeffMin;
        public float CoeffMax;
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

        // The TriggerAccumulator is a simple counter that increments each time the glitch effect is triggered.
        public float TriggerAccumulator;
    }

    private struct RadioState
    {
        public float Envelope;
        public float SquelchEnvelope;
        public FeedForwardCompressor Compressor;
        public float CompAttackCoeff;
        public float CompReleaseCoeff;
        public float AgcState;
        public int TxTimer;
        public bool IsTransmitting;

        public float CvsdAccumulator;
        public float CvsdStepSize;
        public uint CvsdHistory;
        public float CvsdReconstructionState;

        public float StepDecay;
        public float AccumDecay;
        public float ReconCoeff;
        public float SquelchReleaseCoeff;
    }

    // =========================================================================
    // CORE DSP PRIMITIVES & BUFFERS
    // =========================================================================

    private readonly DelayBuffer _delay = new(4096);
    private DcBlocker _dcBlocker = new(sampleRate);
    private NoiseGenerator _noise = new();
    private ThermalDrift _thermal = new();

    private readonly BiQuadFilter[] _filters = new BiQuadFilter[5];
    private int _filterCount;
    private VoiceEffectType _current = VoiceEffectType.None;

    private readonly float[] _glitchCapture = new float[4096];
    private const int GlitchCaptureMask = 4095;
    private readonly DelayBuffer _auxDelay = new(4096);

    // =========================================================================
    // EFFECT STATE INSTANCES
    // =========================================================================

    private ModulationState _mod;
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
    /// Should be called at the start of each new audio generation request to ensure deterministic behavior 
    /// and prevent state bleed between requests.
    /// Optionally accepts a fixed random seed for noise generators to allow reproducible effects, 
    /// which can be useful for debugging or consistent rendering in certain applications. 
    /// If no seed is provided, it will use the current time to generate a seed, resulting in non-deterministic noise patterns.
    /// </summary>
    public void Reset(uint? fixedSeed = null)
    {
        _delay.Clear();
        _dcBlocker.Reset();

        Array.Clear(_filters, 0, _filters.Length);
        _filterCount = 0;

        uint seed = fixedSeed ?? (uint)(DateTime.UtcNow.Ticks % uint.MaxValue);
        _noise.Seed(seed);
        _noise.Reset();
        _thermal.Reset();

        _current = VoiceEffectType.None;

        _mod = default;

        _bc.Phase = 1f;
        _bc.Hold = 0f;

        _tape.DropOut.Reset();
        _tape.Compressor.Reset();
        _tape.LofiPhase = 0f;
        _tape.FlutterPhase = 0f;
        _tape.PreState = 0f;
        _tape.DeState = 0f;
        _tape.HissState = 0f;

        _azimuth = default;

        _glitch = default;
        _glitch.TriggerWindowTimer = _sampleRate / 100;
        Array.Clear(_glitchCapture, 0, _glitchCapture.Length);

        _auxDelay.Clear();

        _radio = default;
        _radio.Compressor.Reset();
    }

    /// <summary>
    /// Applies the specified audio effect to the input buffer in-place.
    /// The 'amount' parameter controls the intensity of the effect, 
    /// typically ranging from 0 (no effect) to 1 (full effect).
    /// Optimized for real-time processing with zero allocations in the audio thread.
    /// </summary>
    public void ApplyEffect(Span<float> buffer, VoiceEffectType type, float amount)
    {
        if (!_config.EnableGlobalEffects || type == VoiceEffectType.None)
            return;

        // Fast-path bypass for CPU savings in server environments.
        // The underlying DSP is mathematically neutral at amount=0, so removing this guard is safe.
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
        int filterCount = _filterCount;
        BiQuadFilter[] filters = _filters;

        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];

            // Isolate 'filtered' signal strictly for effect processing.
            float filtered = Dsp.KillDenormal(dry);

            _thermal.Update(ref _noise, _thermalDriftCoeff);

            if (filterCount > 0)
                for (int f = 0; f < filterCount; f++)
                    filtered = filters[f].Transform(filtered);

            // Algorithm runs on filtered path.
            float wet = Process(type, filtered, amount);
            wet = type != VoiceEffectType.DecoderGlitch ? _dcBlocker.Process(wet) : wet;

            // Mathematical routing guarantees True Bypass when amount=0.
            switch (mode)
            {
                case RoutingMode.ParallelCrossfade:
                    buffer[i] = Dsp.EqualPowerCrossfade(dry, wet, amount);
                    break;

                case RoutingMode.ParallelAdd:
                    buffer[i] = Dsp.Lerp(dry, Dsp.SoftClip(dry + wet * 0.85f), amount);
                    break;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RoutingMode GetRoutingMode(VoiceEffectType type) => type switch
    {
        VoiceEffectType.Telephone => RoutingMode.ParallelCrossfade,
        VoiceEffectType.Overdrive => RoutingMode.ParallelCrossfade,
        VoiceEffectType.Bitcrusher => RoutingMode.ParallelCrossfade,
        VoiceEffectType.RingModulator => RoutingMode.ParallelCrossfade,
        VoiceEffectType.Flanger => RoutingMode.ParallelAdd,
        VoiceEffectType.Chorus => RoutingMode.ParallelAdd,
        VoiceEffectType.LoFiTape => RoutingMode.StrictInsert,
        VoiceEffectType.DecoderGlitch => RoutingMode.StrictInsert,
        VoiceEffectType.CvsdCodec => RoutingMode.ParallelCrossfade,
        _ => RoutingMode.StrictInsert
    };

    private void Setup(VoiceEffectType type)
    {
        _filterCount = 0;

        float nyq = _sampleRate * 0.45f;
        float Safe(float f) => Math.Min(f, nyq);

        switch (type)
        {
            case VoiceEffectType.Telephone:
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(380f), 1.0f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(950f), 4.5f, 7.5f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(820f), 6.0f, 8.0f);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(3400f), 1.0f);
                break;

            case VoiceEffectType.Overdrive:
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(180f), 0.8f);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, nyq, 0.707f);
                break;

            case VoiceEffectType.Bitcrusher:
            case VoiceEffectType.RingModulator:
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(130f), 0.707f);
                break;

            case VoiceEffectType.LoFiTape:
                _tape.PreHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(80f), 0.707f);

                float iecFc = 1326f;
                _tape.PreCoeff = 1f - MathF.Exp(-2f * MathF.PI * iecFc / _sampleRate);
                _tape.DeCoeff = _tape.PreCoeff;
                _tape.HissCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(6000f) / _sampleRate);
                _tape.PreState = _tape.DeState = _tape.HissState = 0f;

                _tape.DropOut.Reset();
                _tape.Compressor.Reset();

                _tape.CompAttackCoeff = FeedForwardCompressor.TimeToCoeff(2f, _sampleRate);
                _tape.CompReleaseCoeff = FeedForwardCompressor.TimeToCoeff(200f, _sampleRate);

                _azimuth.CoeffMax = 1f - MathF.Exp(-2f * MathF.PI * Safe(11000f) / _sampleRate);
                _azimuth.CoeffMin = 1f - MathF.Exp(-2f * MathF.PI * Safe(4500f) / _sampleRate);
                _azimuth.Phase = 0f;
                _azimuth.FilterState = 0f;

                _tape.BoomboxBump = BiQuadFilter.PeakingEQ(_sampleRate, Safe(120f), 0.8f, 4.0f);
                _tape.BoomboxHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(70f), 0.707f);
                _tape.BoomboxLp = BiQuadFilter.LowPassFilter(_sampleRate, Safe(7500f), 0.707f);
                break;

            case VoiceEffectType.DecoderGlitch:
                _glitch = default;
                _glitch.TriggerWindowTimer = _sampleRate / 100; // 10ms window
                break;

            case VoiceEffectType.CvsdCodec:
                // Digital Tactical / Motorola Profile (320 - 3400 Hz)
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(320f), 0.707f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(1100f), 1.4f, 3.0f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(2100f), 1.8f, 5.0f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(2800f), 1.4f, 3.0f);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(3400f), 0.707f);

                _radio = default;
                _radio.Compressor.Reset();

                // Aggressive compression (Attack: 1ms, Release: 80ms)
                _radio.CompAttackCoeff = FeedForwardCompressor.TimeToCoeff(1f, _sampleRate);
                _radio.CompReleaseCoeff = FeedForwardCompressor.TimeToCoeff(80f, _sampleRate);

                // --- CVSD Codec State Initialization ---
                _radio.StepDecay = MathF.Exp(-1f / (0.004f * _sampleRate));
                _radio.AccumDecay = MathF.Exp(-1f / (0.025f * _sampleRate));
                _radio.ReconCoeff = 1f - MathF.Exp(-2f * MathF.PI * 2400f / _sampleRate);
                _radio.SquelchReleaseCoeff = 1f - MathF.Exp(-1f / (0.1f * _sampleRate));

                _delay.Clear();
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Process(VoiceEffectType type, float x, float amount) => type switch
    {
        VoiceEffectType.Telephone => Telephone(x, amount),
        VoiceEffectType.Overdrive => Overdrive(x, amount),
        VoiceEffectType.Bitcrusher => Bitcrusher(x, amount),
        VoiceEffectType.RingModulator => RingMod(x, amount),
        VoiceEffectType.Flanger => Flanger(x, amount),
        VoiceEffectType.Chorus => Chorus(x, amount),
        VoiceEffectType.LoFiTape => LoFiTape(x, amount),
        VoiceEffectType.DecoderGlitch => DecoderGlitch(x, amount),
        VoiceEffectType.CvsdCodec => CvsdCodec(x, amount),
        _ => x
    };

    // =========================================================================
    // EFFECT ALGORITHMS
    // =========================================================================

    /// <summary>
    /// Simulates the characteristic distortion and line noise of a POTS telephone connection.
    /// Pink noise amplitude is thermally modulated to replicate the unpredictable hiss
    /// of aging analog telephony hardware. Returns a pure wet signal.
    /// </summary>
    private float Telephone(float x, float amount)
    {
        float noise = _noise.NextPink() * (0.003f + _thermal.State * 0.002f + 0.018f * amount * amount) * amount;
        float drive = 1f + amount * 2.5f;
        float wet = Dsp.AsymmetricSaturation(x * drive + noise);
        return wet / drive;
    }

    /// <summary>
    /// Simulates the warm harmonic saturation of an analog tube or transistor overdrive stage.
    /// Thermal bias drift replicates the slow operating-point shift of a warming tube amplifier,
    /// causing the distortion character to evolve naturally over time. Returns a pure wet signal.
    /// </summary>
    private float Overdrive(float x, float amount)
    {
        float bias = _thermal.State * 0.06f * amount;
        float drive = 1f + amount * 4.0f;
        float wet = Dsp.AsymmetricSaturation((x * drive) + bias);
        float trim = 1f / MathF.Sqrt(drive);
        return wet * trim;
    }

    /// <summary>
    /// Simulates the true aliasing and quantization artifacts of a lo-fi bitcrusher.
    /// Academic implementation: strictly relies on Zero-Order Hold (decimation) and 
    /// amplitude quantization. The "metallic" character naturally arises from 
    /// foldover frequencies (aliasing) and sharp staircase waveforms.
    /// </summary>
    private float Bitcrusher(float x, float amount)
    {
        float jitter = _thermal.State * 0.005f * amount;
        float targetRate = Dsp.Lerp((float)_sampleRate, 11025f, amount);
        float step = targetRate / _sampleRate;

        _bc.Phase += step * (1f + jitter);

        if (_bc.Phase >= 1f)
        {
            _bc.Phase -= 1f;
            int bits = (int)MathF.Round(16f - (amount * 12f));
            float levels = 1 << bits;
            _bc.Hold = MathF.Round(x * levels) / levels;
        }

        return _bc.Hold;
    }

    /// <summary>
    /// Simulates a classic analog ring modulator.
    /// The carrier frequency drifts with thermal state, replicating the detuned oscillator
    /// instability of vintage hardware ring modulators. Internal output scaling compensates
    /// for the amplitude multiplication inherent to ring modulation.
    /// Returns a pure wet signal.
    /// </summary>
    private float RingMod(float x, float amount)
    {
        float freq = 30f + _thermal.State * 1.5f;
        _mod.RingPhase = Dsp.AdvancePhase(_mod.RingPhase, freq, _sampleRate);
        float carrier = Dsp.Lerp(1f, Dsp.Sine(_mod.RingPhase), amount);
        float makeupGain = 1f + 0.8f * amount;
        return Dsp.SoftClip(x * carrier * makeupGain);
    }

    /// <summary>
    /// Simulates a classic analog flanger using a single modulated delay line with feedback.
    /// The comb filtering effect is produced by mixing the delayed signal with the dry path
    /// externally (in the mix stage), creating the characteristic jet-sweep resonance.
    /// Returns a pure wet signal (the delayed component only).
    /// </summary>
    private float Flanger(float x, float amount)
    {
        _mod.FlangerPhase = Dsp.AdvancePhase(_mod.FlangerPhase, 0.45f, _sampleRate);
        float delayMs = 0.1f + 1.7f * amount + Dsp.Sine(_mod.FlangerPhase) * (1.1f * amount);
        float delayed = _delay.Read(delayMs * _sampleRate * 0.001f);
        _delay.Write(Dsp.SoftClip(x + delayed * 0.75f));
        return delayed * (0.72f * amount);
    }

    /// <summary>
    /// Simulates a classic analog chorus effect using two modulated delay lines.
    /// </summary>
    private float Chorus(float x, float amount)
    {
        _mod.ChorusPhase = Dsp.AdvancePhase(_mod.ChorusPhase, 0.55f, _sampleRate);
        _mod.ChorusPhase2 = Dsp.AdvancePhase(_mod.ChorusPhase2, 0.83f, _sampleRate);

        float wow = _thermal.State * 0.006f * amount;
        float d1 = 0.1f + 14.9f * amount + Dsp.Sine(_mod.ChorusPhase) * (6.0f * amount) + wow;
        float d2 = 0.1f + 23.9f * amount + Dsp.Sine(_mod.ChorusPhase2) * (7.0f * amount) - wow;

        float msToSamples = _sampleRate * 0.001f;
        float s1 = _delay.Read(d1 * msToSamples);
        float s2 = _delay.Read(d2 * msToSamples);
        _delay.Write(x);

        return (s1 + s2) * (0.45f * amount);
    }

    /// <summary>
    /// Simulates the characteristic warmth and modulation of analog cassette tape.
    /// A modulated delay line with feedback creates the tape's natural pitch instability,
    /// while thermally modulated pink noise simulates the hiss and mechanical rumble of tape transport.
    /// </summary>
    private float LoFiTape(float x, float amount)
    {
        // Rumble filter
        // Cuts subsonic noise to prevent the saturation stage from choking on low frequencies.
        float hpf = _tape.PreHp != null ? _tape.PreHp.Transform(x) : x;
        float input = Dsp.Lerp(x, hpf, amount);

        // Pre-emphasis
        // Artificially boosts high frequencies before magnetic recording to compensate for tape physics.
        // Muted slightly (0.8f) to act as a natural de-esser, preventing harsh sibilance in synthesized voices.
        _tape.PreState += _tape.PreCoeff * (input - _tape.PreState);
        _tape.PreState = Dsp.KillDenormal(_tape.PreState);
        float preEmph = input + (input - _tape.PreState) * (0.8f * amount);

        // Tape saturation
        // Simulates magnetic oxide overdrive, making the voice dense, warm, and rich in harmonics.
        float drive = 1f + 2.0f * amount;
        float sat = Dsp.SoftClip(preEmph * drive);
        float satCompensation = 1f / (1f + 0.35f * amount); // Perceptual compensation
        float saturated = Dsp.Lerp(preEmph, sat * satCompensation, amount);

        // Tape compression
        // Mimics cassette dynamics (e.g., Dolby NR), making quiet details louder while squashing peaks.
        float compThreshold = Dsp.Lerp(1.0f, 0.35f, amount);
        float comp = _tape.Compressor.Process(saturated, compThreshold, 3.5f, _tape.CompAttackCoeff, _tape.CompReleaseCoeff);

        // COMPRESSION MAKEUP (GAIN STAGING)
        // TTS voices are inherently very dense. Instead of boosting the makeup gain,
        // we deliberately attenuate the voice (down to 0.85x) so it sits perfectly 
        // inside the tape noise floor without causing volume buildup.
        float compMakeup = Dsp.Lerp(1f, 0.85f, amount);
        float recorded = Dsp.Lerp(saturated, comp * compMakeup, amount);

        // Tape write
        // Saves the processed signal into the delay buffer.
        _delay.Write(recorded);

        // Wow and Flutter
        // Non-linear pitch drift (motor defect) and rapid trembling (tape friction).
        _tape.LofiPhase = Dsp.AdvancePhase(_tape.LofiPhase, 0.4f, _sampleRate);
        _tape.FlutterPhase = Dsp.AdvancePhase(_tape.FlutterPhase, 8.5f, _sampleRate);

        float wow = (Dsp.Sine(_tape.LofiPhase) * 0.8f + Dsp.Sine(_tape.LofiPhase * 0.31f) * 0.4f) * amount;
        float flutter = (Dsp.Sine(_tape.FlutterPhase) * 0.15f + _noise.NextWhite() * 0.03f) * amount;

        float delayMs = 0.1f + 1.3f * amount;
        float msToSamples = _sampleRate * 0.001f;
        float pitchWarped = _delay.Read((delayMs + wow + flutter) * msToSamples);

        // Dropout
        // Physical tape wear causing smooth, stochastic volume drops (oxide shedding).
        float dropped = _tape.DropOut.Process(pitchWarped, amount, ref _noise, _sampleRate, _tapeSlew);

        // De-emphasis
        // Restores spectral balance after reading from the playback head.
        _tape.DeState += _tape.DeCoeff * (dropped - _tape.DeState);
        _tape.DeState = Dsp.KillDenormal(_tape.DeState);

        float hfCut = (dropped - _tape.DeState) * (0.50f * amount);
        float deEmph = dropped - hfCut;

        // Azimuth drift
        // Dynamic high-frequency loss simulating tape misalignment against the magnetic head.
        _azimuth.Phase = Dsp.AdvancePhase(_azimuth.Phase, 0.37f, _sampleRate);
        float azimuthMod = 0.5f + 0.5f * Dsp.Sine(_azimuth.Phase);

        float currentAzimuthCoeff = Dsp.Lerp(_azimuth.CoeffMax, _azimuth.CoeffMin, azimuthMod * amount);

        _azimuth.FilterState += currentAzimuthCoeff * (deEmph - _azimuth.FilterState);
        _azimuth.FilterState = Dsp.KillDenormal(_azimuth.FilterState);

        // Quadratic blend keeps Azimuth subtle at lower intensities.
        float azimuthMix = amount * amount * 0.4f;
        float azimuthSignal = Dsp.Lerp(deEmph, _azimuth.FilterState, azimuthMix);

        // Bias grain and Hiss
        // Microscopic tape grain and ambient playback amplifier noise.
        float biasGrain = _noise.NextWhite() * 0.0015f * amount;
        float hissAmount = 0.0025f * amount + 0.0075f * amount * amount; // Smoother hiss curve
        float rawHiss = _noise.NextWhite() * (hissAmount + _thermal.State * 0.003f);
        _tape.HissState += _tape.HissCoeff * (rawHiss - _tape.HissState);

        float tapeSignal = azimuthSignal + biasGrain + _tape.HissState;

        // Speaker simulation
        // Quadratic blend simulating a cheap plastic speaker enclosure, dominant only on heavily degraded tape.
        if (_tape.BoomboxBump != null && _tape.BoomboxHp != null && _tape.BoomboxLp != null)
        {
            float bumped = _tape.BoomboxBump.Transform(tapeSignal);
            float filtered = _tape.BoomboxLp.Transform(_tape.BoomboxHp.Transform(bumped));

            float boomboxMix = amount * amount;
            tapeSignal = Dsp.Lerp(tapeSignal, filtered, boomboxMix);
        }

        // Output and Final Limiter
        float perceptualTrim = 1f - (0.38f * amount * amount);
        float finalSignal = Dsp.SoftClip(tapeSignal * perceptualTrim);

        return finalSignal;
    }

    /// <summary>
    /// Simulates a VoIP packet loss or AI decoder failure.
    /// Instead of stretching time (like a human stutter), this effect overwrites 
    /// the live audio stream with a frozen buffer, simulating a stalled audio thread 
    /// while the real-time stream continues beneath it.
    /// Duration scales linearly from 0ms to 600ms based on intensity.
    /// Loop length scales via inverse cubic interpolation to guarantee artifact-free micro-glitches.
    /// </summary>
    private float DecoderGlitch(float x, float amount)
    {
        // --- Capture Ring Buffer ---
        _glitchCapture[_glitch.WritePos] = x;
        _glitch.WritePos = (_glitch.WritePos + 1) & GlitchCaptureMask;

        // --- Vowel Detector ---
        float xSq = x * x;
        _glitch.Energy += (xSq > _glitch.Energy ? _glitchAtt : _glitchRel) * (xSq - _glitch.Energy);

        if ((x >= 0f) != (_glitch.PrevSample >= 0f)) _glitch.ZcrCount++;
        _glitch.PrevSample = x;

        // Check the detector every window (~10ms)
        if (--_glitch.TriggerWindowTimer <= 0)
        {
            float rawZcr = (float)_glitch.ZcrCount / (_sampleRate / 100);
            _glitch.Zcr = 0.85f * _glitch.Zcr + 0.15f * rawZcr;
            _glitch.ZcrCount = 0;
            _glitch.TriggerWindowTimer = _sampleRate / 100;

            // Content-aware vowel detector: trigger only on loud and stable voiced segments
            if (!_glitch.Frozen && _glitch.Cooldown <= 0
                && _glitch.Energy > 0.004f
                && _glitch.Zcr < 0.08f)
            {
                // Map white noise from [-0.5, 0.5] to a uniform [0.0, 1.0] range
                float roll = _noise.NextWhite() + 0.5f;

                // Pseudo-Random Distribution (PRD): base 1% chance + accumulated missed steps
                float currentChance = 0.01f + _glitch.TriggerAccumulator;

                if (roll < currentChance)
                {
                    // Glitch successfully triggered
                    _glitch.Frozen = true;
                    _glitch.TriggerAccumulator = 0f;

                    // Duration: Square root (Ease-Out) scaling.
                    // Grows aggressively at low intensities for noticeable "android hangs",
                    // then slows its growth towards the 600ms maximum.
                    float durationMs = MathF.Sqrt(amount) * 600f;
                    _glitch.Remain = (int)(durationMs * _sampleRate / 1000f);

                    // Loop Size: "Knee point" macro-mapping.
                    // Rapidly scales from 5ms to 33ms in the first 10% of the slider,
                    // then stays strictly locked at 33ms (~30Hz) for classic stutter character.
                    const float kneeAmount = 0.1f;
                    const float loopMin = 5f;
                    const float loopMax = 33f;

                    float loopMs = amount < kneeAmount
                        ? loopMin + (loopMax - loopMin) * (amount / kneeAmount)
                        : loopMax;

                    // Mathematical protection: guarantee at least 2 full cycles for micro-glitches
                    loopMs = Math.Min(loopMs, Math.Max(2f, durationMs * 0.5f));

                    // Commit loop buffer coordinates
                    _glitch.LoopLen = Math.Max(1, (int)(loopMs * _sampleRate / 1000f));
                    _glitch.LoopStart = (_glitch.WritePos - _glitch.LoopLen + _glitchCapture.Length) & GlitchCaptureMask;
                    _glitch.PlayPos = 0;

                    // Cooldown: 
                    // Prevent retriggering until the current glitch has fully played out.
                    // Keeps the pause long (~400ms) at lower intensities to isolate micro-glitches,
                    // dropping sharply to 200ms baseline at maximum intensity.
                    float amountSq = amount * amount;
                    float cooldownMs = 200f + (200f * (1f - amountSq));

                    _glitch.Cooldown = _glitch.Remain + (int)(cooldownMs * _sampleRate / 1000f);
                }
                else
                {
                    // Missed roll: accumulate a small chance increase 
                    // to guarantee eventual triggering during sustained vowels.
                    // Uses cubic scaling so the bonus remains extremely low at <50% intensity,
                    // but ramps up to exactly 0.001f at 100% intensity.
                    float bonus = amount * amount * amount * 0.001f;
                    _glitch.TriggerAccumulator += bonus;
                }
            }
        }

        if (_glitch.Cooldown > 0) _glitch.Cooldown--;

        // If not glitching, just return the normal live sound.
        if (!_glitch.Frozen) return x;

        // --- Frozen State Output ---
        if (--_glitch.Remain <= 0)
        {
            _glitch.Frozen = false;
            return x;
        }

        float frozen = _glitchCapture[(_glitch.LoopStart + _glitch.PlayPos) & GlitchCaptureMask];
        frozen = Dsp.SoftClip(frozen * 1.15f); // 1.15f saturation

        // Dynamic fade-in and fade-out at the start and end of the loop to prevent clicks.
        int targetFade = _sampleRate * 2 / 1000;
        int fadeLen = Math.Min(targetFade, _glitch.LoopLen / 2);

        if (fadeLen > 0)
        {
            int samplesLeftInLoop = _glitch.LoopLen - _glitch.PlayPos;
            if (samplesLeftInLoop < fadeLen)
                frozen *= Dsp.HannWindow(samplesLeftInLoop, fadeLen * 2);
            else if (_glitch.PlayPos < fadeLen)
                frozen *= Dsp.HannWindow(_glitch.PlayPos, fadeLen * 2);
        }

        _glitch.PlayPos = (_glitch.PlayPos + 1) % _glitch.LoopLen;

        // Additional release fade in the last 25ms of the glitch to ensure a smooth transition back to live audio, 
        // especially for longer stutters.
        int releaseSamples = Math.Min(_sampleRate * 25 / 1000, _glitch.LoopLen);
        if (_glitch.Remain < releaseSamples && releaseSamples > 0)
        {
            float tFade = (float)_glitch.Remain / releaseSamples;
            frozen = frozen * tFade + x * (1f - tFade);
        }

        return frozen;
    }

    /// <summary>
    /// Simulates a tactical military radio channel using academic CVSD (Continuously Variable Slope Delta) 
    /// modulation, dynamic slope overload, and half-duplex clipping.
    /// </summary>
    private float CvsdCodec(float x, float amount)
    {
        float absX = MathF.Abs(x);

        // --- Envelope Followers ---
        _radio.Envelope += (absX > _radio.Envelope ? _envAtt : _envRel) * (absX - _radio.Envelope);
        _radio.Envelope = Dsp.KillDenormal(_radio.Envelope);

        // Squelch envelope with hysteresis: closes quickly when the signal exceeds the threshold, 
        // but opens slowly to prevent chattering on borderline signals.
        _radio.SquelchEnvelope += (absX > _radio.SquelchEnvelope ? 0.8f : _radio.SquelchReleaseCoeff) * (absX - _radio.SquelchEnvelope);
        _radio.SquelchEnvelope = Dsp.KillDenormal(_radio.SquelchEnvelope);

        // --- Half-Duplex Clipping & Smooth Burst ("tchsh") ---
        int maxTxTimer = (int)((0.02f + amount * 0.04f) * _sampleRate);
        bool currentlyTransmitting = _radio.SquelchEnvelope > 0.01f;

        if (currentlyTransmitting && !_radio.IsTransmitting)
        {
            _radio.IsTransmitting = true;
            _radio.TxTimer = maxTxTimer;
        }
        else if (!currentlyTransmitting && _radio.SquelchEnvelope < 0.001f)
        {
            _radio.IsTransmitting = false;
        }

        float txGate = 1f;
        float burstNoise = 0f;

        if (_radio.TxTimer > 0)
        {
            float burstProgress = 1f - ((float)_radio.TxTimer / maxTxTimer);

            // Smooth burst envelope using a cosine function for natural fade-in and fade-out.
            float burstEnv = burstProgress < 0.15f
                ? 0.5f - 0.5f * MathF.Cos(burstProgress / 0.15f * MathF.PI)
                : 0.5f + 0.5f * MathF.Cos((burstProgress - 0.15f) / 0.85f * MathF.PI);

            burstNoise = _noise.NextPink() * burstEnv * 0.35f * amount;

            _radio.TxTimer--;
            txGate = 0f;
        }

        // --- Pre-Compressor Dynamic Chain ---
        float companderGain = _radio.Envelope < 0.1f ? 1.15f : 0.92f;
        float companded = x * Dsp.Lerp(1f, companderGain, amount) * txGate;

        float preClipDrive = 1f + amount * 1.5f;
        float clippedInput = Dsp.SoftClip(companded * preClipDrive);

        // --- RF Compressor & AGC Pumping ---
        float compressed = _radio.Compressor.Process(clippedInput, 0.25f, 5f, _radio.CompAttackCoeff, _radio.CompReleaseCoeff);

        float dynamicSignal = Dsp.Lerp(clippedInput, compressed, amount);

        float compAbs = MathF.Abs(dynamicSignal);
        _radio.AgcState += (compAbs > _radio.AgcState ? _agcAtt : _agcRel) * (compAbs - _radio.AgcState);
        _radio.AgcState = Dsp.KillDenormal(_radio.AgcState);

        float agcMultiplier = 1f - MathF.Min(0.5f, _radio.AgcState * amount);
        float rfSignal = dynamicSignal * agcMultiplier * Dsp.Lerp(1f, 1.6f, amount);

        // --- CVSD Encoding ---

        // Dynamic step size control based on the input signal and historical bit pattern.
        float minStep = 0.002f * _cvsdRateScale;
        float maxStep = Dsp.Lerp(0.08f, 0.4f, amount) * _cvsdRateScale;
        float stepIncr = Dsp.Lerp(0.001f, 0.02f, amount) * _cvsdRateScale;

        bool bit = rfSignal > _radio.CvsdReconstructionState;

        _radio.CvsdHistory = ((_radio.CvsdHistory << 1) | (bit ? 1u : 0u)) & 0x0Fu;

        if (_radio.CvsdHistory == 0x0Fu || _radio.CvsdHistory == 0x00u)
            _radio.CvsdStepSize += stepIncr;
        else
            _radio.CvsdStepSize *= _radio.StepDecay;

        if (_radio.CvsdStepSize < minStep) _radio.CvsdStepSize = minStep;
        if (_radio.CvsdStepSize > maxStep) _radio.CvsdStepSize = maxStep;

        _radio.CvsdAccumulator += bit ? _radio.CvsdStepSize : -_radio.CvsdStepSize;
        _radio.CvsdAccumulator *= _radio.AccumDecay;

        _radio.CvsdAccumulator = Math.Clamp(_radio.CvsdAccumulator, -1.2f, 1.2f);
        _radio.CvsdAccumulator = Dsp.KillDenormal(_radio.CvsdAccumulator);

        // Update the reconstruction state using the cached coefficient for efficiency.
        _radio.CvsdReconstructionState += _radio.ReconCoeff * (_radio.CvsdAccumulator - _radio.CvsdReconstructionState);
        _radio.CvsdReconstructionState = Dsp.KillDenormal(_radio.CvsdReconstructionState);

        // Quadratic blend for the CVSD output to keep it more intelligible at lower intensities, 
        // while allowing full degradation at maximum intensity.
        float codecMix = amount * amount;
        float codecSignal = Dsp.Lerp(rfSignal, _radio.CvsdReconstructionState, codecMix);

        // --- Radio Hiss & Squelch Tail ---
        float noiseGate = MathF.Min(1f, _radio.SquelchEnvelope * 10f);

        float cvsdHiss = _noise.NextWhite() * _radio.CvsdStepSize * 0.08f * noiseGate * amount;
        float backgroundHiss = _noise.NextWhite() * 0.012f * noiseGate * amount;

        float wet = codecSignal + cvsdHiss + backgroundHiss + burstNoise;

        // --- Final Saturation & Sidetone ---
        float drive = 1f + (amount * 0.4f);
        float saturated = Dsp.AsymmetricSaturation(wet * drive) / drive;

        return Dsp.SoftClip(saturated + x * 0.02f);
    }
}