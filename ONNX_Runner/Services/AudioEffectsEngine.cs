using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Server-side audio effects engine for TTS post-processing.
///
/// Architecture:
///   - Zero-allocation during processing: all buffers are pre-allocated at construction.
///   - Single mix point: dry/wet blending happens exclusively in <see cref="ApplyEffect"/>.
///     Individual effect methods return a pure wet signal with no mix applied.
///   - Analog life: <see cref="ThermalDrift"/> and <see cref="NoiseGenerator"/> introduce
///     subtle, time-varying modulation that prevents the digital "frozen" character.
///   - Hardware safety: denormal killing and DC blocking are applied unconditionally.
///
/// Effect chain per sample:
///   dry → KillDenormal → EQ (Deep Interpolation) → Effect → DcBlocker → wet mix → out
/// </summary>
public class AudioEffectsEngine(EffectsSettings config, int sampleRate)
{
    private readonly EffectsSettings _config = config;
    private readonly int _sampleRate = sampleRate;

    // --- Core DSP Primitives ---
    private readonly DelayBuffer _delay = new(4096);
    private DcBlocker _dcBlocker = new();

    // --- Analog Life Primitives ---
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
    private readonly float _bcStep = 11025f / sampleRate;

    // --- LoFiTape Oscillator Phases ---
    private float _lofiPhase;
    private float _flutterPhase;

    // --- LoFiTape IEC Filter States ---
    // One-pole LP filters for pre-emphasis, de-emphasis, and hiss shaping.
    // Coefficients are computed once in Setup(); states are reset between requests.
    private float _lofiPreState;
    private float _lofiDeState;
    private float _lofiHissState;
    private float _lofiPreCoeff;
    private float _lofiDeCoeff;
    private float _lofiHissCoeff;

    // --- NeuralStutter Capture Ring ---
    // 4096 samples: ~93ms at 44.1kHz, ~256ms at 16kHz.
    private readonly float[] _glitchCapture = new float[4096];
    private const int GlitchCaptureMask = 4095;
    private int _glitchWritePos;

    // --- NeuralStutter State Machine ---
    private bool _glitchFrozen;
    private int _glitchLoopStart;
    private int _glitchLoopLen;
    private int _glitchPlayPos;
    private int _glitchRemain;
    private int _glitchCooldown;

    // --- NeuralStutter Vowel Detector ---
    private float _glitchEnergy;
    private float _glitchZcr;
    private float _glitchPrevSample;
    private int _glitchZcrCount;
    private int _glitchZcrWindowLen;
    private int _glitchZcrWindowPos;

    // Intensity remapped to freeze duration for NeuralStutter (not dry/wet).
    private float _glitchIntensity;

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Resets all DSP state and re-seeds the noise generator.
    /// Call before each new TTS request to prevent inter-request bleed.
    /// </summary>
    public void Reset()
    {
        _delay.Clear();
        _dcBlocker.Reset();

        _noise.Seed((uint)(DateTime.UtcNow.Ticks % uint.MaxValue));
        _noise.Reset();
        _thermal.Reset();

        _ringPhase = _flangerPhase = _chorusPhase = _chorusPhase2 = 0f;
        _bcPhase = _bcHold = 0f;
        _lofiPhase = _flutterPhase = 0f;
        _lofiPreState = _lofiDeState = _lofiHissState = 0f;

        _current = VoiceEffectType.None;

        _glitchFrozen = false;
        _glitchCooldown = 0;
        _glitchEnergy = _glitchZcr = _glitchPrevSample = 0f;
        _glitchZcrCount = 0;
        _glitchZcrWindowLen = 0;
        _glitchZcrWindowPos = 0;
        _glitchLoopLen = 0;
        Array.Clear(_glitchCapture, 0, _glitchCapture.Length);
        _glitchWritePos = 0;
    }

