using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

/// <summary>
/// Stateless Digital Signal Processing (DSP) kernel.
/// Provides core mathematical functions, safe audio operations, and physical modeling primitives.
/// All methods are pure or operate exclusively on explicitly passed state.
/// </summary>
public static class Dsp
{
    // =========================================================================
    // SAFETY & MATH
    // =========================================================================

    /// <summary>
    /// Linear interpolation (Equal-Gain crossfade).
    /// At t=0.5, each signal is attenuated by -6dB, causing a perceptible dip on
    /// correlated signals. Prefer <see cref="EqualPowerCrossfade"/> for wet/dry mixing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// Equal-Power (Constant-Power) crossfade using sqrt-of-complementary-gains.
    /// Preserves constant RMS energy at all mix positions — the correct choice for
    /// perceptually smooth wet/dry blending of uncorrelated audio signals.
    /// At t=0.5: dryGain = wetGain = sqrt(0.5) ≈ 0.707 (-3dB each, 0dB combined).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EqualPowerCrossfade(float dry, float wet, float t)
    {
        float dryGain = MathF.Sqrt(1f - t);
        float wetGain = MathF.Sqrt(t);
        return dry * dryGain + wet * wetGain;
    }

    /// <summary>
    /// Prevents CPU spikes caused by denormalized (subnormal) floating-point numbers.
    /// Denormals occur in IIR filter feedback paths when signal decays toward zero,
    /// triggering a ~100x performance penalty in the FPU on x86 without SSE DAZ mode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float KillDenormal(float x) => MathF.Abs(x) < 1e-15f ? 0f : x;

    // =========================================================================
    // PHASE & OSCILLATORS
    // =========================================================================

    /// <summary>
    /// Advances an oscillator phase by one sample and wraps at 2π.
    /// Phase is in radians [0, 2π). Increment = 2π * f / fs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AdvancePhase(float phase, float freqHz, int sampleRate)
    {
        phase += 2f * MathF.PI * freqHz / sampleRate;
        if (phase >= 2f * MathF.PI) phase -= 2f * MathF.PI;
        return phase;
    }

    /// <summary>Sine oscillator output from phase in radians.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sine(float phase) => MathF.Sin(phase);

    /// <summary>
    /// Sawtooth oscillator output in [-1, 1] from phase in [0, 2π).
    /// Linearly ramps from -1 to +1 per cycle. Rich in both even and odd harmonics.
    /// Formula: y = phase / π - 1
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sawtooth(float phase) => phase / MathF.PI - 1f;

    // =========================================================================
    // SATURATION & WAVE-SHAPING
    // =========================================================================

    /// <summary>
    /// Hyperbolic tangent soft clipper.
    /// Maps ℝ → (-1, 1) with a smooth S-curve. Generates only odd harmonics (symmetric).
    /// Output is normalized: SoftClip(1) ≈ 0.762, SoftClip(3) ≈ 0.995.
    /// Industry standard for tube/console saturation emulation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SoftClip(float x) => MathF.Tanh(x);

    /// <summary>
    /// Asymmetric soft clipper modeled after a single-transistor common-emitter amplifier.
    /// Produces both even and odd harmonics due to the asymmetric transfer curve,
    /// giving a warmer, more "analog" character than symmetric clippers.
    ///
    /// Positive half (forward-biased): cubic polynomial approximation of the
    /// collector current saturation curve — y = x - x³/3, clamped at 1.0.
    /// This is the standard textbook approximation for BJT soft saturation.
    ///
    /// Negative half (reverse-biased): scaled tanh for smooth, gentle limiting.
    /// The asymmetry between halves is what generates the even-harmonic content.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AsymmetricSaturation(float x)
    {
        if (x >= 0f)
        {
            // Cubic soft clip: y = x - x³/3, valid for |x| ≤ 1 (unit gain below clipping threshold).
            // For |x| > 1, hard-clip at ±2/3 (the cubic's peak value at x=1).
            if (x >= 1f) return 2f / 3f;
            return x - (x * x * x) / 3f;
        }

        // Negative half: tanh gives a gentler, more rounded limiting curve.
        // Scale keeps the slope continuous at x=0 (both halves have dy/dx = 1 at origin).
        return MathF.Tanh(x);
    }

    // =========================================================================
    // WINDOWING
    // =========================================================================

