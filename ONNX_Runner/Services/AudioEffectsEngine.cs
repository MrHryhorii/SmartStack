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

    // --- Core DSP Primitives ---
    private readonly DelayBuffer _delay = new(4096);
    private DcBlocker _dcBlocker = new();
    private NoiseGenerator _noise = new();
    private ThermalDrift _thermal = new();

    // --- EQ Chain ---
    private readonly BiQuadFilter[] _filters = new BiQuadFilter[5];
    private int _filterCount;
    private VoiceEffectType _current = VoiceEffectType.None;

    // --- Per-Effect Oscillator Phases ---
    private float _ringPhase;
    private float _flangerPhase;
    private float _chorusPhase;
    private float _chorusPhase2;

    // --- Bitcrusher State ---
    private float _bcPhase;
    private float _bcHold;

    // --- LoFiTape Components ---
    private BiQuadFilter? _lofiPreHp;
    private float _lofiPhase;
    private float _flutterPhase;
    private float _lofiPreState;
    private float _lofiDeState;
    private float _lofiHissState;
    private float _lofiPreCoeff;
    private float _lofiDeCoeff;
    private float _lofiHissCoeff;

    private TapeDropout _tapeDropOut;
    private FeedForwardCompressor _tapeCompressor;
    private float _tapeCompAttackCoeff;
    private float _tapeCompReleaseCoeff;
    private BiQuadFilter? _boomboxHp;
    private BiQuadFilter? _boomboxLp;

    // --- VocalStutter Components ---
    private readonly float[] _glitchCapture = new float[4096];
    private const int GlitchCaptureMask = 4095;
    private int _glitchWritePos;
    private bool _glitchFrozen;
    private int _glitchLoopStart;
    private int _glitchLoopLen;
    private int _glitchPlayPos;
    private int _glitchRemain;
    private int _glitchCooldown;

    private BiQuadFilter? _glitchZcrFilterLow;
    private BiQuadFilter? _glitchZcrFilterHigh;
    private float _glitchEnergy;
    private float _glitchRatio;
    private int _triggerWindowTimer;

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
    /// <param name="fixedSeed"></param>
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

        _tapeDropOut.Reset();
        _tapeCompressor.Reset();

        _ringPhase = _flangerPhase = _chorusPhase = _chorusPhase2 = 0f;
        _bcPhase = 1f;
        _bcHold = 0f;

        _lofiPhase = _flutterPhase = 0f;
        _lofiPreState = _lofiDeState = _lofiHissState = 0f;

        _current = VoiceEffectType.None;

        _glitchFrozen = false;
        _glitchCooldown = 0;
        _glitchEnergy = 0f;
        _glitchRatio = 0f;
        _triggerWindowTimer = _sampleRate / 100;

        _glitchLoopLen = 0;
        Array.Clear(_glitchCapture, 0, _glitchCapture.Length);
        _glitchWritePos = 0;
    }

    /// <summary>
    /// Applies the specified audio effect to the input buffer in-place.
    /// The 'amount' parameter controls the intensity of the effect, 
    /// typically ranging from 0 (no effect) to 1 (full effect), but can exceed 1 for exaggerated processing.
    /// The method is optimized for real-time processing with zero allocations in the audio thread.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="type"></param>
    /// <param name="amount"></param>
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
                // Pre-HPF isolated inside the effect to allow perfect Bypass mapping
                _lofiPreHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(100f), 0.707f);

                float iecFc = 1326f;
                _lofiPreCoeff = 1f - MathF.Exp(-2f * MathF.PI * iecFc / _sampleRate);
                _lofiDeCoeff = _lofiPreCoeff;
                _lofiHissCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(6000f) / _sampleRate);
                _lofiPreState = _lofiDeState = _lofiHissState = 0f;

                _tapeDropOut.Reset();
                _tapeCompressor.Reset();

                _tapeCompAttackCoeff = FeedForwardCompressor.TimeToCoeff(2f, _sampleRate);
                _tapeCompReleaseCoeff = FeedForwardCompressor.TimeToCoeff(50f, _sampleRate);

                _boomboxHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(250f), 0.5f);
                _boomboxLp = BiQuadFilter.LowPassFilter(_sampleRate, Safe(5000f), 0.5f);
                break;

            case VoiceEffectType.VocalStutter:
                _glitchZcrFilterLow = BiQuadFilter.BandPassFilterConstantPeakGain(_sampleRate, 800f, 1.0f);
                _glitchZcrFilterHigh = BiQuadFilter.BandPassFilterConstantPeakGain(_sampleRate, 1200f, 1.0f);

                _glitchFrozen = false;
                _glitchCooldown = 0;
                _glitchEnergy = 0f;
                _glitchRatio = 0f;
                _triggerWindowTimer = _sampleRate / 100;

                _glitchLoopLen = 0;
                Array.Clear(_glitchCapture, 0, _glitchCapture.Length);
                _glitchWritePos = 0;
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
    /// Applies non-linear symmetric soft-clipping driven by pink noise and thermal bias.
    /// </summary>
    private float Telephone(float x, float amount)
    {
        float noise = _noise.NextPink() * (0.003f + _thermal.State * 0.002f + 0.018f * amount * amount) * amount;
        float drive = 1f + amount * 1.1f;
        return Dsp.SoftClip(Dsp.AsymmetricSaturation(x * drive + noise)) * 0.95f;
    }

    /// <summary>
    /// Simulates tube/transistor signal saturation.
    /// Applies asymmetric clipping (cubic polynomial + tanh) to generate warm even-harmonics.
    /// </summary>
    private float Overdrive(float x, float amount)
    {
        float bias = _thermal.State * 0.06f * amount;
        float drive = 1f + amount * 2.2f;
        return Dsp.AsymmetricSaturation((x * drive) + bias);
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

        _bcPhase += step * (1f + jitter);

        if (_bcPhase >= 1f)
        {
            _bcPhase -= 1f;
            int bits = (int)MathF.Round(16f - (amount * 12f));

            // Quantization levels are centered around zero, so we round to the nearest level and then divide back down.
            float levels = 1 << (bits - 1);

            _bcHold = MathF.Round(x * levels) / levels;
        }

        return _bcHold;
    }

    /// <summary>
    /// Amplitude modulation using a sine carrier wave with drifting frequency (~30Hz).
    /// Smoothly morphs from AM Tremolo (low intensity) to Dalek-style Ring Modulation.
    /// </summary>
    private float RingMod(float x, float amount)
    {
        float freq = 30f + _thermal.State * 1.5f;
        _ringPhase = Dsp.AdvancePhase(_ringPhase, freq, _sampleRate);

        float carrier = Dsp.Lerp(1f, Dsp.Sine(_ringPhase), amount);
        float makeupGain = 1f + 0.8f * amount;

        return Dsp.SoftClip(x * carrier * makeupGain);
    }

    /// <summary>
    /// Analog-style flanger utilizing a modulated delay line (0.1ms to 2.9ms sweep).
    /// </summary>
    private float Flanger(float x, float amount)
    {
        _flangerPhase = Dsp.AdvancePhase(_flangerPhase, 0.45f, _sampleRate);
        float delayMs = 0.1f + 1.7f * amount + Dsp.Sine(_flangerPhase) * (1.1f * amount);
        float delayed = _delay.Read(delayMs * _sampleRate / 1000f);
        _delay.Write(Dsp.SoftClip(x + delayed * 0.68f));
        return delayed * (0.72f * amount);
    }

    /// <summary>
    /// Analog-style chorus utilizing dual asynchronous delay lines with thermal wow.
    /// </summary>
    private float Chorus(float x, float amount)
    {
        _chorusPhase = Dsp.AdvancePhase(_chorusPhase, 0.55f, _sampleRate);
        _chorusPhase2 = Dsp.AdvancePhase(_chorusPhase2, 0.83f, _sampleRate);

        float wow = _thermal.State * 0.006f * amount;
        float d1 = 0.1f + 14.9f * amount + Dsp.Sine(_chorusPhase) * (6.0f * amount) + wow;
        float d2 = 0.1f + 23.9f * amount + Dsp.Sine(_chorusPhase2) * (7.0f * amount) - wow;

        float s1 = _delay.Read(d1 * _sampleRate / 1000f);
        float s2 = _delay.Read(d2 * _sampleRate / 1000f);
        _delay.Write(x);

        return (s1 + s2) * (0.45f * amount);
    }

    /// <summary>
    /// Physical simulation of the IEC 60094 cassette tape recording/playback chain.
    /// Sequentially applies pre-emphasis, saturation, Dolby NR compression, delay buffer,
    /// wow/flutter, playback dropout, de-emphasis, playhead hiss, and boombox speaker EQ.
    /// Mathematically neutral at intensity=0 through localized Lerp bridging.
    /// </summary>
    private float LoFiTape(float x, float amount)
    {
        float hpf = _lofiPreHp != null ? _lofiPreHp.Transform(x) : x;
        float input = Dsp.Lerp(x, hpf, amount);

        // Pre-emphasis filter with non-linear drive to simulate tape head saturation and high-frequency boost.
        _lofiPreState += _lofiPreCoeff * (input - _lofiPreState);
        _lofiPreState = Dsp.KillDenormal(_lofiPreState);
        float preEmph = input + (input - _lofiPreState) * (1.1f * amount);

        float sat = Dsp.AsymmetricSaturation(preEmph * (1f + 0.9f * amount));
        float saturated = Dsp.Lerp(preEmph, sat, amount);

        float compThreshold = Dsp.Lerp(1.0f, 0.3f, amount);
        float comp = _tapeCompressor.Process(saturated, compThreshold, 4.0f, _tapeCompAttackCoeff, _tapeCompReleaseCoeff);
        float recorded = Dsp.Lerp(saturated, comp, amount);

        _delay.Write(recorded);

        _lofiPhase = Dsp.AdvancePhase(_lofiPhase, 1.2f, _sampleRate);
        _flutterPhase = Dsp.AdvancePhase(_flutterPhase, 9.0f, _sampleRate);
        float wow = Dsp.Sine(_lofiPhase) * 1.05f * amount;
        float flutter = (Dsp.Sine(_flutterPhase) * 0.4f + _noise.NextWhite() * 0.05f) * amount;
        float delayMs = 0.1f + 4.9f * amount;
        float pitchWarped = _delay.Read((delayMs + wow + flutter) * _sampleRate / 1000f);
        float playback = Dsp.Lerp(recorded, pitchWarped, amount);

        float dropped = _tapeDropOut.Process(playback, amount, ref _noise, _sampleRate);

        // De-emphasis filter with dynamic high-frequency roll-off to simulate tape hiss and loss of treble detail at higher intensities.
        _lofiDeState += _lofiDeCoeff * (dropped - _lofiDeState);
        _lofiDeState = Dsp.KillDenormal(_lofiDeState);
        float hfCut = (dropped - _lofiDeState) * (0.55f * amount);
        float deEmph = dropped - hfCut;

        float hissAmount = 0.012f * amount + 0.010f * amount * amount;
        float rawHiss = _noise.NextPink() * (hissAmount + _thermal.State * 0.004f);
        _lofiHissState += _lofiHissCoeff * (rawHiss - _lofiHissState);

        float tapeSignal = deEmph + _lofiHissState;
        float outSignal = tapeSignal;

        if (_boomboxHp != null && _boomboxLp != null)
        {
            float filtered = _boomboxLp.Transform(_boomboxHp.Transform(outSignal));
            outSignal = Dsp.EqualPowerCrossfade(outSignal, filtered, amount);
        }

        float makeup = 1f - (0.05f * amount);
        float finalVol = outSignal * makeup * (1f + 0.5f * amount);

        return Dsp.Lerp(x, Dsp.SoftClip(finalVol), amount);
    }

    /// <summary>
    /// Voice-driven digital glitch/buffer override.
    /// Mid-band vowel proxy using energy ratio between 800Hz and 1200Hz bands strictly targets vowels.
    /// Evaluates probability in fixed 10ms windows to remain Sample Rate independent.
    /// </summary>
    private float VocalStutter(float x, float amount)
    {
        _glitchCapture[_glitchWritePos] = x;
        _glitchWritePos = (_glitchWritePos + 1) & GlitchCaptureMask;

        float abs = MathF.Abs(x);

        float lowBand = _glitchZcrFilterLow?.Transform(x) ?? x;
        float highBand = _glitchZcrFilterHigh?.Transform(x) ?? x;

        float e1 = lowBand * lowBand;
        float e2 = highBand * highBand;

        _glitchEnergy += (abs - _glitchEnergy) * 0.08f;

        float instantaneousRatio = e1 / (e2 + 0.0001f);
        _glitchRatio = 0.9f * _glitchRatio + 0.1f * instantaneousRatio;

        bool vowelLike = _glitchRatio > 1.2f && _glitchEnergy > 0.002f;

        if (--_triggerWindowTimer <= 0)
        {
            _triggerWindowTimer = _sampleRate / 100;

            if (!_glitchFrozen && _glitchCooldown <= 0 && vowelLike)
            {
                float triggerChance = amount * 0.015f;

                if ((_noise.NextWhite() + 0.5f) < triggerChance)
                {
                    _glitchFrozen = true;

                    float durationMs = 40f + amount * 260f;
                    _glitchRemain = (int)(durationMs * _sampleRate / 1000f);

                    float jitterMs = 25f + (_noise.NextWhite() + 0.5f) * 20f;
                    _glitchLoopLen = Math.Max(1, (int)(jitterMs * _sampleRate / 1000f));

                    _glitchLoopStart = (_glitchWritePos - _glitchLoopLen + _glitchCapture.Length) & GlitchCaptureMask;
                    _glitchPlayPos = 0;

                    _glitchCooldown = _glitchRemain + (_sampleRate / 2);
                }
            }
        }

        if (_glitchCooldown > 0)
            _glitchCooldown--;

        if (!_glitchFrozen)
            return x;

        int fade = _sampleRate * 2 / 1000;

        if (--_glitchRemain <= 0)
        {
            _glitchFrozen = false;
            return x;
        }

        float frozen = _glitchCapture[(_glitchLoopStart + _glitchPlayPos) & GlitchCaptureMask];
        frozen = Dsp.SoftClip(frozen * 1.1f);

        int left = _glitchLoopLen - _glitchPlayPos;

        if (left < fade)
            frozen *= Dsp.HannWindow(left, fade * 2);
        else if (_glitchPlayPos < fade)
            frozen *= Dsp.HannWindow(_glitchPlayPos, fade * 2);

        _glitchPlayPos = (_glitchPlayPos + 1) % _glitchLoopLen;

        if (_glitchRemain < fade)
        {
            float t = (float)_glitchRemain / fade;
            frozen = Dsp.EqualPowerCrossfade(x, frozen, t);
        }

        return frozen;
    }
}