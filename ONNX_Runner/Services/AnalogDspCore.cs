using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services
{
    /// <summary>
    /// A zero-allocation DSP primitive library for Virtual Analog (VA) audio processing.
    /// Designed for high-performance server environments (e.g., TTS generation).
    /// </summary>
    public struct NoiseGenerator
    {
        private uint _prngState;
        private float _pink0, _pink1, _pink2, _pink3, _pink4, _pink5;

        /// <summary>
        /// Initializes the Pseudo-Random Number Generator.
        /// </summary>
        public void Seed(uint seed) => _prngState = seed;

        /// <summary>
        /// Resets the noise generator and all pink noise filters to their initial state.
        /// Call this when restarting audio processing to avoid old state carry-over.
        /// </summary>
        public void Reset()
        {
            _prngState = 0;
            _pink0 = _pink1 = _pink2 = _pink3 = _pink4 = _pink5 = 0f;
        }

        /// <summary>
        /// Generates uncorrelated White Noise (-0.5 to 0.5).
        /// Used for digital dithering and sharp static bursts.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextWhite()
        {
            _prngState = 1664525u * _prngState + 1013904223u;
            return ((float)_prngState / uint.MaxValue) - 0.5f;
        }

        /// <summary>
        /// Generates true Pink Noise (1/f spectrum) using Paul Kellett's 6-pole approximation.
        /// Used for physically accurate analog circuit hiss and tape noise.
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
    /// Simulates the slow, unpredictable heating and cooling of analog components.
    /// Uses a Brownian random walk (Red Noise) to drive macro-instabilities.
    /// </summary>
    public struct ThermalDrift
    {
        public float State { get; private set; }

        /// <summary>
        /// Resets the thermal drift state to zero (cold start).
        /// </summary>
        public void Reset() => State = 0f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(ref NoiseGenerator noise)
        {
            // 1-pole lowpass filter applied to white noise creates ultra-slow Brownian motion
            State = 0.9999f * State + 0.0001f * noise.NextWhite();
        }
    }

    /// <summary>
    /// Low-Frequency Oscillator for modulation effects (Chorus, Flanger, Tremolo).
    /// </summary>
    public struct Lfo
    {
        private float _phase;

        public void Reset() => _phase = 0f;

        /// <summary>
        /// Advances the internal phase and returns the current angle (0 to 2PI).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float AdvancePhase(float freqHz, int sampleRate)
        {
            _phase += MathF.PI * 2f * freqHz / sampleRate;
            if (_phase > MathF.PI * 2f) _phase -= MathF.PI * 2f;
            return _phase;
        }

        // --- Waveforms ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Sine(float phaseOffset = 0f) => MathF.Sin(_phase + phaseOffset);

        /// <summary>
        /// Triangle wave (-1 to 1). Preferred for Chorus to avoid pitch-wobble peaks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Triangle(float phaseOffset = 0f)
        {
            float p = (_phase + phaseOffset) % (MathF.PI * 2f);
            if (p < 0) p += MathF.PI * 2f;
            float norm = p / (MathF.PI * 2f);
            return 2f * MathF.Abs(2f * norm - 1f) - 1f;
        }
    }

    /// <summary>
    /// 1-Pole Infinite Impulse Response (IIR) Filter.
    /// Highly optimized for BBD chip degradation cascades and ADC reconstruction.
    /// </summary>
    public struct OnePoleFilter
    {
        private float _state;

        public void Reset() => _state = 0f;

        /// <summary>
        /// Processes the sample using a direct Alpha coefficient (0.0 to 1.0).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float input, float alpha)
        {
            _state += alpha * (input - _state);
            return _state;
        }

        /// <summary>
        /// Helper to calculate the Alpha coefficient from a target cutoff frequency.
        /// Call this once during Setup, NOT in the per-sample loop.
        /// </summary>
        public static float CalculateAlpha(float cutoffHz, int sampleRate)
        {
            return 1f - MathF.Exp(-MathF.PI * 2f * cutoffHz / sampleRate);
        }
    }

    /// <summary>
    /// Circular buffer for fractional delays (Echo, Flanger, Chorus).
    /// </summary>
    public class DelayLine
    {
        private readonly float[] _buffer;
        private int _writePos;
        private readonly int _mask;

        /// <summary>
        /// Capacity MUST be a power of 2 (e.g., 2048, 4096) for fast bitwise wrapping.
        /// </summary>
        public DelayLine(int capacity = 4096)
        {
            if ((capacity & (capacity - 1)) != 0)
                throw new ArgumentException("Capacity must be a power of 2.");

            _buffer = new float[capacity];
            _mask = capacity - 1;
        }

        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _writePos = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(float sample)
        {
            _buffer[_writePos] = Math.Clamp(sample, -1f, 1f);
            _writePos = (_writePos + 1) & _mask;
        }

        /// <summary>
        /// Reads from the past using linear interpolation.
        /// Prevents zipper noise when modulating the delay time.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ReadLinear(float delaySamples)
        {
            float readPos = _writePos - delaySamples - 1f;
            if (readPos < 0f) readPos += _buffer.Length;

            int p1 = (int)readPos & _mask;
            int p2 = (p1 + 1) & _mask;
            float frac = readPos - (int)readPos;

            return _buffer[p1] * (1f - frac) + _buffer[p2] * frac;
        }
    }

    /// <summary>
    /// Mathematical wave-shaping functions for harmonic distortion.
    /// </summary>
    public static class Saturation
    {
        /// <summary>
        /// Soft clipping (Magnetic Tape / Analog Console). 
        /// Gracefully rounds off peaks. Generates odd harmonics.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Tape(float x) => MathF.Tanh(x);

        /// <summary>
        /// Hard clipping (Transistor Fuzz / Cheap Op-Amps).
        /// Brutally flattens the signal above the threshold.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float HardClip(float x) => Math.Clamp(x, -1f, 1f);

        /// <summary>
        /// Digital Foldback (Glitch / Extreme Bitcrusher).
        /// Reverses the waveform when it hits the ceiling, creating harsh metallic shrieks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Foldback(float x)
        {
            float v = MathF.Abs(x) % 4f;
            if (v > 2f) v = 4f - v;
            return x < 0 ? -(v > 1f ? 2f - v : v) : (v > 1f ? 2f - v : v);
        }

        /// <summary>
        /// Piecewise Exponential Shockley Diode Model (Vintage Overdrive).
        /// Forward bias: exponential knee. Reverse bias: smoother Zener breakdown.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ShockleyHybrid(float x)
        {
            const float TwoDivPi = 2f / MathF.PI;

            if (x > 0f)
            {
                if (x > 6f) return 1f;
                return TwoDivPi * MathF.Atan(MathF.Exp(x * 1.5f) - 1f);
            }

            return TwoDivPi * MathF.Atan(x * 1.2f);
        }
    }
}