    /// <summary>
    /// Hann window (raised cosine) for index i in a window of length N.
    /// w(i) = 0.5 * (1 - cos(2π * i / (N - 1)))
    /// Zero at both endpoints (i=0, i=N-1), peak of 1.0 at center.
    /// Standard window for granular synthesis, FFT analysis, and crossfade envelopes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HannWindow(int i, int N)
    {
        if (N <= 1) return 1f;
        return 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (N - 1)));
    }
}

// ==============================================================================
// HARDWARE EMULATION STRUCTURES (Zero-Allocation)
// ==============================================================================

/// <summary>
/// Fast, zero-allocation Linear Congruential PRNG.
/// Produces White Noise and Pink Noise (1/f spectrum) for audio applications.
///
/// White: uniform distribution, flat spectrum.
/// Pink:  Paul Kellett's 6-pole IIR approximation of 1/f rolloff.
///        Each octave carries equal energy — matches psychoacoustic equal-loudness curves.
/// </summary>
public struct NoiseGenerator
{
    private uint _state;
    private float _b0, _b1, _b2, _b3, _b4, _b5;

    /// <summary>Seeds the PRNG. Must be non-zero for non-degenerate output.</summary>
    public void Seed(uint seed) => _state = seed == 0u ? 1u : seed;

    /// <summary>Clears the pink noise IIR state. Call between requests to prevent bleed.</summary>
    public void Reset() => _b0 = _b1 = _b2 = _b3 = _b4 = _b5 = 0f;

    /// <summary>
    /// White noise sample in [-0.5, 0.5].
    /// Uses the Knuth LCG: x(n+1) = 1664525 * x(n) + 1013904223 (mod 2³²).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextWhite()
    {
        _state = 1664525u * _state + 1013904223u;
        return (float)_state / uint.MaxValue - 0.5f;
    }

    /// <summary>
    /// Pink noise sample (1/f spectrum) via Paul Kellett's refined method.
    /// Six one-pole IIR filters at geometrically spaced cutoffs sum to approximate
    /// a -10dB/decade slope across the audible range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextPink()
    {
        float w = NextWhite();
        _b0 = 0.99886f * _b0 + w * 0.0555179f;
        _b1 = 0.99332f * _b1 + w * 0.0750759f;
        _b2 = 0.96900f * _b2 + w * 0.1538520f;
        _b3 = 0.86650f * _b3 + w * 0.3104856f;
        _b4 = 0.55000f * _b4 + w * 0.5329522f;
        _b5 = -0.76160f * _b5 - w * 0.0168980f;
        return (_b0 + _b1 + _b2 + _b3 + _b4 + _b5 + w * 0.5362f) * 0.115f;
    }
}

/// <summary>
/// Brownian (Red) noise generator for simulating slow thermal drift of analog components.
/// Implemented as a heavily over-damped 1-pole IIR lowpass on white noise.
/// The -6dB/octave slope creates an ultra-slow random walk — the statistical model
/// of resistor Johnson noise and capacitor ESR drift over temperature.
/// </summary>
public struct ThermalDrift
{
    /// <summary>Current drift value. Typical range: ±0.001 after steady state.</summary>
    public float State { get; private set; }

    /// <summary>Resets drift to a cold-start (zero) condition.</summary>
    public void Reset() => State = 0f;

    /// <summary>
    /// Advances the drift by one sample.
    /// Pole at 0.9999 → fc ≈ 0.16 Hz at 44.1kHz — moves imperceptibly slowly.
    /// Must be called once per sample in the processing loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(ref NoiseGenerator noise)
        => State = 0.9999f * State + 0.0001f * noise.NextWhite();
}

/// <summary>
/// First-order IIR high-pass DC blocker (Julius O. Smith design).
/// Transfer function: H(z) = (1 - z⁻¹) / (1 - R·z⁻¹), R = 0.995.
/// Cutoff ≈ (1 - R) / (2π) * fs ≈ 35 Hz at 44.1kHz — removes DC offset while
/// leaving the entire audible band intact.
/// Essential after asymmetric distortion (ShockleyDiode, AsymmetricSaturation)
/// which shifts the signal's mean away from zero.
/// </summary>
public struct DcBlocker
{
    private float _x1;
    private float _y1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        float y = x - _x1 + 0.995f * _y1;
        _x1 = x;
        _y1 = y;
        return y;
    }

    public void Reset() => _x1 = _y1 = 0f;
}

/// <summary>
/// Power-of-2 circular delay line with fractional read via linear interpolation.
/// Uses bitwise AND masking for O(1) wrap-around — faster than modulo on all platforms.
///
/// Capacity must be a power of 2. Maximum readable delay = capacity - 1 samples.
/// Suitable for chorus, flanger, and short tape delay (up to ~93ms at 44.1kHz for 4096).
/// </summary>
public class DelayBuffer
{
    private readonly float[] _buf;
    private readonly int _mask;
    private int _writePos;

