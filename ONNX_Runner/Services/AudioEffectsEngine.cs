using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Server-side audio effects engine. 
/// Operates on a Zero-Allocation principle (prevents allocating new objects in memory 
/// during audio processing to minimize Garbage Collector pressure and ensure low latency).
/// </summary>
public class AudioEffectsEngine(EffectsSettings config, int sampleRate)
{
    private readonly EffectsSettings _config = config;
    private readonly int _sampleRate = sampleRate;

    // Volume compensation (Make-up gain). 
    // DSP effects (especially extreme equalizers and bitcrushers) consume signal energy, 
    // so we apply a designated volume boost at the end of the processing chain.
    private const float GainTelephone = 1.6f;
    private const float GainOverdrive = 0.9f;
    private const float GainBitcrusher = 1.2f;
    private const float GainRingModulator = 1.2f;
    private const float GainFlanger = 1.05f;
    private const float GainChorus = 1.05f;

    // Internal LFO (Low-Frequency Oscillator) phases for time-varying modulation effects
    private float _ringPhase;
    private float _lfoPhase2;
    private float _flangerPhase;
    private float _chorusPhase;

    // Variables for the 8-bit (Arcade) effect and analog chaos simulation
    private float _zohPhase;
    private float _zohHold;
    private uint _prngState = 12345;  // Lightweight Pseudo-Random Number Generator (PRNG)
    private float _chorusDrift;       // Smooth analog drift for the Chorus effect

    // Memory state for the Flanger feedback loop
    private float _flangerFeedbackState;

    // Ring buffer. Stores the last fractions of a second of audio to create delays/echoes 
    // without allocating new memory arrays on each request.
    private readonly float[] _delayBuffer = new float[4096];
    private int _delayWritePos;

    // List of cascaded BiQuad frequency filters (Equalizers)
    private readonly List<BiQuadFilter> _filters = new(4);
    private BiQuadFilter? _odPostFilter;

    private VoiceEffectType _currentEffect = VoiceEffectType.None;
    private bool _eqInitialized = false;

