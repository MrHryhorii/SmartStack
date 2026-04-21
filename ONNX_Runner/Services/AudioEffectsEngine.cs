using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Server-side audio effects engine for TTS.
/// High-performance, zero-allocation DSP implementation with strict hardware safety.
/// Features Deep Interpolation + real analog "life" through ThermalDrift and Pink Noise.
/// </summary>
public class AudioEffectsEngine(EffectsSettings config, int sampleRate)
{
    private readonly EffectsSettings _config = config;
    private readonly int _sampleRate = sampleRate;

    private readonly DelayBuffer _delay = new(4096);
    private DcBlocker _dcBlocker = new();

    // === Analog Life Primitives ===
    private NoiseGenerator _noise = new();
    private ThermalDrift _thermal = new();

    private readonly BiQuadFilter[] _filters = new BiQuadFilter[5];
    private int _filterCount;

    private VoiceEffectType _current = VoiceEffectType.None;

    // --- Isolated Effect States ---
    private float _ringPhase;
    private float _flangerPhase;
    private float _chorusPhase;
    private float _bcPhase;
    private float _bcHold;

    public void Reset()
    {
        _delay.Clear();
        _dcBlocker.Reset();

        // Fresh hardware "personality" on every reset
        _noise.Seed((uint)(DateTime.UtcNow.Ticks % uint.MaxValue));
        _noise.Reset();
        _thermal.Reset();

        _ringPhase = _flangerPhase = _chorusPhase = 0f;
        _bcPhase = _bcHold = 0f;

        _current = VoiceEffectType.None;
    }

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

        int filterCount = _filterCount;
        BiQuadFilter[] filters = _filters;

        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];
            float x = Dsp.KillDenormal(dry);

            // Evolve analog "life" every sample
            _thermal.Update(ref _noise);

            // Wet EQ (Deep Interpolation)
            if (filterCount > 0)
            {
                float eqSignal = x;
                for (int f = 0; f < filterCount; f++)
                    eqSignal = filters[f].Transform(eqSignal);
                x = x + (eqSignal - x) * mix;
            }

            // Native Effect Processing
            float wet = Process(type, x, mix);

            // DC blocking
            wet = _dcBlocker.Process(wet);

            // Parallel mix
            buffer[i] = dry + (wet - dry) * mix;
        }
    }

    private void Setup(VoiceEffectType type)
    {
        _filterCount = 0;
        // Nyquist frequency for safety clamping of filter frequencies
        float nyq = _sampleRate * 0.45f;
        // Clamp filter frequencies to Nyquist to prevent instability on low sample rates
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
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Process(VoiceEffectType type, float x, float mix)
    {
        return type switch
        {
            VoiceEffectType.Telephone => Telephone(x, mix),
            VoiceEffectType.Overdrive => Overdrive(x, mix),
            VoiceEffectType.Bitcrusher => Bitcrusher(x, mix),
            VoiceEffectType.RingModulator => RingMod(x, mix),
            VoiceEffectType.Flanger => Flanger(x, mix),
            VoiceEffectType.Chorus => Chorus(x, mix),
            _ => x
        };
    }

    // ====================== EFFECTS WITH ANALOG LIFE ======================

    private float Telephone(float x, float mix)
    {
        float noise = _noise.NextPink() * (0.0045f + _thermal.State * 0.0022f);
        float fx = Dsp.ShockleyDiode(x * 1.8f + noise);
        fx = Dsp.SoftClip(fx) * 0.95f;

        return x + (fx - x) * mix;
    }

    private static float Overdrive(float x, float mix)
    {
        float fx = Dsp.ShockleyDiode(x * 2.8f);
        fx = Dsp.SoftClip(fx);
        return x + (fx - x) * mix;
    }

    private float Bitcrusher(float x, float mix)
    {
        float jitter = _thermal.State * 0.007f;
        _bcPhase += 11025f / _sampleRate * (1f + jitter);

        if (_bcPhase >= 1f)
        {
            _bcPhase -= 1f;
            _bcHold = x;
        }

        const float levels = 16f;
        float crushed = MathF.Round(_bcHold * levels) / levels;
        crushed = crushed * 0.4f + _bcHold * 0.6f;

        return x + (crushed - x) * mix;
    }

    private float RingMod(float x, float mix)
    {
        float freq = 68f + _thermal.State * 2.2f;
        _ringPhase = Dsp.AdvancePhase(_ringPhase, freq, _sampleRate);

        float carrier = Dsp.Sine(_ringPhase);
        float fx = Dsp.SoftClip(x * carrier * 1.4f);

        float effectiveMix = mix * 0.75f;
        return x + (fx - x) * effectiveMix;
    }

    private float Flanger(float x, float mix)
    {
        _flangerPhase = Dsp.AdvancePhase(_flangerPhase, 0.45f, _sampleRate);

        float delayMs = 1.8f + Dsp.Sine(_flangerPhase) * 1.1f;
        float delayed = _delay.Read(delayMs * _sampleRate / 1000f);

        float fb = delayed * 0.68f * mix;
        _delay.Write(x + fb);

        float fx = delayed * 0.72f;
        return x + (fx - x) * mix;
    }

    private float Chorus(float x, float mix)
    {
        _chorusPhase = Dsp.AdvancePhase(_chorusPhase, 0.65f, _sampleRate);

        float wow = _thermal.State * 0.004f;

        float d1 = 14f + Dsp.Sine(_chorusPhase) * 4.5f;
        float d2 = 23f + Dsp.Sine(_chorusPhase + 2.1f) * 5.5f + wow;

        float s1 = _delay.Read(d1 * _sampleRate / 1000f);
        float s2 = _delay.Read(d2 * _sampleRate / 1000f);

        _delay.Write(x);

        float fx = s1 * 0.5f + s2 * 0.5f;
        float effectiveMix = mix * 0.60f;

        return x + (fx - x) * effectiveMix;
    }
}