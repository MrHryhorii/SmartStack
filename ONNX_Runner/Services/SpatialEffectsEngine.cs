using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Server-side spatial acoustics engine.
/// Uses Freeverb/Schroeder algorithms to simulate realistic physical environments
/// (rooms, halls, outdoor spaces, and underwater conditions) with zero-allocation during processing.
/// </summary>
public class SpatialEffectsEngine
{
    private readonly int _sampleRate;

    // --- Schroeder / Freeverb Primitives ---
    // Full 8-comb topology matches the original Freeverb specification.
    // Fewer combs produce a noticeably thinner, more metallic reverb tail.
    private readonly CombFilter[] _combs;
    private readonly AllPassFilter[] _allPasses;

    // --- Echo & EQ Primitives ---
    // Isolated delay buffers prevent acoustic bleed-over between different physical environments.
    private readonly DelayBuffer _forestDelay;
    private readonly DelayBuffer _underwaterDelay;

    // Shared pre-delay buffer for Cave and Stage environments.
    // Cave uses ~15ms, Stage uses ~25ms. Never active simultaneously, so one buffer suffices.
    // 2048 samples ≈ 42ms at 48kHz — covers both environments with margin.
    private readonly DelayBuffer _preDelay;


    // Environment-specific equalizer, instantiated only when required
    private BiQuadFilter? _environmentEq;

    private BiQuadFilter? _reverbHpFilter;
    private BiQuadFilter? _reverbLpFilter;

    // Inner Voice: high-pass to strip chest/room resonance, presence cut for internalized tone
    private BiQuadFilter? _innerVoiceHp;
    private BiQuadFilter? _innerVoicePresenceCut;

    private SpatialEnvironment _current = SpatialEnvironment.None;

    // Pre-computed delay tap sizes in samples (set in Setup, read in the sample loop)
    private float _preDelaySamples;
    private float _innerVoiceTap1Samples;
    private float _innerVoiceTap2Samples;

    private readonly float _forestDelaySamples;
    private readonly float _underwaterDelaySamples;
    private bool _hasReverbEq;

    /// <summary>
    /// Initializes the spatial engine, pre-allocating all necessary delay buffers.
    /// Buffer sizes are automatically scaled to the host sample rate to maintain
    /// physically accurate room dimensions across different TTS sample rates.
    /// </summary>
    public SpatialEffectsEngine(int sampleRate)
    {
        _sampleRate = sampleRate;
        _forestDelaySamples = 0.25f * sampleRate;
        _underwaterDelaySamples = 0.04f * sampleRate;

        // Scaling factor keeps room sizes physically accurate regardless of TTS sample rate
        float scale = sampleRate / 44100f;
        int ScaleToPrime(int baseSize) => GetNextPrime((int)(baseSize * scale));

        // Full Freeverb 8-comb topology.
        // All buffer sizes are rounded to prime numbers to prevent resonant
        // frequencies from accumulating into audible metallic ringing.
        _combs =
        [
            new(ScaleToPrime(1116)),
            new(ScaleToPrime(1188)),
            new(ScaleToPrime(1277)),
            new(ScaleToPrime(1356)),
            new(ScaleToPrime(1422)),
            new(ScaleToPrime(1491)),
            new(ScaleToPrime(1557)),
            new(ScaleToPrime(1617))
        ];

        // 4 all-pass stages provide sufficient phase diffusion to smear
        // discrete echoes into a smooth, dense reverb tail.
        _allPasses =
        [
            new(ScaleToPrime(225)),
            new(ScaleToPrime(341)),
            new(ScaleToPrime(441)),
            new(ScaleToPrime(556))
        ];

        // 32768 samples ≈ 680ms at 48kHz — large enough for deep forest echoes
        _forestDelay = new DelayBuffer(32768);

        // 8192 samples ≈ 170ms at 48kHz — perfectly covers the 40ms underwater slapback
        _underwaterDelay = new DelayBuffer(8192);

        // 2048 samples ≈ 42ms at 48kHz — shared pre-delay for Cave (~15ms) and Stage (~25ms)
        _preDelay = new DelayBuffer(2048);
    }

    /// <summary>
    /// Clears all internal delay buffers, comb filters, and all-pass filters.
    /// Critical for preventing acoustic bleed-over between consecutive TTS requests.
    /// Note: does NOT reset _current — environment tracking is the responsibility
    /// of ApplyEnvironment, not of the buffer-clearing routine.
    /// </summary>
    public void Reset()
    {
        foreach (var c in _combs) c.Clear();
        foreach (var a in _allPasses) a.Clear();
        _forestDelay.Clear();
        _underwaterDelay.Clear();
        _preDelay.Clear();
    }

