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
    private readonly DelayBuffer _echoDelay;

    // Environment-specific equalizer, instantiated only when required
    private BiQuadFilter? _environmentEq;

    private SpatialEnvironment _current = SpatialEnvironment.None;

    // Cached per-environment reverb parameters, written once in Setup() and read each sample.
    // Avoids redundant property assignments inside the hot sample-processing loop.
    private float _cachedFeedback;
    private float _cachedDamp;

    /// <summary>
    /// Initializes the spatial engine, pre-allocating all necessary delay buffers.
    /// Buffer sizes are automatically scaled to the host sample rate to maintain
    /// physically accurate room dimensions across different TTS sample rates.
    /// </summary>
    public SpatialEffectsEngine(int sampleRate)
    {
        _sampleRate = sampleRate;

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
        _echoDelay = new DelayBuffer(32768);
    }

    /// <summary>
    /// Clears all internal delay buffers, comb filters, and all-pass filters.
    /// Critical for preventing acoustic bleed-over between consecutive TTS requests.
    /// </summary>
    public void Reset()
    {
        foreach (var c in _combs) c.Clear();
        foreach (var a in _allPasses) a.Clear();
        _echoDelay.Clear();
        _current = SpatialEnvironment.None;
    }

    /// <summary>
    /// Processes the audio buffer in-place, applying the specified acoustic environment.
    /// Handles hardware stability (denormals) and dry/wet mixing automatically.
    /// Environment parameters are configured once per environment change, not per sample.
    /// </summary>
    public void ApplyEnvironment(Span<float> buffer, string? environment = null, float? intensity = null)
    {
        if (!Enum.TryParse(environment ?? "None", true, out SpatialEnvironment env) || env == SpatialEnvironment.None)
            return;

        float mix = Math.Clamp(intensity ?? 1.0f, 0f, 1f);
        if (mix <= 0.001f) return;

        if (_current != env)
        {
            // Setup configures all filter parameters and caches reverb coefficients
            // so that the sample loop below only reads values, never writes them.
            Setup(env);
            Reset();
            _current = env;
        }

        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = Dsp.KillDenormal(buffer[i]);

            float wet = env switch
            {
                SpatialEnvironment.LivingRoom => AlgorithmicReverb(dry),
                SpatialEnvironment.ConcreteHall => AlgorithmicReverb(dry),
                SpatialEnvironment.Forest => ForestEcho(dry),
                SpatialEnvironment.Underwater => Underwater(dry),
                _ => dry
            };

            // Apply global environmental EQ (e.g., severe muffling for underwater)
            if (_environmentEq != null)
                wet = _environmentEq.Transform(wet);

            // Parallel mix strategy:
            //   Reverb/Echo sums with the dry signal — the source remains present.
            //   Underwater replaces the dry signal — the source is fully submerged.
            buffer[i] = env == SpatialEnvironment.Underwater
                ? dry + (wet - dry) * mix
                : dry + wet * mix;
        }
    }

    /// <summary>
    /// Configures all environment-specific filters and caches reverb coefficients.
    /// Called once per environment change, keeping the sample loop allocation-free and branch-light.
    /// Cutoff frequencies are clamped safely below the Nyquist limit to prevent BiQuadFilter instability.
    /// </summary>
    private void Setup(SpatialEnvironment env)
    {
        _environmentEq = null;

        // Determine acoustic coefficients for the target environment
        (float feedback, float damp) = env switch
        {
            SpatialEnvironment.LivingRoom => (0.70f, 0.65f),
            SpatialEnvironment.ConcreteHall => (0.88f, 0.15f),
            SpatialEnvironment.Underwater => (0.85f, 0.90f),
            _ => (0.70f, 0.50f)
        };

        // Cache coefficients so that AlgorithmicReverb reads them without any
        // per-sample property assignments on the filter objects.
        _cachedFeedback = feedback;
        _cachedDamp = damp;

        // Apply cached values to all comb filters once, here in Setup
        foreach (var c in _combs)
        {
            c.Feedback = feedback;
            c.Damp = damp;
        }

        // Configure environment-specific EQ
        // Safe Nyquist margin (45% of sample rate) prevents BiQuadFilter edge instability
        float nyq = _sampleRate * 0.45f;
        float Safe(float f) => Math.Min(f, nyq);

        switch (env)
        {
            case SpatialEnvironment.Underwater:
                // Water severely dampens high and mid frequencies
                _environmentEq = BiQuadFilter.LowPassFilter(_sampleRate, Safe(450f), 0.707f);
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
    ///
    /// Filter parameters (Feedback, Damp) are pre-applied in Setup() — this method
    /// only reads filter state, making it safe to call from a hot real-time sample loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float AlgorithmicReverb(float input)
    {
        float outCombs = 0f;

        for (int i = 0; i < _combs.Length; i++)
            outCombs += _combs[i].Process(input);

        // Normalize by comb count to preserve headroom and prevent inter-filter clipping
        float reverbSignal = outCombs / _combs.Length;

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
        float delaySamples = 0.25f * _sampleRate;
        float delayed = _echoDelay.Read(delaySamples);

        _echoDelay.Write(x + delayed * 0.4f);

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
        float wet = AlgorithmicReverb(x);

        float delaySamples = 0.04f * _sampleRate; // 40ms enclosed pressure echo
        float delayed = _echoDelay.Read(delaySamples);
        _echoDelay.Write(x + delayed * 0.5f);

        return (wet * 0.6f) + (delayed * 0.4f);
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
        while (true)
        {
            bool isPrime = true;
            for (int i = 2; i <= Math.Sqrt(start); i++)
            {
                if (start % i == 0) { isPrime = false; break; }
            }
            if (isPrime) return start;
            start++;
        }
    }
}