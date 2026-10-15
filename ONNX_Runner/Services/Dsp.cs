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
    /// Linear interpolation with complementary gains (1 - t and t).
    /// Identical inputs retain their level; uncorrelated inputs can lose power during a crossfade.
    /// The result depends on input level and correlation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    /// <summary>
    /// Computes complementary equal-power gains for a fixed wet/dry mix.
    /// Call once per buffer when the mix is constant to avoid MathF.Sqrt in the sample loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EqualPowerGains(float t, out float dryGain, out float wetGain)
    {
        dryGain = MathF.Sqrt(1f - t);
        wetGain = MathF.Sqrt(t);
    }

    /// <summary>
    /// Combines inputs using pre-computed equal-power gains.
    /// Constant power assumes equal-level, uncorrelated inputs.
    /// Correlated inputs can gain level during the crossfade.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EqualPowerCrossfade(float dry, float wet, float dryGain, float wetGain)
    {
        return dry * dryGain + wet * wetGain;
    }

    /// <summary>
    /// Convenience overload for non-hot-path use. When processing a buffer with a fixed mix,
    /// prefer pre-computing the gains once with EqualPowerGains and using the four-argument overload.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EqualPowerCrossfade(float dry, float wet, float t)
    {
        EqualPowerGains(t, out float dryGain, out float wetGain);
        return EqualPowerCrossfade(dry, wet, dryGain, wetGain);
    }

    /// <summary>
    /// Prepares matched cubic gains for the G.711 speech crossfade.
    /// Both halves share their midpoint value and slope; wet gain can briefly exceed one.
    /// Call once per buffer. This fixed speech preset does not measure or normalize input loudness.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CubicGains(float t, out float dryGain, out float wetGain)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t == .5f)
        {
            dryGain = .6685965239497303f;
            wetGain = dryGain;
            return;
        }
        if (t < .5f)
        {
            float v = 2f * t;
            dryGain = ((.08206214577815385f * v - .1634656218284234f) * v - .25f) * v + 1f;
            wetGain = ((-.056369344960515266f * v - .12503413108975425f) * v + .85f) * v;
            return;
        }

        // Factoring from the full-wet endpoint avoids cancellation in the small dry gain.
        float s = 2f * (1f - t);
        dryGain = s * (.57f + s * (.5350447655268055f - .4364482415770753f * s));
        wetGain = 1f + s * (.66f + s * (-1.8833867252118637f + .8919832491615939f * s));
    }

    /// <summary>Combines inputs with cubic gains prepared outside the sample loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CubicCrossfade(float dry, float wet, float dryGain, float wetGain)
    {
        return dry * dryGain + wet * wetGain;
    }

    /// <summary>
    /// Prepares Bitcrusher gains using two experimentally fitted cubic correction segments.
    /// Both halves share their midpoint value and slope; the original dry/wet ratio is retained.
    /// Call once per buffer. No runtime loudness analysis or normalization is performed.
    /// </summary>
    public static void CrusherGains(float t, out float dryGain, out float wetGain)
    {
        if (t <= 0.001f || float.IsNaN(t))
        {
            dryGain = 1f;
            wetGain = 0f;
            return;
        }
        if (t >= 1f)
        {
            dryGain = 0f;
            wetGain = 1f;
            return;
        }

        float correction;
        if (t == 0.5f)
        {
            correction = 0.867657153f;
        }
        else if (t < 0.5f)
        {
            float u = 2f * t;
            correction = ((-0.3284173f * u + 0.788290584f) * u + -0.592216131f) * u + 1f;
        }
        else
        {
            // Evaluate from the full-effect endpoint to preserve its exact correction.
            float u = 2f * (1f - t);
            correction = ((-0.0907288549f * u + 0.314687421f) * u + -0.356301413f) * u + 1f;
        }
        EqualPowerGains(t, out dryGain, out wetGain);
        dryGain *= correction;
        wetGain *= correction;
    }

    /// <summary>Combines inputs with Crusher gains prepared outside the sample loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CrusherCrossfade(float dry, float wet, float dryGain, float wetGain)
    {
        return dry * dryGain + wet * wetGain;
    }

    /// <summary>
    /// Prepares TacticalRadio gains using two experimentally fitted quartic corrections.
    /// The halves share their midpoint value and slope while retaining the original dry/wet ratio.
    /// Reuses the equal-power roots; call once per buffer without runtime loudness analysis.
    /// </summary>
    public static void TacticalGains(float t, out float dryGain, out float wetGain)
    {
        if (t <= 0.001f || float.IsNaN(t))
        {
            dryGain = 1f;
            wetGain = 0f;
            return;
        }
        if (t >= 1f)
        {
            dryGain = 0f;
            wetGain = 1f;
            return;
        }

        EqualPowerGains(t, out dryGain, out wetGain);
        float correction;
        if (t == 0.5f)
        {
            correction = 0.703430655f;
        }
        else if (t < 0.5f)
        {
            // The existing wet gain supplies sqrt(2*t), matching the low-intensity mix shape.
            float u = wetGain * 1.41421356f;
            correction = (((0.822745211f * u - 2.06082324f) * u + 2.31475267f) * u - 1.37324399f) * u + 1f;
        }
        else
        {
            // Evaluate the second half from its exact full-effect endpoint.
            float u = 2f * (1f - t);
            correction = (((1.01926546f * u - 1.28734397f) * u - 0.368925332f) * u + 0.340434498f) * u + 1f;
        }
        dryGain *= correction;
        wetGain *= correction;
    }

    /// <summary>Combines inputs with Tactical gains prepared outside the sample loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float TacticalCrossfade(float dry, float wet, float dryGain, float wetGain)
    {
        return dry * dryGain + wet * wetGain;
    }

    /// <summary>
    /// Prepares FmRadio gains using two experimentally fitted cubic corrections.
    /// The halves share their midpoint value and slope while retaining the original dry/wet ratio.
    /// Reuses the equal-power roots; call once per buffer without runtime loudness analysis.
    /// </summary>
    public static void FmGains(float t, out float dryGain, out float wetGain)
    {
        if (t <= 0.001f || float.IsNaN(t))
        {
            dryGain = 1f;
            wetGain = 0f;
            return;
        }
        if (t >= 1f)
        {
            dryGain = 0f;
            wetGain = 1f;
            return;
        }

        EqualPowerGains(t, out dryGain, out wetGain);
        float correction;
        if (t == 0.5f)
        {
            correction = 0.851101089f;
        }
        else if (t < 0.5f)
        {
            // The existing wet gain supplies sqrt(2*t), matching the low-intensity mix shape.
            float u = wetGain * 1.41421356f;
            correction = ((0.0887463403f * u + 0.28754265f) * u - 0.525187902f) * u + 1f;
        }
        else
        {
            // Evaluate the second half from its exact full-effect endpoint.
            float u = 2f * (1f - t);
            correction = ((0.7655766f * u - 1.5403225f) * u + 0.625846988f) * u + 1f;
        }
        dryGain *= correction;
        wetGain *= correction;
    }

    /// <summary>Combines inputs with Fm gains prepared outside the sample loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FmCrossfade(float dry, float wet, float dryGain, float wetGain)
    {
        return dry * dryGain + wet * wetGain;
    }

    /// <summary>
    /// Prepares Overdrive gains using two experimentally fitted quartic corrections.
    /// The halves share their midpoint value and slope while retaining the original dry/wet ratio.
    /// Reuses the equal-power roots; call once per buffer without runtime loudness analysis.
    /// </summary>
    public static void DriveGains(float t, out float dryGain, out float wetGain)
    {
        if (t <= 0.001f || float.IsNaN(t))
        {
            dryGain = 1f;
            wetGain = 0f;
            return;
        }
        if (t >= 1f)
        {
            dryGain = 0f;
            wetGain = 1f;
            return;
        }

        EqualPowerGains(t, out dryGain, out wetGain);
        float correction;
        if (t == 0.5f)
        {
            correction = 0.930806007f;
        }
        else if (t < 0.5f)
        {
            // The existing wet gain supplies sqrt(2*t), matching the low-intensity mix shape.
            float u = wetGain * 1.41421356f;
            correction = (((-0.057844468f * u - 0.232485028f) * u + 0.207646598f) * u + 0.0134889044f) * u + 1f;
        }
        else
        {
            // The existing dry gain supplies sqrt(2*(1-t)) near the full-effect endpoint.
            float u = dryGain * 1.41421356f;
            correction = (((2.01971197f * u - 4.01631705f) * u + 2.54274303f) * u - 0.615331945f) * u + 1f;
        }
        dryGain *= correction;
        wetGain *= correction;
    }

    /// <summary>Combines inputs with Drive gains prepared outside the sample loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DriveCrossfade(float dry, float wet, float dryGain, float wetGain)
    {
        return dry * dryGain + wet * wetGain;
    }

    /// <summary>
    /// Prevents CPU spikes caused by denormalized (subnormal) floating-point numbers.
    /// Denormals occur in IIR filter feedback paths when signal decays toward zero,
    /// triggering a ~100x performance penalty in the FPU on x86 without SSE DAZ mode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float KillDenormal(float x)
    {
        return MathF.Abs(x) < 1e-15f ? 0f : x;
    }

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
    public static float Sine(float phase)
    {
        return MathF.Sin(phase);
    }

    /// <summary>
    /// Sawtooth oscillator output in [-1, 1] from phase in [0, 2π).
    /// Linearly ramps from -1 to +1 per cycle. Rich in both even and odd harmonics.
    /// Formula: y = phase / π - 1
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sawtooth(float phase)
    {
        return phase / MathF.PI - 1f;
    }

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
    public static float SoftClip(float x)
    {
        return MathF.Tanh(x);
    }

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

    /// <summary>
    /// Scales a hardcoded 1-pole IIR coefficient (designed for 48kHz) to the current sample rate.
    /// Uses exact exponential scaling to guarantee perfect stability and identical time constants.
    /// Formula: C_new = 1 - (1 - C_48k)^(48000 / Fs)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ScaleCoeff(float coeffAt48k, int sampleRate)
    {
        if (sampleRate == 48000) return coeffAt48k;
        float power = 48000f / sampleRate;
        return 1f - MathF.Pow(1f - coeffAt48k, power);
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
    public void Seed(uint seed)
    {
        _state = seed == 0u ? 1u : seed;
    }

    /// <summary>Clears the pink noise IIR state. Call between requests to prevent bleed.</summary>
    public void Reset()
    {
        _b0 = _b1 = _b2 = _b3 = _b4 = _b5 = 0f;
    }

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
    public void Reset()
    {
        State = 0f;
    }

    /// <summary>
    /// Advances the drift state by one sample.
    /// The driftCoeff controls the "temperature" of the simulation: higher values produce faster, 
    /// more volatile drift, while lower values yield slower, more stable behavior.
    /// Typical values are in the range [0.0001, 0.01] — experiment to find the sweet spot for each effect.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(ref NoiseGenerator noise, float driftCoeff)
    {
        State = (1f - driftCoeff) * State + driftCoeff * noise.NextWhite();
    }
}

