using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Server-side audio effects engine optimized for stateless TTS requests.
/// Acts as the "Sound Designer", routing audio through the primitives of AnalogDspCore
/// to create expressive, character-driven Virtual Analog effects.
/// </summary>
public class AudioEffectsEngine
{
    private readonly EffectsSettings _config;
    private readonly int _sampleRate;

    // Output gain compensation
    private const float GainTelephone = 1.6f;
    private const float GainOverdrive = 0.9f;
    private const float GainBitcrusher = 1.2f;
    private const float GainRingModulator = 1.2f;
    private const float GainFlanger = 1.05f;
    private const float GainChorus = 1.05f;

    // --- DSP Primitives (The Toolbox) ---
    private NoiseGenerator _noise;
    private ThermalDrift _thermal;
    private readonly DelayLine _delayLine;

    // Dedicated LFOs for modulation
    private Lfo _ringLfo;
    private Lfo _flangerLfo;
    private Lfo _chorusLfo;
    private Lfo _chorusWowLfo;

    // Physical Component Tolerances (±5%)
    private float _toleranceA;
    private float _toleranceB;

    // Dedicated One-Pole Filters for Analog Emulation
    private OnePoleFilter _rcFilter; // For Bitcrusher reconstruction
    private OnePoleFilter _bbd1, _bbd2, _bbd3; // For Flanger BBD cascade
    private readonly float _rcCoeff;

    // Bitcrusher ZOH State
    private float _zohPhase;
    private float _zohHold;
    private float _zohOut;

    // Fixed-size EQ filter bank (Zero-Allocation)
    private readonly BiQuadFilter[] _filterBank = new BiQuadFilter[5];
    private int _filterCount;
    private BiQuadFilter? _odPostFilter;

    private VoiceEffectType _currentEffect = VoiceEffectType.None;
    private bool _eqInitialized = false;

    public AudioEffectsEngine(EffectsSettings config, int sampleRate)
    {
        _config = config;
        _sampleRate = sampleRate;
        _delayLine = new DelayLine(4096);
        _rcCoeff = OnePoleFilter.CalculateAlpha(sampleRate / 11f, sampleRate);
        Reset();
    }

    /// <summary>
    /// Hard reset for stateless TTS generation. Generates new hardware tolerances.
    /// </summary>
    public void Reset()
    {
        _delayLine.Clear();

        _noise.Seed((uint)(DateTime.UtcNow.Ticks % uint.MaxValue));
        _noise.Reset();

        _toleranceA = _noise.NextWhite() * 0.05f;
        _toleranceB = _noise.NextWhite() * 0.05f;

        _thermal.Reset();
        _ringLfo.Reset();
        _flangerLfo.Reset();
        _chorusLfo.Reset();
        _chorusWowLfo.Reset();

        _rcFilter.Reset();
        _bbd1.Reset();
        _bbd2.Reset();
        _bbd3.Reset();

        _zohPhase = _zohHold = _zohOut = 0f;

        _eqInitialized = false;
        _currentEffect = VoiceEffectType.None;
    }

    public void ApplyEffect(Span<float> buffer, string? requestedEffect = null, float? requestedIntensity = null)
    {
        if (!_config.EnableGlobalEffects) return;

        if (!Enum.TryParse(requestedEffect ?? _config.DefaultEffect, true, out VoiceEffectType effectType) || effectType == VoiceEffectType.None)
            return;

        float intensity = Math.Clamp(requestedIntensity ?? _config.DefaultIntensity, 0f, 1f);
        if (intensity <= 0.001f) return;

        if (!_eqInitialized || _currentEffect != effectType)
        {
            SetupEqualizer(effectType);
            _eqInitialized = true;
            _currentEffect = effectType;
        }

        int filterCount = _filterCount;
        BiQuadFilter[] filters = _filterBank;

        for (int i = 0; i < buffer.Length; i++)
        {
            // Evolve global physical state (Brownian drift)
            _thermal.Update(ref _noise);

            float dry = buffer[i];
            float eqSignal = dry;

            for (int f = 0; f < filterCount; f++)
                eqSignal = filters[f].Transform(eqSignal);

            float wet = Process(_currentEffect, eqSignal);

            // Parallel Mix
            buffer[i] = dry + (wet - dry) * intensity;
        }
    }

