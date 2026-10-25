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
    /// Prepares G.711 speech gains with two empirically fitted cubic level-correction segments.
    /// Preserves the accepted dry/wet ratio; both halves share their midpoint value and slope.
    /// Call once per buffer. This fixed speech preset does not measure or normalize input loudness.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CubicGains(float t, out float dryGain, out float wetGain)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t == .5f)
        {
            dryGain = .6685965239497303f * .907663394f;
            wetGain = dryGain;
            return;
        }
        float correction;
        if (t < .5f)
        {
            float v = 2f * t;
            dryGain = ((.08206214577815385f * v - .1634656218284234f) * v - .25f) * v + 1f;
            wetGain = ((-.056369344960515266f * v - .12503413108975425f) * v + .85f) * v;
            correction = ((.0924456549f * v - .131718388f) * v - .0530638729f) * v + 1f;
        }
        else
        {
            // Factoring from full wet preserves exact endpoint gains and its accepted waveform.
            float s = 2f * (1f - t);
            dryGain = s * (.57f + s * (.5350447655268055f - .4364482415770753f * s));
            wetGain = 1f + s * (.66f + s * (-1.8833867252118637f + .8919832491615939f * s));
            correction = ((-.337347025f * s + .806194340f) * s - .561183920f) * s + 1f;
        }
        dryGain *= correction;
        wetGain *= correction;
    }

    /// <summary>Combines inputs with cubic gains prepared outside the sample loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CubicCrossfade(float dry, float wet, float dryGain, float wetGain)
    {
        return dry * dryGain + wet * wetGain;
    }

    /// <summary>Brings quantization forward while retaining exact clean/full endpoints.</summary>
    public static float CrushStrength(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * (2f - t);
    }

    /// <summary>Starts sample holding gently; the full update clock remains half the source rate.</summary>
    public static float CrushClock(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Static two-part playback correction; no dry signal or runtime level analysis.</summary>
    public static float CrushGain(float t)
    {
        if (!(t > .001f) || t >= 1f) return 1f;
        float s = t <= .5f ? 2f * t : 2f * t - 1f;
        float db = t <= .5f
            ? ((-0.115586614f * s + 0.364015070f) * s + 0.301648196f) * s
            : ((2.627098406f * s - 3.860093551f) * s + 0.682918494f) * s + 0.550076652f;
        return MathF.Pow(10f, db / 20f);
    }

    /// <summary>
    /// Prepares TacticalRadio gains for its eased input EQ, codec strength and fixed output trim.
    /// Two empirically fitted quartic halves share their midpoint value and intensity slope.
    /// Preserves the equal-power dry/wet ratio and reuses its prepared roots once per buffer.
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
            correction = 0.862721205f;
        }
        else if (t < 0.5f)
        {
            // Reuse the wet root to shape the low-intensity transition.
            float u = wetGain * 1.41421356f;
            correction = (((-0.677354693f * u + 1.27634215f) * u - 0.0871671513f) * u - 0.649099052f) * u + 1f;
        }
        else
        {
            // Evaluate from exact full wet to preserve its accepted waveform.
            float u = 2f * (1f - t);
            correction = (((0.659005046f * u - 1.58438516f) * u + 1.18094683f) * u - 0.392845571f) * u + 1f;
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
    /// Fixed FM gains fitted in two quartic halves with a shared midpoint value and slope.
    /// The prepared full FM branch includes its manual output boost; no input analysis is performed.
    /// </summary>
    public static void FmGains(float t, out float dryGain, out float wetGain)
    {
        if (t <= .001f || float.IsNaN(t))
        {
            dryGain = 1;
            wetGain = 0;
            return;
        }
        if (t >= 1)
        {
            dryGain = 0;
            wetGain = 1;
            return;
        }
        if (t == .5f)
        {
            dryGain = 0.551741719f;
            wetGain = 0.476274043f;
            return;
        }
        if (t < .5f)
        {
            float u = 2 * t;
            dryGain = ((((-1.05324185f * u + 2.34128022f) * u + -1.58125246f) * u + -0.155044287f) * u + 1f);
            wetGain = ((((1.1927706f * u + -2.62879014f) * u + 1.74789405f) * u + 0.16439952f) * u + 0f);
            return;
        }
        float uRight = 2 * (1 - t);
        dryGain = ((((0.394435048f * uRight + -1.10797596f) * uRight + 0.987580836f) * uRight + 0.277701855f) * uRight + 0f);
        wetGain = ((((-0.287124693f * uRight + 0.815499723f) * uRight + -0.790798962f) * uRight + -0.261302054f) * uRight + 1f);
    }

    /// <summary>Combines inputs with Fm gains prepared outside the sample loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FmCrossfade(float dry, float wet, float dryGain, float wetGain)
    {
        return dry * dryGain + wet * wetGain;
    }

    /// <summary>Static Overdrive playback correction, prepared outside the sample loop.</summary>
    public static float DriveGain(float amount)
    {
        if (!(amount > .001f) || amount >= 1f) return 1f;
        float s = amount <= .5f ? amount * 2f : amount * 2f - 1f;
        float db = amount <= .5f
            ? ((-2.344765913f * s + 8.911313896f) * s - 9.410726014f) * s
            : ((-.539464856f * s + 2.006038849f) * s + 1.377604038f) * s - 2.844178031f;
        return MathF.Pow(10f, db / 20f);
    }

    /// <summary>Static ring-modulator playback correction, prepared outside the sample loop.</summary>
    public static float RingGain(float amount)
    {
        if (!(amount > .001f) || amount >= 1f) return 1f;
        float s = amount <= .5f ? amount * 2f : amount * 2f - 1f;
        float db = amount <= .5f
            ? ((-1.503896774f * s + 2.421650612f) * s + 1.356775113f) * s
            : ((1.308648992f * s - 5.271563960f) * s + 1.688386016f) * s + 2.274528952f;
        return MathF.Pow(10f, db / 20f);
    }

    /// <summary>Advances modulation depth and delayed level smoothly from clean to full.</summary>
    public static float ModulationStrength(float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return amount * (2f - amount);
    }

    /// <summary>Removes saturation continuously as drive approaches zero; unity drive retains tanh.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ModulationSaturate(float sample, float drive, float inverseDrive) =>
        drive > 0f ? MathF.Tanh(sample * drive) * inverseDrive : sample;

    /// <summary>Static two-part chorus playback correction, prepared outside the sample loop.</summary>
    public static float ChorusGain(float amount)
    {
        if (!(amount > .001f) || amount >= 1f) return 1f;
        float s = amount <= .5f ? amount * 2f : amount * 2f - 1f;
        float db = amount <= .5f
            ? ((-0.148290302f * s + 0.126378862f) * s - 0.076988553f) * s
            : ((0.231938323f * s + 0.136063404f) * s - 0.269101734f) * s - 0.098899993f;
        return MathF.Pow(10f, db / 20f);
    }

    /// <summary>Static two-part flanger playback correction, prepared outside the sample loop.</summary>
    public static float FlangerGain(float amount)
    {
        if (!(amount > .001f) || amount >= 1f) return 1f;
        float s = amount <= .5f ? amount * 2f : amount * 2f - 1f;
        float db = amount <= .5f
            ? ((0.551390440f * s - 1.666701525f) * s + 1.434298655f) * s
            : ((1.223033317f * s - 1.297087813f) * s - 0.244933075f) * s + 0.318987570f;
        return MathF.Pow(10f, db / 20f);
    }

    /// <summary>
    /// Prepares Telephone weights from one empirically fitted rational quartic curve.
    /// Positive Bernstein coefficients keep the shared denominator at least one.
    /// The wet share increases smoothly, with no midpoint join; endpoints stay exact.
    /// Call once per buffer. Mixing needs no level analysis, extra pass or allocation.
    /// </summary>
    public static void TelephoneGains(float t, out float dryGain, out float wetGain)
    {
        if (t <= .001f || float.IsNaN(t))
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

        float s = 1f - t;
        float s2 = s * s, t2 = t * t, cross = s * t;
        float b0 = s2 * s2, b1 = cross * s2, b2 = cross * cross;
        float b3 = cross * t2, b4 = t2 * t2;
        float scale = 1f / (b0 + 126.780807f * b1 + 75.7484541f * b2 + 19.2256392f * b3 + b4);
        dryGain = (b0 + 127.225773f * b1 + 51.4515467f * b2 + 0.712533685f * b3) * scale;
        wetGain = (5.8789886f * b1 + 102.327607f * b2 + 19.4047641f * b3 + b4) * scale;
    }

    /// <summary>Combines inputs using prepared Telephone weights without additional per-sample work.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float TelephoneCrossfade(float dry, float wet, float dryGain, float wetGain)
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
            return x - x * x * x / 3f;
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
    private float _r = 1f - (2f * MathF.PI * 35f / sampleRate);

    /// <summary>Magnitude of the next output with zero input; bounds the remaining stable one-pole decay.</summary>
    internal readonly float PendingMagnitude => MathF.Abs(-_x1 + _r * _y1);

    /// <summary>Changes the DC corner without resetting the streaming history.</summary>
    public void SetCutoff(float frequency) => _r = 1f - (2f * MathF.PI * frequency / sampleRate);

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
