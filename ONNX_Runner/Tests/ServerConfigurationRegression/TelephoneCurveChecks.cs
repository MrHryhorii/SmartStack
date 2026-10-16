using System.Text.Json;
using ONNX_Runner.Services;

internal static class TelephoneCurveChecks
{
    internal sealed record Moment(float Amount, double WetPower, double Cross, double BaselineChangeDb = 0);
    internal sealed record Recording(string Name, int Rate, double DryPower, Moment[] Points);
    private sealed record FixtureData(Recording[] Voices, Recording[] HistoricalVoices);

    private static readonly FixtureData Data = Load();
    private static readonly Dictionary<string, Recording> Historical = Data.HistoricalVoices.ToDictionary(x => x.Name, StringComparer.Ordinal);

    /// <summary>Checks the current eased Telephone path against independently captured catalog moments.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Telephone curve fixtures cover all 16 voices and three texts at 21 strengths", () =>
        {
            Require(Data.Voices.Length == 48 && Data.Voices.Select(x => x.Name.Split('-')[0]).Distinct().Count() == 16,
                "The original voice catalog is incomplete.");
            foreach (Recording voice in Data.Voices)
            {
                Require(voice.Points.Length == 21 && voice.DryPower > 0, "Invalid current-path moments.");
                for (int i = 0; i <= 20; i++)
                    Require(voice.Points[i].Amount == i / 20f, "Calibration strengths changed.");
            }
        });
        foreach (Recording voice in Data.Voices)
        {
            check($"Telephone current path retains primary loudness bounds for {voice.Name}", () =>
            {
                double full = Level(voice, 1f), previous = 0;
                foreach (Moment point in voice.Points)
                {
                    double value = Level(voice, point.Amount);
                    Require(Math.Abs(value - point.Amount * full) < 1.81, "The current path exceeded the existing guide budget.");
                    Require(Math.Sign(full) * (previous - value) < .36, "The current path exceeded its stricter reverse-step budget.");
                    if (point.Amount == .5f)
                    {
                        double escape = Math.Max(Math.Min(0, full) - value, 0) + Math.Max(value - Math.Max(0, full), 0);
                        Require(escape < .55, "The current midpoint exceeded the existing escape budget.");
                    }
                    previous = value;
                }
            });
        }
        check("Telephone current catalog mean progresses and improves the accepted mixer guide", () =>
        {
            double full = Data.Voices.Average(x => Level(x, 1f)), previous = 0;
            double oldError = 0, newError = 0;
            for (int i = 0; i <= 20; i++)
            {
                float t = i / 20f;
                double value = Data.Voices.Average(x => Level(x, t));
                double oldValue = Data.Voices.Average(x => x.Points[i].BaselineChangeDb);
                Require(value <= previous + 1e-5, "The current catalog mean reversed its falling direction.");
                Require(Math.Abs(value - t * full) < .5, "The current mean moved more than half a dB from its guide.");
                if (i == 10) Require(Math.Abs(value - .5 * full) < .2, "The mean midpoint moved away from the preferred dB center.");
                oldError += Math.Abs(oldValue - t * full);
                newError += Math.Abs(value - t * full);
                previous = value;
            }
            Require(newError < oldError * .5, "The new mean curve did not halve the former guide error.");
        });
        check("Telephone current midpoint window coverage retains at least 41 of 48 recordings", () =>
        {
            int count = Data.Voices.Count(x => Level(x, .5f) >= Math.Min(0, Level(x, 1f))
                && Level(x, .5f) <= Math.Max(0, Level(x, 1f)));
            Require(count >= 41, "Current midpoint-window coverage regressed.");
        });
        check("Telephone refinement reduces both catalog-mean and individual curve bending", () =>
        {
            double oldMean = 0, newMean = 0, oldIndividual = 0, newIndividual = 0;
            for (int i = 2; i <= 20; i++)
            {
                double oldSum = 0, newSum = 0;
                foreach (Recording voice in Data.Voices)
                {
                    double oldBend = voice.Points[i].BaselineChangeDb - 2 * voice.Points[i - 1].BaselineChangeDb
                        + voice.Points[i - 2].BaselineChangeDb;
                    double newBend = Level(voice, i / 20f) - 2 * Level(voice, (i - 1) / 20f)
                        + Level(voice, (i - 2) / 20f);
                    oldIndividual += oldBend * oldBend;
                    newIndividual += newBend * newBend;
                    oldSum += oldBend / Data.Voices.Length;
                    newSum += newBend / Data.Voices.Length;
                }
                oldMean += oldSum * oldSum;
                newMean += newSum * newSum;
            }
            Require(newMean < oldMean && newIndividual < oldIndividual,
                "The new curve introduced more level bending than the accepted baseline.");
        });
        check("Telephone rational refinement retains the preceding quintic quality budgets", CheckQuinticBudgets);
    }

    /// <summary>Protects the preceding quintic curve's measured quality without fixing the new coefficients.</summary>
    private static void CheckQuinticBudgets()
    {
        double guideError = 0, meanBending = 0, individualBending = 0;
        int midpointWindows = 0;
        var means = new double[21];
        foreach (Recording voice in Data.Voices)
        {
            double full = Level(voice, 1f), midpoint = Level(voice, .5f);
            if (midpoint >= Math.Min(0, full) && midpoint <= Math.Max(0, full)) midpointWindows++;
            for (int i = 0; i <= 20; i++)
            {
                double value = Level(voice, i / 20f);
                means[i] += value / Data.Voices.Length;
                if (i < 2) continue;
                double bend = value - 2 * Level(voice, (i - 1) / 20f) + Level(voice, (i - 2) / 20f);
                individualBending += bend * bend / (Data.Voices.Length * 19);
            }
        }
        for (int i = 0; i <= 20; i++)
        {
            guideError += Math.Abs(means[i] - i / 20d * means[20]) / 21;
            if (i < 2) continue;
            double bend = means[i] - 2 * means[i - 1] + means[i - 2];
            meanBending += bend * bend / 19;
        }
        // Frozen version-59 measurements, with small float-evaluation margins.
        Require(guideError < .1469 && meanBending < .000342 && individualBending < .003074,
            "The simpler formula regressed the preceding quintic curve's quality.");
        Require(midpointWindows >= 43, "The simpler formula regressed the preceding midpoint coverage.");
    }

    /// <summary>Re-evaluates an existing historical control using its independent dry/wet moments.</summary>
    internal static double HistoricalLevel(string name, float amount) => Level(Historical[name], amount);

    /// <summary>Reconstructs the historical uncorrected reference without assuming the new mix ratio.</summary>
    internal static double HistoricalEqualPowerLevel(string name, float amount) => Level(Historical[name], amount, true);

    /// <summary>Checks existing dense catalog controls with their original equal recording weights.</summary>
    internal static double HistoricalMean(float amount, bool equalPower = false)
    {
        double sum = 0;
        foreach (Recording voice in Data.HistoricalVoices) sum += Level(voice, amount, equalPower);
        return sum / Data.HistoricalVoices.Length;
    }

    /// <summary>Computes exact mixed power and correlation from samples captured before the mixer.</summary>
    private static double Level(Recording voice, float amount, bool equalPower = false)
    {
        if (amount <= .001f) return 0;
        Moment point = Array.Find(voice.Points, x => x.Amount == amount)
            ?? throw new InvalidDataException($"Missing Telephone control {voice.Name} at {amount:R}.");
        float dry, wet;
        if (equalPower) Dsp.EqualPowerGains(amount, out dry, out wet);
        else Dsp.TelephoneGains(amount, out dry, out wet);
        double power = dry * (double)dry * voice.DryPower + wet * (double)wet * point.WetPower
            + 2 * dry * (double)wet * point.Cross;
        return 10 * Math.Log10(power / voice.DryPower);
    }

    /// <summary>Loads numeric fixtures embedded only in this test executable.</summary>
    private static FixtureData Load()
    {
        using Stream stream = typeof(TelephoneCurveChecks).Assembly.GetManifestResourceStream("TelephoneCurveMoments.json")
            ?? throw new InvalidDataException("Missing independent Telephone moments.");
        return JsonSerializer.Deserialize<FixtureData>(stream)
            ?? throw new InvalidDataException("Invalid independent Telephone moments.");
    }

    /// <summary>Reports a violated catalog-mixer contract.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