    /// <summary>
    /// Completely clears the audio history state.
    /// CRITICAL: Must be called before processing a new audio stream so that delayed 
    /// chunks of previous audio don't bleed into the new generation.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
        _ringPhase = 0;
        _lfoPhase2 = 0;
        _flangerPhase = 0;
        _chorusPhase = 0;
        _zohPhase = 0;
        _zohHold = 0;
        _prngState = 12345;
        _chorusDrift = 0f;
        _flangerFeedbackState = 0f;
        _delayWritePos = 0;
        _eqInitialized = false;
    }

    /// <summary>
    /// Main processing method. Applies the selected DSP effect to the audio buffer in-place.
    /// </summary>
    public void ApplyEffect(Span<float> buffer, string? requestedEffect = null, float? requestedIntensity = null)
    {
        if (!_config.EnableGlobalEffects) return;

        string effectString = requestedEffect ?? _config.DefaultEffect;
        if (!Enum.TryParse(effectString, true, out VoiceEffectType effectType) || effectType == VoiceEffectType.None)
            return;

        float intensity = Math.Clamp(requestedIntensity ?? _config.DefaultIntensity, 0f, 1f);
        if (intensity <= 0.001f) return;

        // If the effect is changed "on the fly", reconfigure the equalizers and flush the delay buffer
        if (!_eqInitialized || _currentEffect != effectType)
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            SetupEqualizer(effectType);
            _eqInitialized = true;
            _currentEffect = effectType;
        }

        // Process the audio signal sample by sample
        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];    // Original unprocessed signal (Dry)
            float eqSignal = dry;

            // 1. Equalization: shape the sound profile (e.g., cutting muddy bass frequencies)
            foreach (var filter in _filters)
                eqSignal = filter.Transform(eqSignal);

            // 2. Artistic DSP processing: apply the core effect algorithm
            float wet = Process(_currentEffect, eqSignal, intensity);

            // 3. Dry/Wet Mix: blend the original signal with the effect based on requested intensity
            buffer[i] = dry + (wet - dry) * intensity;
        }
    }

    /// <summary>
    /// Configures cascaded equalizers to shape the base "profile" of the sound.
    /// </summary>
    private void SetupEqualizer(VoiceEffectType type)
    {
        _filters.Clear();
        float nyquist = _sampleRate / 2.0f * 0.95f; // Maximum safely representable frequency (Nyquist limit)
        float SafeFreq(float target) => Math.Min(target, nyquist);

        switch (type)
        {
            case VoiceEffectType.Telephone:
                // Cheap speaker simulation: aggressively cut extreme lows and highs, boost the mid-range
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 300f, 0.707f));
                _filters.Add(BiQuadFilter.PeakingEQ(_sampleRate, SafeFreq(2000f), 3.0f, 5f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(4000f), 1.2f));
                break;

            case VoiceEffectType.Overdrive:
                // Pre-distortion prep: cut bass to prevent muddiness, and prepare a post-filter 
                // to remove harsh digital "fizz" after clipping.
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 400f, 1.0f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(6500f), 0.707f));
                _odPostFilter = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(8500f), 0.707f);
                break;

            case VoiceEffectType.Bitcrusher:
            case VoiceEffectType.RingModulator:
                // Light low-end rumble cleanup before applying aggressive mathematical modulation
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 150f, 0.707f));
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Process(VoiceEffectType type, float x, float intensity)
    {
        return type switch
        {
            // Saturation.Tape acts as a soft-clipper to prevent harsh digital peaking
            VoiceEffectType.Telephone => Saturation.Tape(Saturation.AsymmetricTube(x * 2.5f)) * GainTelephone,
            VoiceEffectType.Overdrive => Overdrive(x, intensity) * GainOverdrive,
            VoiceEffectType.Bitcrusher => Bitcrusher(x) * GainBitcrusher,
            VoiceEffectType.RingModulator => RingModulator(x) * GainRingModulator,
            VoiceEffectType.Flanger => Flanger(x) * GainFlanger,
            VoiceEffectType.Chorus => Chorus(x) * GainChorus,
            _ => x
        };
    }

    /// <summary>
    /// Overdrive (Loudspeaker / Walkie-Talkie). Applies hard clipping distortion.
    /// </summary>
    private float Overdrive(float x, float intensity)
    {
        // Higher user intensity = more aggressive drive multiplier
        float drive = 2.0f + (intensity * 6.0f);
        float distorted = Saturation.Cubic(x * drive);

        // Smooth the sound to prevent high-frequency ear-piercing artifacts
        if (_odPostFilter != null)
            distorted = _odPostFilter.Transform(distorted);

        return Saturation.Tape(distorted);
    }

    /// <summary>
    /// Ring Modulator (Robot / Dalek). Multiplies the voice by a fast pulsating sine wave.
    /// </summary>
    private float RingModulator(float x)
    {
        // The modulation frequency slightly "drifts" (500 ± 100 Hz) to make the robot sound 
        // more organic and unsettling, rather than perfectly monotonic.
        _lfoPhase2 += MathF.PI * 2f * 0.5f / _sampleRate;
        if (_lfoPhase2 > MathF.PI * 2f) _lfoPhase2 -= MathF.PI * 2f;

        float currentFreq = 500f + MathF.Sin(_lfoPhase2) * 100f;

        _ringPhase += MathF.PI * 2f * currentFreq / _sampleRate;
        if (_ringPhase > MathF.PI * 2f) _ringPhase -= MathF.PI * 2f;

        return x * MathF.Sin(_ringPhase);
    }

    /// <summary>
    /// Bitcrusher (8-bit / Arcade). Artificially degrades audio quality by reducing sample rate and bit depth.
    /// </summary>
    private float Bitcrusher(float x)
    {
        // Zero-Order Hold (ZOH) Downsampling: intentionally skip samples to simulate an 8kHz sample rate
        _zohPhase += 8000f / _sampleRate;
        if (_zohPhase >= 1.0f)
        {
            _zohHold = x;
            _zohPhase -= 1.0f;
        }

        // Generate micro-noise (dither) to give the sound the textured "grit" of a real retro console chip
        _prngState = 1664525 * _prngState + 1013904223;
        float dither = (((float)_prngState / uint.MaxValue) - 0.5f) * 0.03f;

        // Quantization: Reduce the volume resolution down to 16 distinct levels (4-bit depth simulation)
        float levels = 16f;
        return MathF.Round((_zohHold + dither) * levels) / levels;
    }

    /// <summary>
    /// Flanger (Space Tube / Jet Plane). Mixes the signal with a slightly delayed version of itself, 
    /// sweeping the delay time using an LFO to create moving comb-filter notches.
    /// </summary>
    private float Flanger(float x)
    {
        _flangerPhase += MathF.PI * 2f * 0.5f / _sampleRate;
        if (_flangerPhase > MathF.PI * 2f) _flangerPhase -= MathF.PI * 2f;

        float delayMs = 3.0f + MathF.Sin(_flangerPhase) * 2.0f;
        float delayed = ReadFractionalDelay(delayMs * _sampleRate / 1000f);

        // Simulate an old analog bucket-brigade device (BBD): slightly blur the feedback signal returning to memory
        float feedback = 0.7f;
        float fbInput = delayed * feedback;
        _flangerFeedbackState = 0.5f * _flangerFeedbackState + 0.5f * fbInput;

        // Prevents energy buildup and "digital explosions" (clipping) if the input signal is too loud.
        float input = x + _flangerFeedbackState;
        _delayBuffer[_delayWritePos] = Math.Clamp(input, -1.0f, 1.0f);
        _delayWritePos = (_delayWritePos + 1) & 4095;

        // 50/50 Dry/Wet mix for the classic sweeping comb-filter effect
        float wet = (x * 0.7f) + (delayed * 0.7f);
        return Math.Clamp(wet, -1.0f, 1.0f);
    }

    /// <summary>
    /// Chorus. Thickens the sound by creating the illusion of multiple voices playing simultaneously, 
    /// slightly out of tune and time.
    /// </summary>
    private float Chorus(float x)
    {
        _chorusPhase += MathF.PI * 2f * 0.8f / _sampleRate;
        if (_chorusPhase > MathF.PI * 2f) _chorusPhase -= MathF.PI * 2f;

        // Create a smooth analog "wow and flutter" drift (makes the voices feel alive)
        _prngState = 1664525 * _prngState + 1013904223;
        float analogDrift = (((float)_prngState / uint.MaxValue) - 0.5f) * 0.04f;
        _chorusDrift = 0.998f * _chorusDrift + 0.002f * analogDrift;

        // Tap two additional "voices" from the delay buffer with oscillating delay times
        float delayMs1 = 20.0f + MathF.Sin(_chorusPhase) * 6.0f;
        float delayed1 = ReadFractionalDelay(delayMs1 * _sampleRate / 1000f);

        float delayMs2 = 25.0f + MathF.Cos(_chorusPhase + _chorusDrift) * 7.0f;
        float delayed2 = ReadFractionalDelay(delayMs2 * _sampleRate / 1000f);

        _delayBuffer[_delayWritePos] = x;
        _delayWritePos = (_delayWritePos + 1) & 4095;

        // Mix: less original voice, more delayed voices for a thick ensemble effect
        float wet = (x * 0.4f) + (delayed1 * 0.3f) + (delayed2 * 0.3f);
        return Math.Clamp(wet, -1.0f, 1.0f);
    }

    /// <summary>
    /// Safely reads a past sample from the ring buffer.
    /// Uses linear interpolation so that sub-sample delay time changes don't create audible clicks or zipper noise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ReadFractionalDelay(float delaySamples)
    {
        float readPos = _delayWritePos - delaySamples;
        if (readPos < 0) readPos += 4096;

        int p1 = (int)readPos & 4095;
        int p2 = (p1 + 1) & 4095;
        float frac = readPos - (int)readPos;

        return (_delayBuffer[p1] * (1.0f - frac)) + (_delayBuffer[p2] * frac);
    }
}

/// <summary>
/// Mathematical tools for wave shaping (adds analog warmth and distortion).
/// </summary>
file static class Saturation
{
    // Soft clipping (simulates magnetic tape saturation, gracefully rounding off volume peaks)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tape(float x) => MathF.Tanh(x);

    // Asymmetric distortion (simulates warm vacuum tube harmonics)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AsymmetricTube(float x) => x > 0 ? MathF.Tanh(x) : (MathF.Exp(x) - 1.0f);

    // Cubic overdrive (hard solid-state distortion)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cubic(float x)
    {
        float clamped = x > 1.0f ? 1.0f : (x < -1.0f ? -1.0f : x);
        return clamped - clamped * clamped * clamped / 3.0f;
    }
}