using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

/// <summary>
/// Stateless Digital Signal Processing (DSP) kernel.
/// Provides core mathematical functions and safe audio operations.
/// All methods are pure or operate exclusively on explicitly passed state.
/// </summary>
public static class Dsp
{
    // --- Safety & Stability ---

    /// <summary>
    /// Prevents CPU spikes caused by denormalized (subnormal) floating-point numbers.
    /// Crucial for stabilizing infinite feedback loops and IIR filters.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float KillDenormal(float x)
    {
        return MathF.Abs(x) < 1e-15f ? 0f : x;
    }

    // --- Phase & Oscillators ---

    /// <summary>
    /// Advances an oscillator's phase by a specific frequency and wraps it safely at 2*PI.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AdvancePhase(float currentPhase, float freqHz, int sampleRate)
    {
        float nextPhase = currentPhase + (MathF.PI * 2f * freqHz / sampleRate);
        if (nextPhase > MathF.PI * 2f) nextPhase -= MathF.PI * 2f;
        return nextPhase;
    }

    /// <summary>
    /// Computes a standard sine wave based on the current phase.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sine(float phase) => MathF.Sin(phase);

    // --- Saturation & Wave-Shaping ---

    /// <summary>
    /// Applies soft clipping (hyperbolic tangent) to smoothly limit audio peaks.
    /// Simulates analog tape or console saturation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SoftClip(float x) => MathF.Tanh(x);

    /// <summary>
    /// Piecewise Exponential Shockley Diode Model.
    /// Creates realistic asymmetric overdrive by generating both even and odd harmonics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ShockleyDiode(float x)
    {
        const float TwoDivPi = 2f / MathF.PI;

        if (x > 0f)
        {
            // Optimization: avoid expensive Exp calculation for heavily clipped signals
            if (x > 6f) return 1f;
            return TwoDivPi * MathF.Atan(MathF.Exp(x * 1.5f) - 1f);
        }

        return TwoDivPi * MathF.Atan(x * 1.2f);
    }
}

// ==============================================================================
// HARDWARE EMULATION STRUCTURES (Zero-Allocation)
// ==============================================================================

/// <summary>
/// Fast, zero-allocation Pseudo-Random Number Generator.
/// Produces both White and Pink noise for audio character and dithering.
/// </summary>
public struct NoiseGenerator
{
    private uint _prngState;
    private float _pink0, _pink1, _pink2, _pink3, _pink4, _pink5;

    /// <summary>
    /// Initializes the random sequence based on a given seed.
    /// </summary>
    public void Seed(uint seed) => _prngState = seed;

    /// <summary>
    /// Clears the history of the pink noise filters to prevent state bleed between sessions.
    /// </summary>
    public void Reset()
    {
        _pink0 = _pink1 = _pink2 = _pink3 = _pink4 = _pink5 = 0f;
    }

    /// <summary>
    /// Generates uniformly distributed White Noise in the range [-0.5, 0.5].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextWhite()
    {
        _prngState = 1664525u * _prngState + 1013904223u;
        return ((float)_prngState / uint.MaxValue) - 0.5f;
    }

    /// <summary>
    /// Generates true Pink Noise (1/f frequency spectrum) using Paul Kellett's algorithm.
    /// Replicates physically accurate analog circuit hiss and vintage tape noise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextPink()
    {
        float white = NextWhite();
        _pink0 = 0.99886f * _pink0 + white * 0.0555179f;
        _pink1 = 0.99332f * _pink1 + white * 0.0750759f;
        _pink2 = 0.96900f * _pink2 + white * 0.1538520f;
        _pink3 = 0.86650f * _pink3 + white * 0.3104856f;
        _pink4 = 0.55000f * _pink4 + white * 0.5329522f;
        _pink5 = -0.76160f * _pink5 - white * 0.0168980f;

        return (_pink0 + _pink1 + _pink2 + _pink3 + _pink4 + _pink5 + white * 0.5362f) * 0.115f;
    }
}

/// <summary>
/// Simulates the slow, unpredictable thermal drift of physical analog components.
/// Uses Brownian motion (Red Noise) to slowly modulate parameters over time.
/// </summary>
public struct ThermalDrift
{
    /// <summary>
    /// The current thermal offset value.
    /// </summary>
    public float State { get; private set; }

    /// <summary>
    /// Resets the thermal state to a "cold" start.
    /// </summary>
    public void Reset() => State = 0f;

    /// <summary>
    /// Updates the thermal state by heavily filtering new white noise.
    /// Must be called per-sample or per-block to evolve the hardware life.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(ref NoiseGenerator noise)
    {
        // 1-pole lowpass filter applied to white noise creates ultra-slow Brownian motion
        State = 0.9999f * State + 0.0001f * noise.NextWhite();
    }
}

/// <summary>
/// A first-order high-pass filter (cutoff ~20Hz) used as a Direct Current (DC) Blocker.
/// Crucial for removing inaudible low-frequency offsets generated by asymmetric distortion.
/// </summary>
public struct DcBlocker
{
    private float _prevX;
    private float _prevY;

    /// <summary>
    /// Processes a single sample through the high-pass filter, neutralizing DC offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        float y = x - _prevX + 0.995f * _prevY;
        _prevX = x;
        _prevY = y;
        return y;
    }

    /// <summary>
    /// Clears the filter history to prevent pops on new audio streams.
    /// </summary>
    public void Reset()
    {
        _prevX = _prevY = 0f;
    }
}

/// <summary>
/// A highly optimized, memory-backed circular buffer for delay-based effects (Chorus, Flanger).
/// Uses bitwise masking for wrap-around instead of modulo operations.
/// </summary>
public class DelayBuffer
{
    private readonly float[] _buffer;
    private int _writePos;
    private readonly int _mask;

    /// <summary>
    /// Initializes the delay buffer. Capacity MUST be a power of 2 for fast bitwise masking.
    /// </summary>
    public DelayBuffer(int capacity = 4096)
    {
        if ((capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of 2.");

        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    /// <summary>
    /// Silences the delay line and resets the write pointer.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _writePos = 0;
    }

    /// <summary>
    /// Writes a clamped sample into the delay line and increments the pointer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(float sample)
    {
        _buffer[_writePos] = Math.Clamp(sample, -1f, 1f);
        _writePos = (_writePos + 1) & _mask;
    }

    /// <summary>
    /// Reads a fractional number of samples from the past using linear interpolation.
    /// Accurate read offset (-1f) ensures delaySamples=0 perfectly fetches the last written value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Read(float delaySamples)
    {
        float readPos = _writePos - delaySamples - 1f;
        if (readPos < 0f) readPos += _buffer.Length;

        int p1 = (int)readPos & _mask;
        int p2 = (p1 + 1) & _mask;
        float frac = readPos - (int)readPos;

        return _buffer[p1] * (1f - frac) + _buffer[p2] * frac;
    }
}