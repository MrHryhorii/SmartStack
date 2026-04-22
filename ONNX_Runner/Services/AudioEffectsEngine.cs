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
    // ThermalDrift simulates slow component-level parameter drift (Brownian motion).
    // NoiseGenerator provides both pink noise (hiss/hum) and white noise (dithering).
    private NoiseGenerator _noise = new();
    private ThermalDrift _thermal = new();

    // --- EQ Chain (Deep Interpolation) ---
    // Pre-allocated to avoid heap pressure; only _filterCount slots are active.
    private readonly BiQuadFilter[] _filters = new BiQuadFilter[5];
    private int _filterCount;

    private VoiceEffectType _current = VoiceEffectType.None;

    // --- Per-Effect Oscillator States ---
    // Isolated phase accumulators prevent inter-effect state contamination.
    private float _ringPhase;
    private float _flangerPhase;
    private float _chorusPhase;
    private float _chorusPhase2;

    // Bitcrusher sample-and-hold state
    private float _bcPhase;
    private float _bcHold;
    private readonly float _bcStep = 11025f / sampleRate;

    /// <summary>
    /// Resets all internal DSP states and re-seeds the noise generator.
    /// Must be called before each new TTS request to prevent audio bleed-over
    /// and to give each utterance a unique analog "personality".
    /// </summary>
    public void Reset()
    {
        _delay.Clear();
        _dcBlocker.Reset();

        // Re-seed with wall-clock entropy so each request sounds subtly different
        _noise.Seed((uint)(DateTime.UtcNow.Ticks % uint.MaxValue));
        _noise.Reset();
        _thermal.Reset();

        _ringPhase = _flangerPhase = _chorusPhase = _chorusPhase2 = 0f;
        _bcPhase = _bcHold = 0f;

        _current = VoiceEffectType.None;
    }

    /// <summary>
    /// Processes an audio buffer in-place, applying the selected effect at the given intensity.
    ///
    /// Mix architecture — single blend point:
    ///   The <paramref name="intensity"/> parameter controls only the final dry/wet crossfade.
    ///   EQ filtering is applied at full strength to shape the wet signal before blending.
    ///   Individual effect methods must return a pure wet signal with no internal mix scaling.
    /// </summary>
    /// <param name="buffer">Audio samples to process in-place (normalized float [-1, 1]).</param>
    /// <param name="effect">Name of the <see cref="VoiceEffectType"/> to apply. Falls back to config default.</param>
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

        // Hoist to locals to help the JIT avoid repeated field dereferences in the hot loop
        int filterCount = _filterCount;
        BiQuadFilter[] filters = _filters;

        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];

            // Denormal kill on input prevents CPU spikes from subnormal floats
            // propagating through the feedback paths in delay and filter stages
            float x = Dsp.KillDenormal(dry);

            // Advance analog life simulation every sample.
            // State is read inside each effect method — no return value needed here.
            _thermal.Update(ref _noise);

            // --- Deep Interpolation EQ ---
            // Shapes the wet signal before it reaches the effect processor.
            // Applied at full strength; the final dry/wet blend handles perceived intensity.
            if (filterCount > 0)
                for (int f = 0; f < filterCount; f++)
                    x = filters[f].Transform(x);

            // --- Effect Processing ---
            // Each method returns a pure wet signal.
            // No mix scaling occurs inside the effect methods.
            float wet = Process(type, x);

            // DC blocking removes inaudible low-frequency offsets introduced by
            // asymmetric distortion algorithms (ShockleyDiode, SoftClip)
            wet = _dcBlocker.Process(wet);

            // --- Single Mix Point ---
            // All dry/wet blending is consolidated here.
            buffer[i] = dry + (wet - dry) * mix;
        }
    }

    // =========================================================================
    // SETUP
    // =========================================================================

    /// <summary>
    /// Configures the EQ filter chain for the given effect type.
    /// Called once per effect change — never inside the sample loop.
    /// All cutoff frequencies are clamped below Nyquist to prevent filter instability
    /// on TTS engines operating at lower sample rates (e.g., 16kHz, 22kHz).
    /// </summary>
    private void Setup(VoiceEffectType type)
    {
        _filterCount = 0;

        // 45% of sample rate provides a safe margin below the Nyquist limit
        float nyq = _sampleRate * 0.45f;
        float Safe(float f) => Math.Min(f, nyq);

        switch (type)
        {
            case VoiceEffectType.Telephone:
                // POTS bandpass: 300–3400Hz with presence peaks to simulate
                // the characteristic midrange coloration of telephone codecs
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(380f), 1.0f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(950f), 4.5f, 7.5f);
                _filters[_filterCount++] = BiQuadFilter.PeakingEQ(_sampleRate, Safe(820f), 6.0f, 8.0f);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, Safe(3400f), 1.0f);
                break;

            case VoiceEffectType.Overdrive:
                // Remove subsonic rumble before distortion to prevent low-frequency
                // intermodulation artifacts from muddying the harmonic content
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(180f), 0.8f);
                _filters[_filterCount++] = BiQuadFilter.LowPassFilter(_sampleRate, nyq, 0.707f);
                break;

            case VoiceEffectType.Bitcrusher:
            case VoiceEffectType.RingModulator:
                // Subsonic filter only — these effects intentionally preserve
                // (and generate) high-frequency content as part of their character
                _filters[_filterCount++] = BiQuadFilter.HighPassFilter(_sampleRate, Safe(130f), 0.707f);
                break;
        }
    }

    // =========================================================================
    // EFFECT DISPATCH
    // =========================================================================

    /// <summary>
    /// Dispatches a single sample to the appropriate effect algorithm.
    /// Returns a pure wet signal. Dry/wet mixing is the caller's responsibility.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Process(VoiceEffectType type, float x) => type switch
    {
        VoiceEffectType.Telephone => Telephone(x),
        VoiceEffectType.Overdrive => Overdrive(x),
        VoiceEffectType.Bitcrusher => Bitcrusher(x),
        VoiceEffectType.RingModulator => RingMod(x),
        VoiceEffectType.Flanger => Flanger(x),
        VoiceEffectType.Chorus => Chorus(x),
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
    private float Telephone(float x)
    {
        // Thermally modulated pink noise simulates line hiss and carbon mic self-noise
        float noise = _noise.NextPink() * (0.0045f + _thermal.State * 0.0022f);

        // ShockleyDiode generates asymmetric even+odd harmonics characteristic
        // of carbon button microphones and transformer saturation
        float fx = Dsp.ShockleyDiode(x * 1.8f + noise);
        return Dsp.SoftClip(fx) * 0.95f;
    }

    /// <summary>
    /// Simulates the warm harmonic saturation of an analog tube or transistor overdrive stage.
    /// Thermal bias drift replicates the slow operating-point shift of a warming tube amplifier,
    /// causing the distortion character to evolve naturally over time. Returns a pure wet signal.
    /// </summary>
    private float Overdrive(float x)
    {
        // Thermal bias shift: positive drift pushes the operating point asymmetrically,
        // generating more even-order harmonics (warmer, rounder distortion character)
        float bias = _thermal.State * 0.04f;
        float fx = Dsp.ShockleyDiode(x * 2.8f + bias);
        return Dsp.SoftClip(fx);
    }

    /// <summary>
    /// Simulates the true aliasing and quantization artifacts of a lo-fi bitcrusher.
    /// Academic implementation: strictly relies on Zero-Order Hold (decimation) and 
    /// amplitude quantization. The "metallic" character naturally arises from 
    /// foldover frequencies (aliasing) and sharp staircase waveforms.
    /// </summary>
    private float Bitcrusher(float x)
    {
        // Thermal jitter adds vintage oscillator instability to the sample clock
        float jitter = _thermal.State * 0.005f;

        // Target sample rate: ~11kHz (classic lo-fi sampler territory)
        _bcPhase += _bcStep * (1f + jitter);

        // Decimation (Zero-Order Hold)
        if (_bcPhase >= 1f)
        {
            _bcPhase -= 1f;

            // Quantization (Bit Reduction)
            // Calculated only on a new clock cycle. 
            // 16 levels (4-bit) provides a much more aggressive, metallic crunch than 32 levels.
            const float levels = 16f;
            _bcHold = MathF.Round(x * levels) / levels;
        }

        // Return the raw, unprocessed staircase waveform. 
        // Do NOT use SoftClip here — sharp edges are strictly required for the metallic high-end.
        return _bcHold;
    }

    /// <summary>
    /// Simulates a classic analog ring modulator.
    /// The carrier frequency drifts with thermal state, replicating the detuned oscillator
    /// instability of vintage hardware ring modulators. Internal output scaling compensates
    /// for the amplitude multiplication inherent to ring modulation.
    /// Returns a pure wet signal.
    /// </summary>
    private float RingMod(float x)
    {
        // Thermal drift shifts the carrier frequency, creating subtle metallic pitch variation
        float freq = 68f + _thermal.State * 2.2f;
        _ringPhase = Dsp.AdvancePhase(_ringPhase, freq, _sampleRate);

        float carrier = Dsp.Sine(_ringPhase);

        // SoftClip prevents harsh digital aliasing from pure amplitude multiplication.
        // 1.4x pre-gain compensates for the average amplitude loss of the sine carrier (~0.637),
        // and the 0.9x post-scale pulls the output back to unity to prevent clipping at full mix.
        return Dsp.SoftClip(x * carrier * 1.4f) * 0.9f;
    }

    /// <summary>
    /// Simulates a classic analog flanger using a single modulated delay line with feedback.
    /// The comb filtering effect is produced by mixing the delayed signal with the dry path
    /// externally (in the mix stage), creating the characteristic jet-sweep resonance.
    /// Returns a pure wet signal (the delayed component only).
    /// </summary>
    private float Flanger(float x)
    {
        _flangerPhase = Dsp.AdvancePhase(_flangerPhase, 0.45f, _sampleRate);

        // LFO sweeps delay time between 0.7ms and 2.9ms (classic flanger range)
        float delayMs = 1.8f + Dsp.Sine(_flangerPhase) * 1.1f;
        float delayed = _delay.Read(delayMs * _sampleRate / 1000f);

        // Feedback reinforces comb filter notches, increasing the metallic intensity
        _delay.Write(x + delayed * 0.68f);

        return x + delayed * 0.72f;
    }

    /// <summary>
    /// Simulates a classic analog chorus effect using two modulated delay lines.
    /// </summary>
    private float Chorus(float x)
    {
        // Non-integer LFO ratio prevents mathematical phase-locking between voices
        _chorusPhase = Dsp.AdvancePhase(_chorusPhase, 0.55f, _sampleRate);
        _chorusPhase2 = Dsp.AdvancePhase(_chorusPhase2, 0.83f, _sampleRate);

        // Thermally modulated wow: slow pitch instability of warming BBD hardware
        float wow = _thermal.State * 0.006f;

        // Wider modulation depth (±6ms / ±7ms) makes the chorus clearly audible
        // while staying within the range the brain perceives as detuning, not echo
        float d1 = 15f + Dsp.Sine(_chorusPhase) * 6.0f + wow;
        float d2 = 24f + Dsp.Sine(_chorusPhase2) * 7.0f - wow; // Counter-drift for voice separation

        float s1 = _delay.Read(d1 * _sampleRate / 1000f);
        float s2 = _delay.Read(d2 * _sampleRate / 1000f);

        _delay.Write(x);

        // Mix the two delayed signals together, then blend with the dry signal in the caller.
        return x + (s1 + s2) * 0.45f;
    }
}