    public DelayBuffer(int capacity = 4096)
    {
        if ((capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of 2.", nameof(capacity));
        _buf = new float[capacity];
        _mask = capacity - 1;
    }

    /// <summary>Zeros all samples and resets the write pointer.</summary>
    public void Clear()
    {
        Array.Clear(_buf, 0, _buf.Length);
        _writePos = 0;
    }

    /// <summary>
    /// Writes one sample and advances the pointer.
    /// Input is clamped to [-1, 1] to prevent feedback runaway.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(float sample)
    {
        _buf[_writePos] = Math.Clamp(sample, -1f, 1f);
        _writePos = (_writePos + 1) & _mask;
    }

    /// <summary>
    /// Reads a fractional delay (in samples) into the past via linear interpolation.
    /// The -1 offset ensures Read(0) returns the sample written on the immediately
    /// preceding Write() call — consistent with a zero-delay tap.
    /// Valid range: delaySamples ∈ [0, capacity - 1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Read(float delaySamples)
    {
        float pos = _writePos - delaySamples - 1f;
        if (pos < 0f) pos += _buf.Length;
        int p0 = (int)pos & _mask;
        int p1 = (p0 + 1) & _mask;
        float frac = pos - MathF.Floor(pos);
        return _buf[p0] * (1f - frac) + _buf[p1] * frac;
    }
}

/// <summary>
/// Physical simulation of random tape dropouts caused by worn magnetic oxide.
/// A slow phase accumulator (~1.5 Hz) periodically samples a noise gate threshold.
/// When triggered, a target attenuation depth is chosen; a slew-rate limiter
/// (one-pole lowpass on depth) smooths the transition to avoid clicks.
/// Slew coefficient 0.002 → ~150ms rise/fall time — matches real oxide wear behavior.
/// </summary>
public struct TapeDropout
{
    private float _phase;
    private float _targetDepth;
    private float _currentDepth;

    public void Reset() => _phase = _targetDepth = _currentDepth = 0f;

    /// <summary>
    /// Applies a slew-limited stochastic volume dip to the input.
    /// </summary>
    /// <param name="input">Input sample.</param>
    /// <param name="intensity">Dropout probability and depth scale [0, 1].</param>
    /// <param name="noise">Noise source (passed by ref to avoid copy).</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float input, float intensity, ref NoiseGenerator noise, int sampleRate)
    {
        if (intensity <= 0.001f) return input;

        // Advance the dropout phase and check for trigger.
        float rate = intensity * 2.5f;
        _phase += rate / sampleRate;
        if (_phase >= 1f)
        {
            _phase -= 1f;
            // 25% of the time, trigger a dropout with random depth scaled by intensity.
            _targetDepth = noise.NextWhite() < intensity * 0.25f
                ? 0.05f + (noise.NextWhite() + 0.5f) * 0.25f * intensity
                : 0f;
        }

        // Slew the current depth toward the target to create a smooth, natural-sounding dropout.
        _currentDepth += (_targetDepth - _currentDepth) * 0.002f;

        return input * (1f - _currentDepth);
    }
}

/// <summary>
/// Feed-forward RMS compressor with asymmetric attack/release envelope follower.
/// Based on the standard gain-computer model described in:
///   Zölzer, U. — "DAFX: Digital Audio Effects", 2nd ed., Ch. 4.
///
/// Gain reduction is computed in the linear domain (not dB) for efficiency.
/// The envelope follower uses pre-computed one-pole IIR coefficients:
///   coeff = exp(-1 / (time_s * fs))
///
/// IMPORTANT: attackCoeff and releaseCoeff must be pre-computed once in Setup()
/// via <see cref="TimeToCoeff"/> and passed in per-block — NOT per-sample —
/// to avoid calling MathF.Exp in the hot path.
/// </summary>
public struct FeedForwardCompressor
{
    private float _envelope;

    public void Reset() => _envelope = 0f;

    /// <summary>
    /// Converts a time constant in milliseconds to a one-pole IIR coefficient.
    /// Call once in Setup(), store the result, pass it to Process().
    /// coeff = exp(-1 / (timeMs * sampleRate / 1000))
    /// </summary>
    public static float TimeToCoeff(float timeMs, int sampleRate)
        => MathF.Exp(-1f / (timeMs * sampleRate / 1000f));

    /// <summary>
    /// Processes one sample through the compressor.
    /// </summary>
    /// <param name="input">Input sample.</param>
    /// <param name="threshold">Linear amplitude threshold above which gain reduction begins.</param>
    /// <param name="ratio">Compression ratio (e.g. 4.0 = 4:1). Must be ≥ 1.0.</param>
    /// <param name="attackCoeff">Pre-computed attack coefficient from <see cref="TimeToCoeff"/>.</param>
    /// <param name="releaseCoeff">Pre-computed release coefficient from <see cref="TimeToCoeff"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float input, float threshold, float ratio, float attackCoeff, float releaseCoeff)
    {
        float absIn = MathF.Abs(input);

        // Asymmetric envelope follower: fast attack tracks peaks, slow release tracks decay.
        _envelope = absIn > _envelope
            ? attackCoeff * _envelope + (1f - attackCoeff) * absIn
            : releaseCoeff * _envelope + (1f - releaseCoeff) * absIn;

        _envelope = Dsp.KillDenormal(_envelope);

        if (_envelope <= threshold) return input;

        // Linear-domain gain computer: reduce excess above threshold by (1 - 1/ratio).
        float overshoot = _envelope - threshold;
        float gainReduction = overshoot * (1f - 1f / ratio);
        float gainMultiplier = (_envelope - gainReduction) / _envelope;

        return input * gainMultiplier;
    }
}

// ==============================================================================
// SPATIAL EMULATION STRUCTURES (Reverb & Room Acoustics)
// ==============================================================================

/// <summary>
/// Low-Pass Feedback Comb Filter (LPFCF) — Moorer / Freeverb design.
/// Models a single reflection path with frequency-dependent decay:
///   y(n) = x(n - D) + g · H_lp(y(n - D))
/// where D = buffer length, g = feedback gain, H_lp = one-pole LP filter.
/// The LP filter absorbs high frequencies each reflection, simulating air absorption.
/// <para>
/// Stability: feedback must satisfy |g · (1 - damp / 2)| &lt; 1.
/// Safe operating range: Feedback ∈ [0, 0.98], Damp ∈ [0, 1].
/// </para>
/// </summary>
public class CombFilter(int bufferSize)
{
    private readonly float[] _buf = new float[bufferSize];
    private int _idx;
    private float _lpStore;

    /// <summary>Feedback gain [0, 0.98]. Controls reverb decay time (RT60).</summary>
    public float Feedback { get; set; }

    /// <summary>
    /// LP damping coefficient [0, 1].
    /// 0 = bright (no HF absorption), 0.5+ = dark/carpeted room.
    /// </summary>
    public float Damp { get; set; }

    public void Clear()
    {
        Array.Clear(_buf, 0, _buf.Length);
        _lpStore = 0f;
        _idx = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float input)
    {
        float output = _buf[_idx];

        // One-pole LP on the feedback path: absorbs HF per reflection.
        _lpStore = output * (1f - Damp) + _lpStore * Damp;
        _lpStore = Dsp.KillDenormal(_lpStore);

        _buf[_idx] = input + _lpStore * Feedback;

        if (++_idx >= _buf.Length) _idx = 0;

        return output;
    }
}

/// <summary>
/// Schroeder All-Pass Filter — phase disperser for reverb diffusion.
/// Transfer function: H(z) = (-g + z⁻ᴺ) / (1 - g · z⁻ᴺ)
/// Flat magnitude response (all-pass property) but non-linear phase:
/// smears echoes in time without coloring the frequency spectrum.
/// Used in series chains to increase reverb density.
/// <para>
/// Stability: |Feedback| must be strictly &lt; 1. Typical value: 0.5.
/// </para>
/// </summary>
public class AllPassFilter(int bufferSize)
{
    private readonly float[] _buf = new float[bufferSize];
    private int _idx;

    /// <summary>Feedback/feedforward gain. Must satisfy |Feedback| &lt; 1.</summary>
    public float Feedback { get; set; } = 0.5f;

    public void Clear()
    {
        Array.Clear(_buf, 0, _buf.Length);
        _idx = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float input)
    {
        float delayed = Dsp.KillDenormal(_buf[_idx]);

        // Classic Schroeder lattice structure:
        //   output   = -input + delayed
        //   buf[idx] =  input + delayed * g
        float output = -input + delayed;
        _buf[_idx] = input + delayed * Feedback;

        if (++_idx >= _buf.Length) _idx = 0;

        return output;
    }
}