    /// <summary>
    /// Processes an audio buffer in-place, applying the selected effect at the given intensity.
    /// </summary>
    /// <param name="buffer">Normalized float samples [-1, 1] to process in-place.</param>
    /// <param name="effect">Effect name matching <see cref="VoiceEffectType"/>. Falls back to config default.</param>
    /// <param name="intensity">Dry/wet mix ratio [0, 1]. Falls back to config default.</param>
    public void ApplyEffect(Span<float> buffer, string? effect = null, float? intensity = null)
    {
        if (!_config.EnableGlobalEffects) return;

        if (!Enum.TryParse(effect ?? _config.DefaultEffect, true, out VoiceEffectType type) ||
            type == VoiceEffectType.None)
            return;

        float mix = Math.Clamp(intensity ?? _config.DefaultIntensity, 0f, 1f);
        if (mix <= 0.001f) return;

        if (_current != type)
        {
            Setup(type);
            _delay.Clear();
            _dcBlocker.Reset();
            _current = type;
        }

        // NeuralStutter uses intensity as freeze duration, not dry/wet ratio.
        if (type == VoiceEffectType.NeuralStutter)
        {
            _glitchIntensity = mix;
            mix = 1.0f;
        }

        int filterCount = _filterCount;
        BiQuadFilter[] filters = _filters;

        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];
            float x = Dsp.KillDenormal(dry);

            _thermal.Update(ref _noise);

            if (filterCount > 0)
                for (int f = 0; f < filterCount; f++)
                    x = filters[f].Transform(x);

            float wet = Process(type, x);

            wet = type != VoiceEffectType.NeuralStutter ? _dcBlocker.Process(wet) : wet;