/// <summary>
/// DC Blocker: a one-pole high-pass filter to remove DC offset and low-frequency rumble.
/// Formula: y(n) = x(n) - x(n-1) + r * y(n-1), where r is the pole radius.
/// </summary>
public struct DcBlocker(int sampleRate)
{
    private float _x1 = 0f;
    private float _y1 = 0f;
    private readonly float _r = 1f - (2f * MathF.PI * 35f / sampleRate);

    /// <summary>Removes DC offset from one sample while retaining the previous input and output.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        float y = x - _x1 + _r * _y1;
        _x1 = x;
        _y1 = y;
        return y;
    }

    /// <summary>Clears the DC blocker's input and output history.</summary>
    public void Reset()
    {
        _x1 = _y1 = 0f;
    }
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

    /// <summary>Allocates a circular delay buffer with a power-of-two capacity for masked indexing.</summary>
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
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(float sample)
    {
        // No Clamp! Buffer should be linear and transparent.
        // Overload protection should be at the mixing stage, not in memory.
        _buf[_writePos] = sample;
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
/// The default 0.002 coefficient has an effective ~10 ms one-pole time constant
/// (~31 ms to reach 95% of a step) at 48 kHz; ScaleCoeff preserves that timing at other rates.
/// </summary>
public struct TapeDropout
{
    private float _phase;
    private float _targetDepth;
    private float _currentDepth;

    /// <summary>Clears the oscillator phase and smoothed modulation depth.</summary>
    public void Reset()
    {
        _phase = _targetDepth = _currentDepth = 0f;
    }

    /// <summary>
    /// Applies a slew-limited stochastic volume dip to the input.
    /// </summary>
    /// <param name="input">Input sample.</param>
    /// <param name="intensity">Dropout probability and depth scale [0, 1].</param>
    /// <param name="noise">Noise source (passed by ref to avoid copy).</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="slewRate">Pre-scaled slew rate coefficient.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float input, float intensity, ref NoiseGenerator noise, int sampleRate, float slewRate)
    {
        if (intensity <= 0.001f) return input;

        // Advance the dropout phase and check for trigger.
        float rate = intensity * 2.5f;
        _phase += rate / sampleRate;
        if (_phase >= 1f)
        {
            _phase -= 1f;
            // 25% of the time, trigger a dropout.
            // Depth is scaled for a subtle, realistic tape oxide wear profile.
            _targetDepth = (noise.NextWhite() + 0.5f) < intensity * 0.25f
                ? 0.03f + (noise.NextWhite() + 0.5f) * 0.20f * intensity
                : 0f;
        }

        // Slew the current depth toward the target to create a smooth, natural-sounding dropout envelope.
        _currentDepth += (_targetDepth - _currentDepth) * slewRate;

        // Safety clamp to prevent negative gain or phase inversion on extreme inputs.
        _currentDepth = Math.Clamp(_currentDepth, 0f, 0.95f);

        return input * (1f - _currentDepth);
    }
}

/// <summary>
/// Feed-forward amplitude compressor with an asymmetric rectified-signal envelope follower.
/// Above the threshold, the tracked amplitude becomes threshold + excess / ratio.
/// Ratio controls linear-amplitude excess, not a conventional compression slope in dB.
/// This lightweight curve preserves the authored tape and tactical-radio character.
/// Pre-compute attack/release coefficients with <see cref="TimeToCoeff"/> before sample processing.
/// </summary>
public struct FeedForwardCompressor
{
    private float _envelope;

    /// <summary>Clears the tracked envelope before processing a new signal.</summary>
    public void Reset()
    {
        _envelope = 0f;
    }

    /// <summary>
    /// Converts a time constant in milliseconds to a one-pole IIR coefficient.
    /// Call once in Setup(), store the result, pass it to Process().
    /// coeff = exp(-1 / (timeMs * sampleRate / 1000))
    /// </summary>
    public static float TimeToCoeff(float timeMs, int sampleRate)
    {
        return MathF.Exp(-1f / (timeMs * sampleRate / 1000f));
    }

    /// <summary>
    /// Processes one sample through the compressor.
    /// </summary>
    /// <param name="input">Input sample.</param>
    /// <param name="threshold">Linear amplitude threshold above which gain reduction begins.</param>
    /// <param name="ratio">Divisor of amplitude above the threshold, at least 1; not a dB-domain ratio.</param>
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

    /// <summary>Clears the comb delay and feedback filter history.</summary>
    public void Clear()
    {
        Array.Clear(_buf, 0, _buf.Length);
        _lpStore = 0f;
        _idx = 0;
    }

    /// <summary>Processes one comb-filter sample with damped feedback and circular delay state.</summary>
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

    /// <summary>Clears the all-pass delay buffer and resets its write position.</summary>
    public void Clear()
    {
        Array.Clear(_buf, 0, _buf.Length);
        _idx = 0;
    }

    /// <summary>Processes one sample through the Schroeder all-pass delay structure.</summary>
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
