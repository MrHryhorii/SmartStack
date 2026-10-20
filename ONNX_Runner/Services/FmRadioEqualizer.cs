using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

/// <summary>
/// Fixed linear-phase FM equalizer with a matched dry delay.
/// Kernel preparation is cached by sample rate; processing allocates no memory.
/// </summary>
internal sealed class FmRadioEqualizer
{
    private static readonly ConcurrentDictionary<int, float[]> Kernels = new();
    private readonly float[] _kernel;
    private readonly float[] _history;
    private readonly int _mask;
    private int _position;

    internal int DelaySamples => _kernel.Length / 2;
    internal ReadOnlySpan<float> Coefficients => _kernel;

    internal FmRadioEqualizer(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _kernel = Kernels.GetOrAdd(sampleRate, CreateKernel);
        int size = 1;
        while (size < _kernel.Length) size <<= 1;
        _history = new float[size];
        _mask = size - 1;
    }

    internal void Reset()
    {
        Array.Clear(_history);
        _position = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float Process(float sample, out float dry)
    {
        int position = _position;
        int delay = DelaySamples;
        _history[position] = sample;
        dry = _history[(position - delay) & _mask];
        float sum = _kernel[delay] * dry;
        int last = _kernel.Length - 1;
        for (int i = 0; i < delay; i++)
            sum += _kernel[i] * (_history[(position - i) & _mask]
                + _history[(position - last + i) & _mask]);
        _position = (position + 1) & _mask;
        return sum;
    }

    private static float[] CreateKernel(int sampleRate)
    {
        int delay = Math.Max(16, (int)Math.Round(sampleRate * (128.0 / 22050.0)));
        int length = 2 * delay + 1;
        int grid = 1;
        while (grid < length) grid <<= 1;

        var highPass = Section(sampleRate, 300, .707, 0, 0);
        var presence = Section(sampleRate, 1800, 1.8, 4.5, 1);
        var lowPass = Section(sampleRate, 3000, .707, 0, 2);
        var magnitude = new double[grid + 1];
        for (int i = 0; i <= grid; i++)
        {
            Complex z = Complex.FromPolarCoordinates(1, -Math.PI * i / grid);
            magnitude[i] = (highPass.At(z) * presence.At(z) * lowPass.At(z)).Magnitude;
        }

        const double beta = 5;
        double denominator = BesselI0(beta);
        var kernel = new float[length];
        for (int n = 0; n <= delay; n++)
        {
            double sum = magnitude[0] + magnitude[grid] * Math.Cos(Math.PI * (n - delay));
            for (int k = 1; k < grid; k++)
                sum += 2 * magnitude[k] * Math.Cos(Math.PI * k * (n - delay) / grid);
            double x = (n - delay) / (double)delay;
            double window = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - x * x))) / denominator;
            float value = (float)(sum * window / (2 * grid));
            kernel[n] = kernel[length - 1 - n] = value;
        }
        return kernel;
    }

    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
    {
        internal Complex At(Complex z) => (B0 + B1 * z + B2 * z * z) / (1 + A1 * z + A2 * z * z);
    }

    private static Biquad Section(int rate, double frequency, double q, double gainDb, int type)
    {
        double w = 2 * Math.PI * Math.Min(frequency, rate * .45) / rate;
        double c = Math.Cos(w);
        double alpha = Math.Sin(w) / (2 * q);
        if (type == 1)
        {
            double a = Math.Pow(10, gainDb / 40);
            double a0 = 1 + alpha / a;
            return new((1 + alpha * a) / a0, -2 * c / a0,
                (1 - alpha * a) / a0, -2 * c / a0, (1 - alpha / a) / a0);
        }
        double b0 = type == 0 ? (1 + c) / 2 : (1 - c) / 2;
        double b1 = type == 0 ? -(1 + c) : 1 - c;
        double divisor = 1 + alpha;
        return new(b0 / divisor, b1 / divisor, b0 / divisor, -2 * c / divisor, (1 - alpha) / divisor);
    }

    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        for (int k = 1; k < 40; k++)
        {
            term *= x * x / (4.0 * k * k);
            sum += term;
            if (term < sum * 1e-16) break;
        }
        return sum;
    }
}