    private void SetupEqualizer(VoiceEffectType type)
    {
        _filterCount = 0;
        _odPostFilter = null;

        float nyquist = _sampleRate / 2f * 0.95f;
        float Safe(float f) => Math.Min(f, nyquist);

        switch (type)
        {
            case VoiceEffectType.Telephone:
                // Hard plastic handset chamber resonances
                _filterBank[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, 400f, 1.2f);
                _filterBank[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, 1000f, 5.0f, 6f);
                _filterBank[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(2500f), 3.0f, 4f);
                _filterBank[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(3400f), 1.2f);
                break;

            case VoiceEffectType.Overdrive:
                _filterBank[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, 200f, 1.0f);
                _filterBank[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, 1500f, 1.0f, 2f);
                _filterBank[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(5000f), 0.707f);
                break;

            case VoiceEffectType.Bitcrusher:
            case VoiceEffectType.RingModulator:
                _filterBank[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, 150f, 0.707f);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Process(VoiceEffectType type, float x)
    {
        return type switch
        {
            VoiceEffectType.Telephone => Telephone(x) * GainTelephone,
            VoiceEffectType.Overdrive => Overdrive(x) * GainOverdrive,
            VoiceEffectType.Bitcrusher => Bitcrusher(x) * GainBitcrusher,
            VoiceEffectType.RingModulator => RingModulator(x) * GainRingModulator,
            VoiceEffectType.Flanger => Flanger(x) * GainFlanger,
            VoiceEffectType.Chorus => Chorus(x) * GainChorus,
            _ => x
        };
    }

    // --- Sound Design Effect Implementations ---

    private float Telephone(float x)
    {
        // 1/f noise mixed with Brownian thermal variation
        float noise = _noise.NextPink() * (0.005f + _thermal.State * 0.002f);
        // Drives into the Shockley diode for authentic carbon mic crackle
        return Saturation.Tape(Saturation.ShockleyHybrid(x * 1.8f + noise));
    }

    private float Overdrive(float x)
    {
        float distorted = Saturation.ShockleyHybrid(x * 3.0f);

        if (_odPostFilter != null)
            distorted = _odPostFilter.Transform(distorted);

        return Saturation.Tape(distorted);
    }

    private float RingModulator(float x)
    {
        // Low 60Hz carrier gives a harsh, robotic growl (amplitude modulation)
        float driftFreq = 60f + _thermal.State * 1.5f;
        _ringLfo.AdvancePhase(driftFreq, _sampleRate);

        return Saturation.Tape(x * _ringLfo.Sine() * 1.5f);
    }

    private float Bitcrusher(float x)
    {
        float clockJitter = _thermal.State * 0.008f;

        // Arcade Style: Low sampling rate to induce metallic aliasing
        _zohPhase += 5500f / _sampleRate * (1f + clockJitter);
        if (_zohPhase >= 1f)
        {
            _zohPhase -= 1f;
            _zohHold = x;

            const float levels = 10f; // Approx 3-4 bit depth
            // Raw truncation. No dither or noise shaping to preserve the 80s grit.
            _zohOut = MathF.Round(_zohHold * levels) / levels;
        }

        // Apply reconstruction filter, but mix with raw signal for aggressive bite
        float smoothOut = _rcFilter.Process(_zohOut, _rcCoeff);
        return _zohOut * 0.5f + smoothOut * 0.5f;
    }

    private float Flanger(float x)
    {
        _flangerLfo.AdvancePhase(0.5f, _sampleRate);

        float delayMs = 1.5f + _flangerLfo.Sine() * 1.0f;
        float delayed = _delayLine.ReadLinear(delayMs * _sampleRate / 1000f);

        const float feedback = 0.75f;
        float fbInput = delayed * feedback;

        // BBD charge transfer loss using the new OnePoleFilter struct
        float dynamicAlpha = 0.90f - MathF.Abs(fbInput) * 0.05f;
        float bbdOut = _bbd1.Process(fbInput, dynamicAlpha);
        bbdOut = _bbd2.Process(bbdOut, dynamicAlpha);
        bbdOut = _bbd3.Process(bbdOut, dynamicAlpha);

        bbdOut = Saturation.Tape(bbdOut * 1.1f);
        _delayLine.Write(x + bbdOut);

        const float mix = 0.707f;
        return Math.Clamp(x * mix + bbdOut * mix, -1f, 1f);
    }

    private float Chorus(float x)
    {
        _chorusLfo.AdvancePhase(0.8f, _sampleRate);

        // Asynchronous tape wow driven by thermal drift
        float wowSpeed = 0.2f + _thermal.State * 0.05f;
        _chorusWowLfo.AdvancePhase(wowSpeed, _sampleRate);
        float wowDrift = _chorusWowLfo.Sine() * 0.005f;

        // Sine and Cosine offsets ensure voices swing in different directions
        float sweep1 = _chorusLfo.Sine();
        float sweep2 = _chorusLfo.Sine(1.57f); // 90-degree phase offset

        // Tolerances affect both delay length and gain
        float baseD1 = 15f * (1f + _toleranceA);
        float baseD2 = 24f * (1f + _toleranceB);

        float d1 = baseD1 + sweep1 * 3f;
        float d2 = baseD2 + sweep2 * 4f + wowDrift;

        float s1 = _delayLine.ReadLinear(d1 * _sampleRate / 1000f) * (1f + _toleranceA);
        float s2 = _delayLine.ReadLinear(d2 * _sampleRate / 1000f) * (1f + _toleranceB);

        _delayLine.Write(x);
        return Math.Clamp(x * 0.4f + s1 * 0.4f + s2 * 0.4f, -1f, 1f);
    }
}