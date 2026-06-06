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
/// </summary>
public class AudioEffectsEngine(EffectsSettings config, int sampleRate)
{
    private readonly EffectsSettings _config = config;
    private readonly int _sampleRate = sampleRate;

    private enum RoutingMode
    {
        ParallelCrossfade,
        ParallelAdd,
        StrictInsert
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

    private struct StutterState
    {
        public int WritePos;
        public bool Frozen;
        public int LoopStart;
        public int LoopLen;
        public int PlayPos;
        public int Remain;
        public int Cooldown;

        public BiQuadFilter? ZcrFilterLow;
        public BiQuadFilter? ZcrFilterHigh;
        public float Energy;
        public float Ratio;
        public int TriggerWindowTimer;
    }

    // =========================================================================
    // CORE DSP PRIMITIVES & BUFFERS
    // =========================================================================

    private readonly DelayBuffer _delay = new(4096);
    private DcBlocker _dcBlocker = new();
    private NoiseGenerator _noise = new();
    private ThermalDrift _thermal = new();

    private readonly BiQuadFilter[] _filters = new BiQuadFilter[5];
    private int _filterCount;
    private VoiceEffectType _current = VoiceEffectType.None;

    private readonly float[] _glitchCapture = new float[4096];
    private const int GlitchCaptureMask = 4095;

    // =========================================================================
    // EFFECT STATE INSTANCES
    // =========================================================================

    private ModulationState _mod;
    private BitcrusherState _bc;
    private TapeState _tape;
    private AzimuthState _azimuth;
    private StutterState _stutter;

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

        _stutter = default;
        _stutter.TriggerWindowTimer = _sampleRate / 100;

        Array.Clear(_glitchCapture, 0, _glitchCapture.Length);
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

            _thermal.Update(ref _noise);

            if (filterCount > 0)
                for (int f = 0; f < filterCount; f++)
                    filtered = filters[f].Transform(filtered);

            // Algorithm runs on filtered path.
            float wet = Process(type, filtered, amount);
            wet = type != VoiceEffectType.VocalStutter ? _dcBlocker.Process(wet) : wet;

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
        VoiceEffectType.VocalStutter => RoutingMode.StrictInsert,
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

            case VoiceEffectType.VocalStutter:
                _stutter.ZcrFilterLow = BiQuadFilter.BandPassFilterConstantPeakGain(_sampleRate, Safe(800f), 1.0f);
                _stutter.ZcrFilterHigh = BiQuadFilter.BandPassFilterConstantPeakGain(_sampleRate, Safe(1200f), 1.0f);

                _stutter.Frozen = false;
                _stutter.Cooldown = 0;
                _stutter.Energy = 0f;
                _stutter.Ratio = 0f;
                _stutter.TriggerWindowTimer = _sampleRate / 100;

                _stutter.LoopLen = 0;
                Array.Clear(_glitchCapture, 0, _glitchCapture.Length);
                _stutter.WritePos = 0;
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
        VoiceEffectType.VocalStutter => VocalStutter(x, amount),
        _ => x
    };

    // =========================================================================
    // EFFECT ALGORITHMS
    // =========================================================================

    /// <summary>
    /// Simulates analog telephone line degradation.
    /// Applies non-linear asymmetric soft-clipping driven by pink noise and thermal bias.
    /// </summary>
    private float Telephone(float x, float amount)
    {
        float noise = _noise.NextPink() * (0.003f + _thermal.State * 0.002f + 0.018f * amount * amount) * amount;
        float drive = 1f + amount * 2.5f;
        float wet = Dsp.AsymmetricSaturation(x * drive + noise);
        return wet / drive;
    }

