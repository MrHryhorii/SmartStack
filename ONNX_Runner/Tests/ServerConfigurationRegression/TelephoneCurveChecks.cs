using System.Text.Json;
using ONNX_Runner.Services;

internal static class TelephoneCurveChecks
{
    private sealed record Moment(float Amount, double WetPower, double Cross);
    private sealed record Recording(string Name, int Rate, double DryPower, Moment[] Points);
    private sealed record FixtureData(Recording[] Voices);

    /// <summary>Exercises the mixer using powers and correlations captured from the existing voice catalog.</summary>
    internal static void Run(Action<string, Action> check)
    {
        using Stream stream = typeof(TelephoneCurveChecks).Assembly.GetManifestResourceStream("TelephoneCurveMoments.json")
            ?? throw new InvalidDataException("Missing Telephone endpoint moments.");
        var data = JsonSerializer.Deserialize<FixtureData>(stream)
            ?? throw new InvalidDataException("Invalid Telephone endpoint moments.");
        check("Telephone endpoint fixtures cover 16 voices and three texts", () =>
            Require(data.Voices.Length == 48 && data.Voices.Select(x => x.Name.Split('-')[0]).Distinct().Count() == 16,
                "The recorded endpoint catalog is incomplete."));
        foreach (Recording voice in data.Voices)
            check($"Power-matched Telephone has ordered pre-limiter power for {voice.Name}", () =>
            {
                Moment full = voice.Points.Single(point => point.Amount == 1f);
                double rho = Math.Clamp(full.Cross / Math.Sqrt(voice.DryPower * full.WetPower), -1, 1);
                const int count = 512;
                float[] dry = new float[count], wet = new float[count], work = new float[count];
                // Orthogonal tones reproduce the recorded endpoint powers and their correlation.
                for (int i = 0; i < count; i++)
                {
                    double phase = 2 * Math.PI * 13 * i / count;
                    dry[i] = (float)(Math.Sqrt(2 * voice.DryPower) * Math.Cos(phase));
                    wet[i] = (float)(Math.Sqrt(2 * full.WetPower) *
                        (rho * Math.Cos(phase) + Math.Sqrt(1 - rho * rho) * Math.Sin(phase)));
                }
                double previous = voice.DryPower;
                for (int step = 0; step <= 100; step++)
                {
                    float amount = step / 100f;
                    wet.CopyTo(work, 0);
                    Require(PowerMatchedMixer.Mix(dry, work, amount).Matched, "The catalog endpoint mix cancelled.");
                    double measured = Power(work);
                    double target = (1 - (double)amount) * voice.DryPower + amount * full.WetPower;
                    Require(Math.Abs(10 * Math.Log10(measured / target)) < 2e-6,
                        "The mix differs from the independently recorded endpoint-power target.");
                    Require(Math.Sign(full.WetPower - voice.DryPower) * (previous - measured) < 1e-10,
                        "The pre-limiter power reversed direction.");
                    previous = measured;
                }
            });
    }

    private static double Power(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float sample in samples) sum += sample * (double)sample;
        return sum / samples.Length;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
