using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CrusherCurveChecks
{
    private sealed record Point(float Amount, double UntrimmedLufs, double ExpectedDeltaLU);
    private sealed record Recording(string Name, float Scale, int Rate, string Group, double DryLufs,
        double MaxGuideErrorLU, double MaxReverseLU, Point[] Points);
    private static readonly Recording[] Recordings = Load();

    /// <summary>Checks the static playback curve against frozen offline loudness measurements.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Crusher playback fixtures retain all recorded PCM probes and the dense base curve", () =>
        {
            Require(Recordings.Length == 31 && Recordings.Sum(x => x.Points.Length) == 1555,
                "Incomplete offline level fixtures.");
            Require(Recordings.Count(x => x.Group == "dense") == 1, "Missing dense base curve.");
            foreach (Recording recording in Recordings)
            {
                Require(recording.Rate == 22050 && recording.Points[0].Amount == 0 && recording.Points[^1].Amount == 1,
                    "Recorded endpoints changed.");
                Require(recording.Points.All(x => double.IsFinite(x.UntrimmedLufs) && double.IsFinite(x.ExpectedDeltaLU)),
                    "Invalid recorded loudness.");
            }
        });
        foreach (Recording recording in Recordings)
        {
            check($"Crusher static playback correction retains recorded bounds for {recording.Name} at scale {recording.Scale:R}", () =>
            {
                double full = recording.Points[^1].ExpectedDeltaLU, previous = 0;
                foreach (Point point in recording.Points)
                {
                    double value = Level(recording, point);
                    Require(Math.Abs(value - point.ExpectedDeltaLU) < .005,
                        "Playback correction no longer matches the recorded full-wet curve.");
                    Require(Math.Abs(value - point.Amount * full) <= recording.MaxGuideErrorLU,
                        "The measured clean/full guide error increased.");
                    Require(Math.Sign(full) * (previous - value) <= recording.MaxReverseLU,
                        "The measured local reverse-step budget increased.");
                    previous = value;
                }
            });
        }
        check("Crusher dense base curve keeps its measured small reverse steps and meaningful midpoint", () =>
        {
            Recording recording = Recordings.Single(x => x.Group == "dense");
            double previous = 0;
            foreach (Point point in recording.Points)
            {
                double value = Level(recording, point);
                Require(previous - value < .02, "A large base-voice reverse step returned.");
                previous = value;
            }
            double midpoint = Level(recording, recording.Points.Single(x => x.Amount == .5f));
            double full = Level(recording, recording.Points[^1]);
            Require(midpoint > .35 * full && midpoint < .65 * full, "The base midpoint left its useful level window.");
        });
        foreach (int rate in new[] { 8000, 16000, 22050, 48000 })
        {
            check($"Crusher preserves digital silence and its full four-bit dead zone at {rate} Hz", () =>
            {
                for (int i = 0; i <= 20; i++)
                {
                    var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456);
                    float[] silence = new float[rate / 4];
                    engine.ApplyEffect(silence, VoiceEffectType.Bitcrusher, i / 20f);
                    Require(silence.All(x => x == 0f), "Crusher introduced sound into digital silence.");
                }
                float[] quiet = new float[rate];
                for (int i = 0; i < quiet.Length; i++) quiet[i] = .03f * MathF.Sin(2 * MathF.PI * 220 * i / rate);
                var full = new AudioEffectsEngine(new(), rate); full.Reset(123456);
                full.ApplyEffect(quiet, VoiceEffectType.Bitcrusher, 1f);
                Require(quiet.All(x => x == 0f), "The four-bit dead zone unexpectedly restored quiet input.");
            });
        }
    }

    /// <summary>Applies only a scalar to recorded wet loudness; no runtime audio normalization is involved.</summary>
    private static double Level(Recording recording, Point point) => point.Amount <= .001f ? 0
        : point.UntrimmedLufs - recording.DryLufs + 20 * Math.Log10(Dsp.CrushGain(point.Amount));

    private static Recording[] Load()
    {
        using Stream stream = typeof(CrusherCurveChecks).Assembly.GetManifestResourceStream("CrusherCurveMoments.json")
            ?? throw new InvalidDataException("Missing offline Crusher levels.");
        return JsonSerializer.Deserialize<Recording[]>(stream) ?? throw new InvalidDataException("Invalid offline Crusher levels.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
