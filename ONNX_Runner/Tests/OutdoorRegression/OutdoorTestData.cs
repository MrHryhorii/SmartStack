namespace ONNX_Runner.Tests;

public static class OutdoorTestData
{
    public const int ReferenceSeconds = 10;
    public static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000, 192000];
    public static readonly string[] Environments = ["Canyon"];

    public static float[] Signal(int rate, int length)
    {
        float[] samples = new float[length];
        uint state = 0x834af1;
        for (int i = 0; i < samples.Length; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            samples[i] = .15f * MathF.Sin(2 * MathF.PI * 211 * i / rate)
                + .08f * MathF.Sin(2 * MathF.PI * 1713 * i / rate)
                + (state / (float)uint.MaxValue * 2 - 1) * .025f;
        }
        samples[0] = .4f;
        return samples;
    }

    public static OutdoorImpulseMetrics Measure(ReadOnlySpan<float> reflected, int sampleRate)
    {
        double total = 0, late = 0;
        float peak = 0;
        for (int i = 0; i < reflected.Length; i++)
        {
            double power = reflected[i] * (double)reflected[i];
            total += power;
            if (i >= sampleRate) late += power;
            peak = Math.Max(peak, Math.Abs(reflected[i]));
        }
        if (total == 0) return new(0, 0, 0, 0);
        int first = -1, ninety = -1;
        double cumulative = 0;
        for (int i = 0; i < reflected.Length; i++)
        {
            if (first < 0 && Math.Abs(reflected[i]) > peak * 1e-6) first = i;
            cumulative += reflected[i] * (double)reflected[i];
            if (ninety < 0 && cumulative >= total * .9) ninety = i;
        }
        return new(first * 1000.0 / sampleRate, ninety * 1000.0 / sampleRate, late / total, total);
    }
}

public sealed record OutdoorImpulseMetrics(double FirstReflectionMilliseconds, double NinetyPercentEnergyMilliseconds,
    double EnergyAfterOneSecondFraction, double ReflectedEnergy);
public sealed record OutdoorFixtureCorpus(string Source, OutdoorFixture[] References);
public sealed record OutdoorFixture(int Rate, string Environment, float Amount, double Energy, int[] Indices, float[] Samples);
