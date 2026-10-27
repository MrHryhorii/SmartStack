using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

/// <summary>Single-precision RBJ biquad for the low-level cassette noise path only.</summary>
internal struct TapeNoiseFilter
{
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    private TapeNoiseFilter(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0);
        _b1 = (float)(b1 / a0);
        _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0);
        _a2 = (float)(a2 / a0);
    }

    // Standard cookbook coefficients remain double precision during preparation.
    internal static TapeNoiseFilter LowPass(int sampleRate, float frequency, float q)
    {
        double omega = 2 * Math.PI * frequency / sampleRate;
        double cosine = Math.Cos(omega);
        double alpha = Math.Sin(omega) / (2 * q);
        return new((1 - cosine) / 2, 1 - cosine, (1 - cosine) / 2,
            1 + alpha, -2 * cosine, 1 - alpha);
    }

    internal static TapeNoiseFilter HighPass(int sampleRate, float frequency, float q)
    {
        double omega = 2 * Math.PI * frequency / sampleRate;
        double cosine = Math.Cos(omega);
        double alpha = Math.Sin(omega) / (2 * q);
        return new((1 + cosine) / 2, -(1 + cosine), (1 + cosine) / 2,
            1 + alpha, -2 * cosine, 1 - alpha);
    }

    internal static TapeNoiseFilter Peak(int sampleRate, float frequency, float q, float gainDb)
    {
        double omega = 2 * Math.PI * frequency / sampleRate;
        double cosine = Math.Cos(omega);
        double alpha = Math.Sin(omega) / (2 * q);
        double gain = Math.Pow(10, gainDb / 40f);
        return new(1 + alpha * gain, -2 * cosine, 1 - alpha * gain,
            1 + alpha / gain, -2 * cosine, 1 - alpha / gain);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float Transform(float input)
    {
        float output = _b0 * input + _z1;
        _z1 = _b1 * input - _a1 * output + _z2;
        _z2 = _b2 * input - _a2 * output;
        return output;
    }
}