    /// <summary>
    /// Processes the audio buffer in-place, applying the specified acoustic environment.
    /// Handles hardware stability (denormals) and dry/wet mixing automatically.
    /// </summary>
    public void ApplyEnvironment(Span<float> buffer, SpatialEnvironment env, float mix)
    {
        // Fast-path bypass
        if (env == SpatialEnvironment.None || mix <= 0.001f)
            return;

        if (_current != env)
        {
            Setup(env);
            Reset();
            _current = env;
        }

        // Loop Unswitching (Branch Hoisting).
        switch (env)
        {
            case SpatialEnvironment.LivingRoom:
            case SpatialEnvironment.ConcreteHall:
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = AlgorithmicReverb(dry);
                    // Equal-Power Crossfade preserves constant RMS energy, preventing clipping on dense signals.
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, mix));
                }
                break;

            case SpatialEnvironment.Forest:
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = ForestEcho(dry);
                    // Equal-Power Crossfade preserves constant RMS energy, preventing clipping on dense signals.
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, mix));
                }
                break;

            case SpatialEnvironment.Underwater:
                // Underwater environment requires post-reverb EQ to simulate the characteristic muffling effect of water.
                bool hasEq = _environmentEq != null;

                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = Underwater(dry);

                    if (hasEq)
                        wet = _environmentEq!.Transform(wet);

                    // Equal-Power Crossfade preserves constant RMS energy, preventing clipping on dense signals.
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, mix));
                }
                break;

            case SpatialEnvironment.Cave:
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = CaveReverb(dry);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, mix));
                }
                break;

            case SpatialEnvironment.Stage:
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = StageReverb(dry);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, mix));
                }
                break;

            case SpatialEnvironment.InnerVoice:
                // Inner Voice applies post-processing EQ to both hp and presence filters.
                bool hasInnerVoiceEq = _innerVoiceHp != null && _innerVoicePresenceCut != null;

                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = InnerVoice(dry);

                    if (hasInnerVoiceEq)
                    {
                        wet = _innerVoiceHp!.Transform(wet);
                        wet = _innerVoicePresenceCut!.Transform(wet);
                    }
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, mix));
                }
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Configures all environment-specific filters and caches reverb coefficients.
    /// Called once per environment change, keeping the sample loop allocation-free and branch-light.
    /// Cutoff frequencies are clamped safely below the Nyquist limit to prevent BiQuadFilter instability.
    ///
    /// Forest deliberately does not configure comb filters because ForestEcho() uses a
    /// discrete delay line only — comb/allpass parameters are irrelevant for that algorithm.
    ///
    /// InnerVoice deliberately does not configure comb filters either — it uses short
    /// tap delays to model intracranial resonance, not room acoustics.
    /// </summary>
    private void Setup(SpatialEnvironment env)
    {
        _environmentEq = null;
        _reverbHpFilter = null;
        _reverbLpFilter = null;
        _innerVoiceHp = null;
        _innerVoicePresenceCut = null;
        _hasReverbEq = false;

        // Safe Nyquist margin (45% of sample rate) prevents BiQuadFilter edge instability.
        float nyq = _sampleRate * 0.45f;
        float Safe(float f) => Math.Min(f, nyq);

        // Determine acoustic coefficients for reverb-based environments.
        // Forest and InnerVoice are excluded: they use delay-based algorithms, not algorithmic reverb,
        // so comb filter parameters have no effect and are intentionally left unconfigured.
        (float feedback, float damp)? reverbParams = env switch
        {
            SpatialEnvironment.LivingRoom => (0.70f, 0.65f),
            SpatialEnvironment.ConcreteHall => (0.88f, 0.15f),
            SpatialEnvironment.Underwater => (0.85f, 0.90f),
            SpatialEnvironment.Cave => (0.93f, 0.08f),
            SpatialEnvironment.Stage => (0.80f, 0.40f),
            _ => null  // Forest, InnerVoice: skip comb setup
        };

        if (reverbParams.HasValue)
        {
            var (feedback, damp) = reverbParams.Value;
            // Pre-scale the damp coefficient to optimize the biquad filter calculations in the sample loop.
            float scaledDamp = Dsp.ScaleCoeff(damp, _sampleRate);

            foreach (var c in _combs)
            {
                c.Feedback = feedback;
                c.Damp = scaledDamp;
            }

            // "Abbey Road Reverb Trick": Band-pass filtering before the algorithmic reverb.
            // HPF cuts subsonic and low-bass frequencies to prevent muddy resonance.
            // LPF softens sharp high-frequency transients, preventing flutter echo (metallic clicking).
            // Cutoffs vary per environment to match the acoustic character of the space.
            float hpFreq = env == SpatialEnvironment.Cave ? 80f :       // Stone transmits sub-bass — cut lower
                           env == SpatialEnvironment.Stage ? 120f :     // Stage floor absorbs very low frequencies
                           160f;                                        // Default (LivingRoom, ConcreteHall, Underwater)

            float lpFreq = env == SpatialEnvironment.Cave ? 5000f :     // Stone reflects mids strongly, roll off highs earlier
                           env == SpatialEnvironment.Stage ? 8000f :    // Audience absorption softens the top end
                           6500f;                                       // Default

            _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(hpFreq), 0.707f);
            _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(lpFreq), 0.707f);

            _hasReverbEq = true;
        }

        // Configure environment-specific parameters and EQ.
        switch (env)
        {
            case SpatialEnvironment.Underwater:
                // Water severely dampens high and mid frequencies
                _environmentEq = BiQuadFilter.LowPassFilter(_sampleRate, Safe(450f), 0.707f);
                break;

            case SpatialEnvironment.Cave:
                // Pre-delay of ~15ms simulates the time for sound to travel to the nearest cave wall
                // and return. This detaches the reverb tail from the dry signal, giving the
                // characteristic sense of physical distance in an enclosed stone space.
                _preDelaySamples = 0.015f * _sampleRate;
                break;

            case SpatialEnvironment.Stage:
                // Pre-delay of ~25ms simulates a concert hall stage: the listener is positioned
                // in the audience, and the first reflections arrive from the back wall and ceiling
                // after the direct sound. This asymmetry creates the sense of "sound coming from ahead".
                _preDelaySamples = 0.025f * _sampleRate;
                break;

            case SpatialEnvironment.InnerVoice:
                // Two short tap delays simulate intracranial resonance:
                //   Tap 1 (~5ms): primary reflection from the front of the skull
                //   Tap 2 (~11ms): secondary reflection from the back of the skull
                // Together they create the subtle "doubling" perceived when hearing one's own voice
                // internally — distinct from any external room acoustic.
                _innerVoiceTap1Samples = 0.005f * _sampleRate;
                _innerVoiceTap2Samples = 0.011f * _sampleRate;

                // Strip chest and room resonance — bone-conducted internal voice has no sub-bass body.
                _innerVoiceHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(300f), 0.9f);

                // Mild presence cut around 3kHz: internal voice sounds less "forward" and airy
                // than an externally heard voice — the skull acts as a mild low-pass barrier.
                _innerVoicePresenceCut = BiQuadFilter.PeakingEQ(_sampleRate, Safe(3000f), 1.5f, -4.0f);
                break;
        }
    }

    // =========================================================================
    // ACOUSTIC ALGORITHMS
    // =========================================================================

    /// <summary>
    /// Implements the classic Schroeder/Freeverb algorithmic reverb topology.
    /// Runs the dry signal through 8 parallel Comb filters to simulate room dimensions
    /// and frequency-dependent decay, then passes the sum through 4 series All-Pass filters
    /// to create dense, non-metallic acoustic diffusion.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float AlgorithmicReverb(float input)
    {
        float filteredInput = input;

        // Apply pre-reverb filtering to prevent comb filter overloading and artifacts.
        if (_hasReverbEq)
        {
            filteredInput = _reverbHpFilter!.Transform(filteredInput);
            filteredInput = _reverbLpFilter!.Transform(filteredInput);
        }

        float outCombs = 0f;

        for (int i = 0; i < _combs.Length; i++)
            // Each comb filter processes the same input in parallel, 
            // creating multiple delayed and decayed copies of the signal that combine to form the characteristic reverb tail.
            outCombs += _combs[i].Process(filteredInput);

        // Normalize by comb count and apply output damping (0.6f) to preserve headroom 
        // and ensure the reverb tail never overpowers the transient energy of the original signal.
        // float reverbSignal = (outCombs / _combs.Length) * 0.6f;

        // 0.075f = 0.6f / 8 (number of combs). Multiplication is dramatically faster than division.
        float reverbSignal = outCombs * 0.075f;

        for (int i = 0; i < _allPasses.Length; i++)
            reverbSignal = _allPasses[i].Process(reverbSignal);

        return reverbSignal;
    }

    /// <summary>
    /// Simulates expansive outdoor spaces using a discrete, decaying multi-tap delay.
    /// Unlike reverb, forest reflections arrive from distant surfaces without dense diffusion,
    /// producing a clean, spacious echo rather than a smooth reverb tail.
    /// Echo time: ~250ms (natural forest reflection distance).
    /// </summary>
    private float ForestEcho(float x)
    {
        // Echo delay is fixed at 250ms to simulate typical forest reflection distances.
        float delayed = _forestDelay.Read(_forestDelaySamples);
        // Echo feedback is set to 0.4 for a few discrete repeats that decay naturally over time.
        _forestDelay.Write(x + delayed * 0.4f);
        // Output is a mix of the dry signal and the delayed echo, with the echo attenuated to prevent overpowering the source.
        return delayed * 0.5f;
    }

    /// <summary>
    /// Combines heavy acoustic diffusion with a fast slapback delay to simulate
    /// the pressure, resonance, and muffling of a liquid enclosure.
    /// The algorithmic reverb provides the dense smearing of a hard enclosed space,
    /// while the short 40ms slapback adds the distinctive underwater pressure ring.
    /// Final EQ muffling is applied externally via _environmentEq in the main loop.
    /// </summary>
    private float Underwater(float x)
    {
        // Start with a dense, diffused reverb to simulate the enclosed, reflective nature of an underwater environment.
        float wet = AlgorithmicReverb(x);

        // Add a short slapback echo with a delay of around 40ms,
        // which simulates the characteristic "ringing" or "pinging" that occurs when sound reflects off nearby surfaces underwater.
        float delayed = _underwaterDelay.Read(_underwaterDelaySamples);
        _underwaterDelay.Write(x + delayed * 0.5f);

        // Linearly interpolate between the dense reverb and the distinct echo to maintain energy balance 
        // without causing additive volume spikes.
        return Dsp.Lerp(wet, delayed, 0.4f);
    }

    /// <summary>
    /// Simulates the deep, long reverb of a stone cave or underground cavern.
    /// A pre-delay of ~15ms physically detaches the reverb tail from the dry signal,
    /// creating the perceptual sense of distance to the nearest reflective surface.
    /// High feedback (0.93) and very low damping (0.08) model stone's near-perfect
    /// reflectivity: sound decays slowly, preserving high frequencies far into the tail.
    /// </summary>
    private float CaveReverb(float x)
    {
        // Pre-delay: feed the current sample into the pre-delay buffer.
        // The algorithmic reverb processes the output of the pre-delay (not the dry signal directly),
        // so the reverb tail begins ~15ms after the transient — the acoustic signature of stone distance.
        _preDelay.Write(x);
        float preDelayed = _preDelay.Read(_preDelaySamples);

        return AlgorithmicReverb(preDelayed);
    }

    /// <summary>
    /// Simulates a concert hall or theater stage acoustic.
    /// A pre-delay of ~25ms models the travel time from stage to audience and back,
    /// creating the "sound from ahead" asymmetry characteristic of a performance space.
    /// Moderate feedback (0.80) with medium damping (0.40) models the mixed absorption
    /// of wood, curtains, and audience seating — denser than a living room but
    /// substantially shorter and warmer than a concrete hall.
    /// </summary>
    private float StageReverb(float x)
    {
        // Pre-delay positions the reverb tail ~25ms behind the dry signal,
        // simulating the longer reflection paths of a large open performance space.
        _preDelay.Write(x);
        float preDelayed = _preDelay.Read(_preDelaySamples);

        return AlgorithmicReverb(preDelayed);
    }

    /// <summary>
    /// Simulates an "inner monologue" or telepathic voice localized inside the listener's head.
    /// Uses subtle micro-delays (doubling) and EQ cuts to remove chest resonance and external "air".
    /// This creates an intimate, internalized sound, perfect for visually distinguishing 
    /// a character's thoughts from standard spoken dialogue.
    /// </summary>
    private float InnerVoice(float x)
    {
        // Write current sample into both delay taps (single shared buffer, different read offsets).
        _preDelay.Write(x);

        float tap1 = _preDelay.Read(_innerVoiceTap1Samples);
        float tap2 = _preDelay.Read(_innerVoiceTap2Samples);

        // Mix: dry dominates, taps add subtle intracranial doubling.
        // Tap gains are intentionally asymmetric — the frontal reflection is slightly
        // louder than the occipital one, matching the anatomy of bone conduction.
        float rawMix = x + tap1 * 0.28f + tap2 * 0.18f;
        return Dsp.SoftClip(rawMix * 0.85f);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Finds the next prime number greater than or equal to the starting value.
    /// Sizing delay lines to prime numbers prevents resonant frequencies from
    /// reinforcing each other inside the reverb tail, eliminating metallic ringing.
    /// Called only at construction time — never in the sample loop.
    /// </summary>
    private static int GetNextPrime(int start)
    {
        // Values below 2 are not prime; clamp to the first valid candidate.
        if (start < 2) start = 2;

        while (true)
        {
            bool isPrime = true;
            int limit = (int)Math.Sqrt(start);
            for (int i = 2; i <= limit; i++)
            {
                if (start % i == 0) { isPrime = false; break; }
            }
            if (isPrime) return start;
            start++;
        }
    }
}