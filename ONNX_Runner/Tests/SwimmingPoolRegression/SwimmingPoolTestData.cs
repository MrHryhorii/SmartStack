namespace ONNX_Runner.Tests;

public static class SwimmingPoolTestData
{
    public static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000, 192000];
    public static readonly string[] Environments = ["SwimmingPool"];
    public const int ReferenceSeconds = 15;

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
}

public sealed record SwimmingPoolFixtureCorpus(string Source, SwimmingPoolFixture[] References);
public sealed record SwimmingPoolFixture(int Rate, string Environment, float Amount, double Energy, int[] Indices, float[] Samples, int WarmupFrames = 0);
