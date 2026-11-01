using NAudio.Dsp;

namespace ONNX_Runner.Tests;

public sealed record LargeSpaceAcousticMetrics(double FirstReflectionMilliseconds, double HalfEnergyMilliseconds,
    double NinetyPercentEnergyMilliseconds, double EnergyAfterTwoSecondsFraction, double LowToMidEnergyDb)
{
    public static LargeSpaceAcousticMetrics Measure(ReadOnlySpan<float> reflected, int sampleRate)
    {
        double total = 0, late = 0, lowPower = 0, midPower = 0;
        float peak = 0;
        var low = BiQuadFilter.LowPassFilter(sampleRate, 250, .707f);
        var high = BiQuadFilter.HighPassFilter(sampleRate, 250, .707f);
        var middle = BiQuadFilter.LowPassFilter(sampleRate, 2000, .707f);
        for (int i = 0; i < reflected.Length; i++)
        {
            float value = reflected[i];
            double power = value * (double)value;
            total += power;
            if (i >= sampleRate * 2) late += power;
            peak = Math.Max(peak, Math.Abs(value));
            float bass = low.Transform(value), mid = middle.Transform(high.Transform(value));
            lowPower += bass * (double)bass;
            midPower += mid * (double)mid;
        }
        if (total == 0) return new(0, 0, 0, 0, 0);
        int first = -1, half = -1, ninety = -1;
        double cumulative = 0;
        for (int i = 0; i < reflected.Length; i++)
        {
            if (first < 0 && Math.Abs(reflected[i]) > peak * 1e-6) first = i;
            cumulative += reflected[i] * (double)reflected[i];
            if (half < 0 && cumulative >= total * .5) half = i;
            if (ninety < 0 && cumulative >= total * .9) ninety = i;
        }
        return new(first * 1000.0 / sampleRate, half * 1000.0 / sampleRate, ninety * 1000.0 / sampleRate,
            late / total, 10 * Math.Log10(Math.Max(lowPower, 1e-300) / Math.Max(midPower, 1e-300)));
    }
}
