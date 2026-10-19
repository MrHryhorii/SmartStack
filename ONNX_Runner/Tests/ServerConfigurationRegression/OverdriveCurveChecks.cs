using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class OverdriveCurveChecks
{
    private sealed record Moment(float Amount, double WetPower, double Cross, double BaselineChangeDb);
    private sealed record Recording(string Name, string Voice, int Rate, float Scale, double DryPower, Moment[] Points);
    private sealed record Fixtures(string SignalPath, float OutputGain, float DriveRange, Recording[] Voices, Recording[] Synthetic);
    private static readonly Fixtures Data = Load();

    /// <summary>Protects the current eased wet path using independently captured powers and cross terms.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Overdrive current fixtures retain 16 voices, three input scales and 21 strengths", () =>
        {
            Require(Data.SignalPath == "amount*(2-amount); post-DC wet; fixed output trim"
                && Data.OutputGain == .5347782f && Data.DriveRange == 4.808956f,
                "The fixture describes a different wet path.");
            Require(Data.Voices.Length == 48 && Data.Voices.Select(x => x.Voice).Distinct().Count() == 16,
                "Incomplete voice catalog.");
            Require(Data.Voices.Count(x => x.Voice == "piper_base") == 3 && Data.Synthetic.Length == 9,
                "Missing primary recordings or synthetic holdouts.");
            foreach (Recording record in Data.Voices.Concat(Data.Synthetic))
            {
                Require(record.DryPower > 0 && record.Points.Length == 21, "Invalid recording.");
                for (int i = 0; i <= 20; i++)
                {
                    Moment point = record.Points[i];
                    Require(point.Amount == i / 20f && double.IsFinite(point.WetPower) && double.IsFinite(point.Cross),
                        "Primary strengths or moments changed.");
                    Require(point.Cross * point.Cross <= record.DryPower * point.WetPower + 1e-14,
                        "The frozen moments violate the signal-power bound.");
                }
            }
        });
        foreach (Recording voice in Data.Voices)
        {
            check($"Overdrive current guide and reverse-step budgets hold for {voice.Name}", () =>
            {
                double full = Change(voice, 20), previous = 0;
                for (int i = 0; i <= 20; i++)
                {
                    double level = Change(voice, i);
                    Require(double.IsFinite(level), "Invalid current level.");
                    Require(Math.Abs(level - i / 20d * full) < 1.634, "The current catalog guide budget regressed.");
                    Require(Math.Sign(full) * (previous - level) < .287, "The current reverse-step budget regressed.");
                    Require(level >= Math.Min(0, full) - 1.272 && level <= Math.Max(0, full) + 1.272,
                        "The current endpoint-window budget regressed.");
                    previous = level;
                }
            });
        }
        check("Overdrive all three base-voice midpoints stay inside their clean/full windows", () =>
        {
            foreach (Recording voice in Data.Voices.Where(x => x.Voice == "piper_base"))
            {
                double middle = Change(voice, 10), full = Change(voice, 20);
                Require(middle >= Math.Min(0, full) && middle <= Math.Max(0, full), "A primary midpoint left its window.");
            }
        });
        check("Overdrive current base-voice mean progresses without the preceding double hump", () =>
        {
            Recording[] voices = Data.Voices.Where(x => x.Voice == "piper_base").ToArray();
            double[] mean = Enumerable.Range(0, 21).Select(i => voices.Average(x => Change(x, i))).ToArray();
            for (int i = 1; i <= 20; i++)
            {
                // Stronger saturation retains a measured 0.00402 dB reverse step with the accepted mixer.
                Require(mean[i] >= mean[i - 1] - .005, "The base mean reversed beyond its small fitting tolerance.");
                Require(mean[i] <= mean[20] + .002 && mean[i] >= -.002, "The base mean left its endpoint window.");
            }
            // The soft criterion is an ordered window; an exact arithmetic midpoint is not required.
            Require(mean[10] >= 0 && mean[10] <= mean[20], "The primary mean midpoint escaped its window.");
            Require(Math.Abs(mean[20] - 1.0334693810110946) < .00001,
                "Stronger saturation changed the calibrated base-mean full level.");
            foreach (Recording voice in voices)
            {
                for (int i = 1; i <= 20; i++)
                    Require(Change(voice, i) >= Change(voice, i - 1) - .03, "A primary recording acquired a large reverse step.");
            }
        });
        check("Overdrive stronger saturation retains the preceding catalog guide-error budgets", () =>
        {
            double meanError = 0, oldMeanError = 0, individualError = 0, oldIndividualError = 0;
            for (int i = 0; i <= 20; i++)
            {
                double mean = 0, oldMean = 0, guide = 0;
                foreach (Recording voice in Data.Voices)
                {
                    double target = i / 20d * Change(voice, 20), current = Change(voice, i);
                    double precedingTarget = i / 20d * voice.Points[20].BaselineChangeDb;
                    mean += current; oldMean += voice.Points[i].BaselineChangeDb; guide += target;
                    individualError += Math.Abs(current - target);
                    oldIndividualError += Math.Abs(voice.Points[i].BaselineChangeDb - precedingTarget);
                }
                meanError += Math.Abs(mean - guide) / (48 * 21);
                double precedingGuide = Data.Voices.Sum(x => i / 20d * x.Points[20].BaselineChangeDb);
                oldMeanError += Math.Abs(oldMean - precedingGuide) / (48 * 21);
            }
            Require(meanError < .215 && meanError <= oldMeanError + .002, "The catalog mean guide error regressed.");
            Require(individualError / (48 * 21) < .27 && individualError <= oldIndividualError + .002 * 48 * 21,
                "The individual catalog guide error regressed.");
        });
        foreach (Recording record in Data.Synthetic)
        foreach (int index in Enumerable.Range(0, 21))
        {
            check($"Overdrive current wet routing matches independent moments for {record.Name} at {index * 5}%", () =>
            {
                float[] signal = OverdriveMixingChecks.Signal(record.Rate, record.Rate * 3, record.Scale);
                OverdriveMixingChecks.NewEngine(record.Rate).ApplyEffect(signal, VoiceEffectType.Overdrive, index / 20f);
                Require(signal.All(float.IsFinite), "The real engine returned nonfinite PCM.");
                double expected = 10 * Math.Log10(record.DryPower) + Change(record, index);
                Require(Math.Abs(OverdriveMixingChecks.Level(signal) - expected) < .002,
                    "The engine differs from its independently captured current wet path.");
            });
        }
    }

    /// <summary>Combines current gains with frozen pre-trim wet moments, including dry/wet correlation.</summary>
    private static double Change(Recording record, int index)
    {
        Moment point = record.Points[index];
        if (point.Amount <= .001f) return 0;
        Dsp.DriveGains(point.Amount, out float dry, out float wet);
        wet *= Data.OutputGain;
        double power = dry * (double)dry * record.DryPower + wet * (double)wet * point.WetPower
            + 2 * dry * (double)wet * point.Cross;
        return 10 * Math.Log10(power / record.DryPower);
    }

    /// <summary>Loads numeric evidence in the test executable only; the server never reads this resource.</summary>
    private static Fixtures Load()
    {
        using Stream stream = typeof(OverdriveCurveChecks).Assembly.GetManifestResourceStream("OverdriveCurveMoments.json")
            ?? throw new InvalidDataException("Missing current Overdrive moments.");
        return JsonSerializer.Deserialize<Fixtures>(stream) ?? throw new InvalidDataException("Invalid current Overdrive moments.");
    }

    /// <summary>Reports a violated current-path contract without treating RMS as a listening assessment.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
