using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class OverdriveCurveChecks
{
    private static readonly Corpus Fixtures = Load();

    /// <summary>Checks captured voice powers and independent synthetic PCM references.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Overdrive retains measured voices, the dense base curve and independent endpoints", () =>
        {
            Require(Fixtures.Voices.Length == 31 && Fixtures.Synthetic.Length == 27, "Incomplete Overdrive fixtures.");
            Require(Fixtures.Voices.Count(x => x.Group == "dense") == 1, "Missing dense base curve.");
            Require(Fixtures.Routing.Length == 9, "Missing independent synthetic routing references.");
        });
        foreach (VoiceRecord voice in Fixtures.Voices)
        {
            check($"Overdrive retains measured RMS bounds for {voice.Name} at scale {voice.Scale:R}", () =>
            {
                Require(voice.Points[0].Amount == 0 && voice.Points[^1].Amount == 1, "Missing endpoints.");
                double full = voice.Points[^1].ExpectedDeltaDb, previous = 0;
                foreach (Point point in voice.Points)
                {
                    double level = Level(voice, point);
                    Require(Math.Abs(level-point.ExpectedDeltaDb) < .0001, "Scalar correction differs from measured PCM.");
                    Require(Math.Abs(level-point.Amount*full) <= voice.GuideBudgetDb, "The measured guide budget regressed.");
                    Require(Math.Sign(full)*(previous-level) <= voice.ReverseBudgetDb, "The measured reverse-step budget regressed.");
                    previous = level;
                }
            });
        }
        check("Overdrive dense base RMS remains ordered with a useful level midpoint", () =>
        {
            VoiceRecord voice = Fixtures.Voices.Single(x => x.Group == "dense");
            double previous = 0;
            foreach (Point point in voice.Points)
            {
                double level = Level(voice, point);
                Require(previous-level < .04, "A large base-voice reverse step returned.");
                previous = level;
            }
            double midpoint = Level(voice, voice.Points.Single(p => p.Amount == .5f));
            double full = voice.Points[^1].ExpectedDeltaDb;
            Require(midpoint > .35*full && midpoint < .65*full, "The base midpoint left its level window.");
        });
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
        foreach (RoutingRecord record in Fixtures.Routing)
        {
            check($"Overdrive direct routing matches captured intermediate PCM at {record.Rate} Hz and scale {record.Scale:R}", () =>
            {
                Require(record.Points.Length == 21, "Incomplete intermediate routing fixture.");
                foreach (RoutingPoint point in record.Points)
                {
                    float[] input = OverdriveMixingChecks.Signal(record.Rate, record.Rate*3, record.Scale);
                    OverdriveMixingChecks.NewEngine(record.Rate).ApplyEffect(input, VoiceEffectType.Overdrive, point.Amount);
                    Require(input.All(float.IsFinite), "Non-finite direct output.");
                    Require(Math.Abs(OverdriveMixingChecks.RmsDb(input)-point.ExpectedRmsDbfs) < .002,
                        "Direct routing differs from the independently captured PCM powers.");
                }
            });
        }
    }

    private static double Level(VoiceRecord voice, Point point) => point.Amount <= .001f ? 0
        : point.UntrimmedRmsDbfs-voice.DryRmsDbfs+20*Math.Log10(Dsp.DriveGain(point.Amount));

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

    private sealed record Corpus(VoiceRecord[] Voices, SyntheticRecord[] Synthetic, RoutingRecord[] Routing);
    private sealed record VoiceRecord(string Name, string Group, float Scale, double DryRmsDbfs, double GuideBudgetDb, double ReverseBudgetDb, Point[] Points);
    private sealed record Point(float Amount, double UntrimmedRmsDbfs, double ExpectedDeltaDb);
    private sealed record SyntheticRecord(int Rate, float Scale, double RmsDbfs, int[] Indices, float[] Samples);
    private sealed record RoutingRecord(int Rate, float Scale, RoutingPoint[] Points);
    private sealed record RoutingPoint(float Amount, double UntrimmedRmsDbfs, double ExpectedRmsDbfs);
}