    /// <summary>
    /// Simulates tube/transistor signal saturation.
    /// Applies asymmetric clipping (cubic polynomial + tanh) to generate warm even-harmonics.
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
    /// Digital degradation via Zero-Order Hold (ZOH) decimation and bit quantization.
    /// Dynamically scales from native sample rate to 11kHz and 16-bit to 4-bit based on intensity.
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
    /// Amplitude modulation using a sine carrier wave with drifting frequency (~30Hz).
    /// Smoothly morphs from AM Tremolo (low intensity) to Dalek-style Ring Modulation.
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
    /// Classic flanger effect using a single modulated delay line with a sine LFO.
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
    /// Analog-style chorus utilizing dual asynchronous delay lines with thermal wow.
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
    /// Physical simulation of the IEC 60094 cassette tape recording/playback chain.
    /// Implemented as a sequential DSP pipeline for authentic analog behavior.
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
        float dropped = _tape.DropOut.Process(pitchWarped, amount, ref _noise, _sampleRate);

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
    /// Voice-driven digital glitch/buffer override.
    /// Mid-band vowel proxy using energy ratio between 800Hz and 1200Hz bands strictly targets vowels.
    /// Evaluates probability in fixed 10ms windows to remain Sample Rate independent.
    /// </summary>
    private float VocalStutter(float x, float amount)
    {
        _glitchCapture[_stutter.WritePos] = x;
        _stutter.WritePos = (_stutter.WritePos + 1) & GlitchCaptureMask;

        float abs = MathF.Abs(x);

        float lowBand = _stutter.ZcrFilterLow?.Transform(x) ?? x;
        float highBand = _stutter.ZcrFilterHigh?.Transform(x) ?? x;

        float e1 = lowBand * lowBand;
        float e2 = highBand * highBand;

        _stutter.Energy += (abs - _stutter.Energy) * 0.08f;

        float instantaneousRatio = e1 / (e2 + 0.0001f);
        _stutter.Ratio = 0.9f * _stutter.Ratio + 0.1f * instantaneousRatio;

        bool vowelLike = _stutter.Ratio > 1.2f && _stutter.Energy > 0.002f;

        if (--_stutter.TriggerWindowTimer <= 0)
        {
            _stutter.TriggerWindowTimer = _sampleRate / 100;

            if (!_stutter.Frozen && _stutter.Cooldown <= 0 && vowelLike)
            {
                float triggerChance = amount * 0.015f;

                if ((_noise.NextWhite() + 0.5f) < triggerChance)
                {
                    _stutter.Frozen = true;

                    float durationMs = 40f + amount * 260f;
                    _stutter.Remain = (int)(durationMs * _sampleRate * 0.001f);

                    float jitterMs = 25f + (_noise.NextWhite() + 0.5f) * 20f;
                    _stutter.LoopLen = Math.Max(1, (int)(jitterMs * _sampleRate * 0.001f));

                    _stutter.LoopStart = (_stutter.WritePos - _stutter.LoopLen + _glitchCapture.Length) & GlitchCaptureMask;
                    _stutter.PlayPos = 0;

                    _stutter.Cooldown = _stutter.Remain + (_sampleRate / 2);
                }
            }
        }

        if (_stutter.Cooldown > 0)
            _stutter.Cooldown--;

        if (!_stutter.Frozen)
            return x;

        int fade = _sampleRate * 2 / 1000;

        if (--_stutter.Remain <= 0)
        {
            _stutter.Frozen = false;
            return x;
        }

        float frozen = _glitchCapture[(_stutter.LoopStart + _stutter.PlayPos) & GlitchCaptureMask];
        frozen = Dsp.SoftClip(frozen * 1.1f);

        int left = _stutter.LoopLen - _stutter.PlayPos;

        if (left < fade)
            frozen *= Dsp.HannWindow(left, fade * 2);
        else if (_stutter.PlayPos < fade)
            frozen *= Dsp.HannWindow(_stutter.PlayPos, fade * 2);

        _stutter.PlayPos = (_stutter.PlayPos + 1) % _stutter.LoopLen;

        if (_stutter.Remain < fade)
        {
            float t = (float)_stutter.Remain / fade;
            frozen = Dsp.EqualPowerCrossfade(x, frozen, t);
        }

        return frozen;
    }
}