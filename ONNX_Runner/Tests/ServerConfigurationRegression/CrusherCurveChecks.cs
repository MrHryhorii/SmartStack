using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CrusherCurveChecks
{
    private sealed record Moment(float Amount, double WetPower, double Cross, double BaselineChangeDb);
    private sealed record Recording(string Name, string Voice, int Rate, double DryPower, Moment[] Points);
    private static readonly Recording[] Voices = Load();

    /// <summary>Protects current Crusher progression using independent powers and cross terms.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Crusher current fixtures retain all 16 voices and 21 original strengths", () =>
        {
            Require(Voices.Length == 48 && Voices.Select(x => x.Voice).Distinct().Count() == 16, "Incomplete voice catalog.");
            foreach (Recording voice in Voices)
            {
                Require(voice.Rate == 22050 && voice.DryPower > 0 && voice.Points.Length == 21, "Invalid original recording.");
                for (int i = 0; i <= 20; i++) Require(voice.Points[i].Amount == i / 20f, "Primary strengths changed.");
            }
        });
        foreach (Recording voice in Voices)
        {
            check($"Crusher current guide and reverse-step budgets hold for {voice.Name}", () =>
            {
                double full = Level(voice, 20), previous = 0;
                for (int i = 0; i <= 20; i++)
                {
                    double value = Level(voice, i);
                    Require(double.IsFinite(value), "Invalid current level.");
                    Require(Math.Abs(value - i / 20d * full) < .8921, "The preceding worst-case voice error regressed.");
                    Require(Math.Sign(full) * (previous - value) < .3501, "The reverse-step reduction regressed.");
                    previous = value;
                }
            });
        }
        check("Crusher joined halves reduce catalog mean error and curve bending", () =>
        {
            double fullMean = Voices.Average(x => Level(x, 20));
            double error = 0, oldError = 0, bend = 0, oldBend = 0, individualBend = 0, oldIndividualBend = 0;
            var mean = new double[21]; var original = new double[21];
            for (int i = 0; i <= 20; i++)
            {
                mean[i] = Voices.Average(x => Level(x, i));
                original[i] = Voices.Average(x => x.Points[i].BaselineChangeDb);
                error += Math.Abs(mean[i] - i / 20d * fullMean) / 21;
                oldError += Math.Abs(original[i] - i / 20d * fullMean) / 21;
                Require(Math.Abs(mean[i] - i / 20d * fullMean) < .15, "The near-flat catalog guide budget regressed.");
                if (i < 2) continue;
                bend += Math.Pow(mean[i] - 2 * mean[i - 1] + mean[i - 2], 2);
                oldBend += Math.Pow(original[i] - 2 * original[i - 1] + original[i - 2], 2);
                foreach (Recording voice in Voices)
                {
                    double a = Level(voice, i) - 2 * Level(voice, i - 1) + Level(voice, i - 2);
                    double b = voice.Points[i].BaselineChangeDb - 2 * voice.Points[i - 1].BaselineChangeDb + voice.Points[i - 2].BaselineChangeDb;
                    individualBend += a * a; oldIndividualBend += b * b;
                }
            }
            Require(error < .07 && error < oldError * .3, "The mean-error reduction regressed.");
            Require(bend < oldBend * .05 && individualBend < oldIndividualBend * .15, "Curve bending no longer improves.");
        });
        check("Crusher midpoint window coverage retains the measured improvement", () =>
        {
            int count = Voices.Count(x => InWindow(Level(x, 10), Level(x, 20)));
            Require(count >= 21, "Current midpoint-window coverage regressed.");
        });
        check("Crusher all three base-voice midpoints remain inside their clean/full windows", () =>
        {
            Recording[] voices = Voices.Where(x => x.Voice == "piper_base").ToArray();
            Require(voices.Length == 3 && voices.All(x => InWindow(Level(x, 10), Level(x, 20))), "A base midpoint left its window.");
        });
        check("Crusher joined halves limit the measured base-voice hump and reverse steps", () =>
        {
            Recording[] voices = Voices.Where(x => x.Voice == "piper_base").ToArray();
            double[] mean = Enumerable.Range(0, 21).Select(i => voices.Average(x => Level(x, i))).ToArray();
            double reverse = 0;
            for (int i = 1; i <= 20; i++)
            {
                double fall = Math.Max(mean[i-1]-mean[i], 0);
                Require(fall < (i <= 10 ? .0052 : .0502), "The mean base-voice transition hump regressed.");
                reverse += fall;
            }
            Require(mean.Max()-mean[20] < .0502 && reverse < .136,
                "The preceding global mixer's base-voice wave returned.");
        });
        foreach (int rate in new[] { 8000, 22050, 48000 })
        {
            check($"Crusher preserves digital silence and quiet full quantization at {rate} Hz", () =>
            {
                for (int i = 0; i <= 20; i++)
                {
                    var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456);
                    float[] silence = new float[rate / 4];
                    engine.ApplyEffect(silence, VoiceEffectType.Bitcrusher, i / 20f);
                    Require(silence.All(x => x == 0f), "The mixer introduced sound into digital silence.");
                }
                float[] quiet = new float[rate];
                for (int i = 0; i < quiet.Length; i++) quiet[i] = .03f * MathF.Sin(2 * MathF.PI * 220 * i / rate);
                var full = new AudioEffectsEngine(new(), rate); full.Reset(123456);
                full.ApplyEffect(quiet, VoiceEffectType.Bitcrusher, 1);
                Require(quiet.All(x => x == 0f), "The full mixer changed the four-bit dead zone.");
            });
        }
    }

    /// <summary>Evaluates independent frozen moments using only prepared production weights.</summary>
    private static double Level(Recording voice, int index)
    {
        Moment point = voice.Points[index];
        if (point.Amount <= .001f) return 0;
        Dsp.CrusherGains(point.Amount, out float dry, out float wet);
        double power = dry * (double)dry * voice.DryPower + wet * (double)wet * point.WetPower + 2 * dry * (double)wet * point.Cross;
        return 10 * Math.Log10(power / voice.DryPower);
    }

    /// <summary>Tests the soft clean/full midpoint window without imposing an exact halfway level.</summary>
    private static bool InWindow(double value, double full) => value >= Math.Min(0, full) && value <= Math.Max(0, full);

    /// <summary>Loads numeric fixtures only in the regression executable.</summary>
    private static Recording[] Load()
    {
        using Stream stream = typeof(CrusherCurveChecks).Assembly.GetManifestResourceStream("CrusherCurveMoments.json")
            ?? throw new InvalidDataException("Missing current Crusher moments.");
        return JsonSerializer.Deserialize<Recording[]>(stream) ?? throw new InvalidDataException("Invalid Crusher moments.");
    }

    /// <summary>Reports a violated current-curve contract.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
