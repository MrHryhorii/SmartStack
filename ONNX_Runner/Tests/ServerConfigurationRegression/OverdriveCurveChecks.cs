using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class OverdriveCurveChecks
{
    private static readonly Corpus Fixtures = Load();

    /// <summary>Retains independent full-strength PCM fixtures; intermediate powers are measured at runtime.</summary>
    internal static void Run(Action<string, Action> check)
    {
        foreach (SyntheticRecord record in Fixtures.Synthetic)
        {
            check($"Overdrive retains accepted full PCM at {record.Rate} Hz and scale {record.Scale:R}", () =>
            {
                float[] actual = OverdriveMixingChecks.Signal(record.Rate, record.Rate*3, record.Scale);
                OverdriveMixingChecks.NewEngine(record.Rate).ApplyEffect(actual, VoiceEffectType.Overdrive, 1f);
                Require(actual.All(float.IsFinite), "Non-finite full output.");
                Require(Math.Abs(OverdriveMixingChecks.RmsDb(actual)-record.RmsDbfs) < .002, "Full output level changed.");
                Require(record.Indices.Length == 16 && record.Samples.Length == 16, "Incomplete full PCM fixture.");
                for (int i = 0; i < record.Indices.Length; i++)
                    Require(Math.Abs(actual[record.Indices[i]]-record.Samples[i]) < 3e-6, "Full output waveform changed.");
            });
        }
    }

    private static Corpus Load()
    {
        using Stream stream = typeof(OverdriveCurveChecks).Assembly.GetManifestResourceStream("OverdriveCurveMoments.json")
            ?? throw new InvalidDataException("Missing direct Overdrive level fixtures.");
        return JsonSerializer.Deserialize<Corpus>(stream) ?? throw new InvalidDataException("Invalid direct Overdrive fixtures.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Corpus(SyntheticRecord[] Synthetic);
    private sealed record SyntheticRecord(int Rate, float Scale, double RmsDbfs, int[] Indices, float[] Samples);
}