            buffer[i] = dry + (wet - dry) * mix;
        }
    }

    // =========================================================================
    // SETUP
    // =========================================================================

    /// <summary>
    /// Configures the EQ filter chain for the given effect type.
    /// Called once per effect change — never inside the sample loop.
    /// All cutoff frequencies are clamped below Nyquist to remain stable
    /// across TTS models with different sample rates (16kHz, 22kHz, etc.).
    /// </summary>
    private void Setup(VoiceEffectType type)
    {
        _filterCount = 0;

        float nyq = _sampleRate * 0.45f;
        float Safe(float f) => Math.Min(f, nyq);

        switch (type)
        {
            case VoiceEffectType.Telephone:
                // POTS bandpass: 300–3400 Hz with midrange presence peaks.
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(380f), 1.0f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(950f), 4.5f, 7.5f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(820f), 6.0f, 8.0f);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(3400f), 1.0f);
                break;

            case VoiceEffectType.Overdrive:
                // Subsonic cut before distortion to prevent low-frequency intermodulation.
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(180f), 0.8f);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, nyq, 0.707f);
                break;

            case VoiceEffectType.Bitcrusher:
            case VoiceEffectType.RingModulator:
                // Subsonic cut only — both effects rely on intentional high-frequency content.
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(130f), 0.707f);
                break;

            case VoiceEffectType.LoFiTape:
                // HP removes mechanical transport rumble.
                // Pre/de-emphasis and hiss are handled inside LoFiTape() using IEC 60094 curves.
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(100f), 0.707f);

                // IEC 60094 cassette time constant: 120µs → fc ≈ 1326 Hz.
                // Pre-emphasis and de-emphasis use identical coefficients — exact mirror response.
                // Hiss filter simulates the playback head's natural HF rolloff (~6kHz).
                float iecFc = 1326f;
                _lofiPreCoeff = 1f - MathF.Exp(-2f * MathF.PI * iecFc / _sampleRate);
                _lofiDeCoeff = _lofiPreCoeff;
                _lofiHissCoeff = 1f - MathF.Exp(-2f * MathF.PI * Safe(6000f) / _sampleRate);
                _lofiPreState = _lofiDeState = _lofiHissState = 0f;
                break;

            case VoiceEffectType.NeuralStutter:
                // No EQ — raw signal is required for accurate phoneme capture.
                _glitchFrozen = false;
                _glitchCooldown = 0;
                _glitchEnergy = 0f;
                _glitchZcr = 0f;
                _glitchPrevSample = 0f;
                _glitchZcrCount = 0;
                _glitchZcrWindowLen = Math.Max(1, _sampleRate / 100);
                _glitchZcrWindowPos = _glitchZcrWindowLen;
                _glitchLoopLen = Math.Max(1, _sampleRate * 30 / 1000);
                Array.Clear(_glitchCapture, 0, _glitchCapture.Length);
                _glitchWritePos = 0;
                break;
        }
    }

    // =========================================================================
    // EFFECT DISPATCH
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Process(VoiceEffectType type, float x) => type switch
    {
        VoiceEffectType.Telephone => Telephone(x),
        VoiceEffectType.Overdrive => Overdrive(x),
        VoiceEffectType.Bitcrusher => Bitcrusher(x),
        VoiceEffectType.RingModulator => RingMod(x),
        VoiceEffectType.Flanger => Flanger(x),
        VoiceEffectType.Chorus => Chorus(x),
        VoiceEffectType.LoFiTape => LoFiTape(x),
        VoiceEffectType.NeuralStutter => NeuralStutter(x),
        _ => x
    };

    // =========================================================================
    // EFFECT ALGORITHMS
    // =========================================================================

    /// <summary>
    /// POTS telephone distortion with thermally modulated line noise.
    /// Returns a pure wet signal.
    /// </summary>
    private float Telephone(float x)
    {
        float noise = _noise.NextPink() * (0.0045f + _thermal.State * 0.0022f);
        float fx = Dsp.ShockleyDiode(x * 1.8f + noise);
        return Dsp.SoftClip(fx) * 0.95f;
    }

    /// <summary>
    /// Tube/transistor overdrive with thermally drifting bias point.
    /// Returns a pure wet signal.
    /// </summary>
    private float Overdrive(float x)
    {
        float bias = _thermal.State * 0.04f;
        float fx = Dsp.ShockleyDiode(x * 2.8f + bias);
        return Dsp.SoftClip(fx);
    }

    /// <summary>
    /// Lo-fi bitcrusher: Zero-Order Hold decimation + 4-bit amplitude quantization.
    /// Returns the raw staircase waveform — no soft clipping applied.
    /// </summary>
    private float Bitcrusher(float x)
    {
        float jitter = _thermal.State * 0.005f;
        _bcPhase += _bcStep * (1f + jitter);

        if (_bcPhase >= 1f)
        {
            _bcPhase -= 1f;
            const float levels = 16f;
            _bcHold = MathF.Round(x * levels) / levels;
        }

        return _bcHold;
    }

    /// <summary>
    /// Analog ring modulator with thermally drifting carrier frequency.
    /// Returns a pure wet signal.
    /// </summary>
    private float RingMod(float x)
    {
        float freq = 68f + _thermal.State * 2.2f;
        _ringPhase = Dsp.AdvancePhase(_ringPhase, freq, _sampleRate);
        float carrier = Dsp.Sine(_ringPhase);
        return Dsp.SoftClip(x * carrier * 1.4f) * 0.9f;
    }

    /// <summary>
    /// Analog flanger: modulated delay line with feedback.
    /// LFO sweeps delay between 0.7ms and 2.9ms.
    /// Returns a pure wet signal.
    /// </summary>
    private float Flanger(float x)
    {
        _flangerPhase = Dsp.AdvancePhase(_flangerPhase, 0.45f, _sampleRate);
        float delayMs = 1.8f + Dsp.Sine(_flangerPhase) * 1.1f;
        float delayed = _delay.Read(delayMs * _sampleRate / 1000f);
        _delay.Write(Dsp.SoftClip(x + delayed * 0.68f));
        return x + delayed * 0.72f;
    }

    /// <summary>
    /// Analog chorus: two modulated delay lines with thermally drifting wow.
    /// Returns a pure wet signal.
    /// </summary>
    private float Chorus(float x)
    {
        _chorusPhase = Dsp.AdvancePhase(_chorusPhase, 0.55f, _sampleRate);
        _chorusPhase2 = Dsp.AdvancePhase(_chorusPhase2, 0.83f, _sampleRate);

        float wow = _thermal.State * 0.006f;
        float d1 = 15f + Dsp.Sine(_chorusPhase) * 6.0f + wow;
        float d2 = 24f + Dsp.Sine(_chorusPhase2) * 7.0f - wow;

        float s1 = _delay.Read(d1 * _sampleRate / 1000f);
        float s2 = _delay.Read(d2 * _sampleRate / 1000f);
        _delay.Write(x);

        return x + (s1 + s2) * 0.45f;
    }

    /// <summary>
    /// Cassette tape simulation following the IEC 60094 recording/playback chain.
    ///
    /// Signal path:
    ///   HP (Setup) → Pre-emphasis (IEC 120µs) → Tape saturation → Delay write
    ///   → Wow/flutter read → De-emphasis (IEC mirror) → Hiss (playback head filtered)
    /// </summary>
    private float LoFiTape(float x)
    {
        // Pre-emphasis: one-pole LP extracts low-frequency component;
        // the difference (x - LP) is the HF boost applied before tape saturation.
        _lofiPreState += _lofiPreCoeff * (x - _lofiPreState);
        _lofiPreState = Dsp.KillDenormal(_lofiPreState);
        float preEmph = x + (x - _lofiPreState) * 0.7f;

        // Tape oxide saturation with the pre-emphasized signal.
        float recorded = Dsp.SoftClip(preEmph * 1.4f);
        _delay.Write(recorded);

        // Wow: ±0.8ms at 1.2Hz — speed drift of the tape transport capstan.
        // Flutter: ±0.3ms at 9Hz — high-frequency mechanical instability.
        _lofiPhase = Dsp.AdvancePhase(_lofiPhase, 1.2f, _sampleRate);
        _flutterPhase = Dsp.AdvancePhase(_flutterPhase, 9.0f, _sampleRate);
        float wow = Dsp.Sine(_lofiPhase) * 0.8f;
        float flutter = Dsp.Sine(_flutterPhase) * 0.3f + _noise.NextWhite() * 0.05f;
        float pitchWarped = _delay.Read((5f + wow + flutter) * _sampleRate / 1000f);

        // De-emphasis: exact mirror of pre-emphasis — restores spectral balance on playback.
        _lofiDeState += _lofiDeCoeff * (pitchWarped - _lofiDeState);
        _lofiDeState = Dsp.KillDenormal(_lofiDeState);

        // Tape hiss filtered through the playback head LP (~6kHz) —
        // places the noise "behind" the signal, not on top of it.
        float rawHiss = _noise.NextPink() * (0.012f + _thermal.State * 0.005f);
        _lofiHissState += _lofiHissCoeff * (rawHiss - _lofiHissState);

        return (_lofiDeState + _lofiHissState) * 0.9f;
    }

    /// <summary>
    /// Digital freeze glitch: captures a vowel segment and loops it.
    /// Triggered by an energy + zero-crossing rate detector.
    /// Intensity controls freeze duration (50ms – 600ms), not dry/wet mix.
    /// </summary>
    private float NeuralStutter(float x)
    {
        // Continuously record into the capture ring so a fresh segment is always available.
        _glitchCapture[_glitchWritePos] = x;
        _glitchWritePos = (_glitchWritePos + 1) & GlitchCaptureMask;

        // Asymmetric envelope follower: fast attack, slow release.
        float xSq = x * x;
        _glitchEnergy += (xSq > _glitchEnergy ? 0.9f : 0.001f) * (xSq - _glitchEnergy);

        // Count zero-crossings to distinguish vowels (low ZCR) from consonants (high ZCR).
        if ((x >= 0f) != (_glitchPrevSample >= 0f)) _glitchZcrCount++;
        _glitchPrevSample = x;

        // Evaluate the detector once per 10ms window.
        if (--_glitchZcrWindowPos <= 0)
        {
            float rawZcr = (float)_glitchZcrCount / _glitchZcrWindowLen;
            _glitchZcr = 0.85f * _glitchZcr + 0.15f * rawZcr;
            _glitchZcrCount = 0;
            _glitchZcrWindowPos = _glitchZcrWindowLen;

            if (!_glitchFrozen && _glitchCooldown <= 0
                && _glitchEnergy > 0.004f
                && _glitchZcr < 0.08f
                && _noise.NextWhite() > 0.48f)
            {
                _glitchFrozen = true;
                _glitchRemain = (int)((50f + _glitchIntensity * 550f) * _sampleRate / 1000f);
                _glitchLoopStart = (_glitchWritePos - _glitchLoopLen + _glitchCapture.Length) & GlitchCaptureMask;
                _glitchPlayPos = 0;
                _glitchCooldown = _glitchRemain + (_sampleRate / 5);
            }
        }

        if (_glitchCooldown > 0) _glitchCooldown--;

        if (!_glitchFrozen) return x;

        int fadeLen = _sampleRate * 2 / 1000;

        if (--_glitchRemain <= 0)
        {
            _glitchFrozen = false;
            return x;
        }

        float frozen = _glitchCapture[(_glitchLoopStart + _glitchPlayPos) & GlitchCaptureMask];
        frozen = Dsp.SoftClip(frozen * 1.15f);

        // Cross-fade at loop boundaries to prevent clicks.
        int samplesLeftInLoop = _glitchLoopLen - _glitchPlayPos;
        if (samplesLeftInLoop < fadeLen)
            frozen *= (float)samplesLeftInLoop / fadeLen;
        else if (_glitchPlayPos < fadeLen)
            frozen *= (float)_glitchPlayPos / fadeLen;

        _glitchPlayPos = (_glitchPlayPos + 1) % _glitchLoopLen;

        // Cross-fade back to live signal at the end of the freeze.
        if (_glitchRemain < fadeLen)
        {
            float t = (float)_glitchRemain / fadeLen;
            frozen = frozen * t + x * (1f - t);
        }

        return frozen;
    }
}