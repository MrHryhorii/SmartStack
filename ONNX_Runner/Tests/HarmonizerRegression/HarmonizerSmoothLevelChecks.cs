internal static class HarmonizerSmoothLevelChecks
{
    internal static void Run(Action<string, Action> check, List<object> evidence)
    {
        foreach (int rate in new[] { 8000, 16000, 22050, 24000, 44100, 48000, 96000, 192000 })
        {
            foreach (float amount in new[] { .5f, 1f })
                check($"Reference level controls correction continuously: {rate} Hz, {amount}", () =>
                {
                    double previous = 0;
                    foreach (double power in new[] { 0d, 1e-12, 1e-11, 1e-10, 1e-9, 4e-9, 1e-8, 2e-8, 1e-7, 1e-6, 1e-4 })
                    {
                        double gain = SettledGain(rate, power, amount);
                        evidence.Add(new { Kind = "ReferenceLevelCurve", Rate = rate, Amount = amount, ReferencePower = power, Gain = gain });
                        Require(gain + 1e-9 >= previous, "The correction reversed direction as the original voice became louder.");
                        Require(gain <= 2 + 1e-8, "The reference-level weight exceeded the measured correction.");
                        previous = gain;
                    }
                    double nominal = HarmonizerRegression.NewLevel(rate, amount).Gain();
                    Require(Math.Abs(SettledGain(rate, 0, amount) - nominal) < 1e-12, "Silence did not end at nominal gain.");
                    double middle = SettledGain(rate, 1e-8, amount);
                    Require(middle > nominal + .3 * (2 - nominal) && middle < nominal + .7 * (2 - nominal),
                        "Intermediate original levels still select only on or off correction.");
                });

            check($"The former activity threshold has no correction jump: {rate} Hz", () =>
            {
                double below = SettledGain(rate, 1e-8 * .999);
                double above = SettledGain(rate, 1e-8 * 1.001);
                Require(above >= below && above - below < .005, "Crossing the old threshold changed correction abruptly.");
                Require(below > 1.4 && above < 1.6, "The former threshold is not inside the continuous transition.");
            });

            check($"Genuine quiet speech retains useful cancellation correction: {rate} Hz", () =>
            {
                foreach (float amplitude in new[] { .001f, .003f, .01f })
                {
                    var (process, _) = HarmonizerRegression.NewLevel(rate);
                    double referencePower = 0, outputPower = 0;
                    for (int i = 0; i < rate; i++)
                    {
                        float reference = amplitude * (float)Math.Sin(2 * Math.PI * 180 * i / rate);
                        float output = process(reference, reference * .5f);
                        if (i < rate / 2) continue;
                        referencePower += reference * (double)reference;
                        outputPower += output * (double)output;
                    }
                    Require(Math.Abs(10 * Math.Log10(outputPower / referencePower)) < .02,
                        "Confidence fading removed useful correction from genuine quiet speech.");
                }
            });

            check($"A processed cancellation cannot classify a strong original voice as silence: {rate} Hz", () =>
            {
                var (process, gain) = HarmonizerRegression.NewLevel(rate);
                for (int i = 0; i < rate / 2; i++)
                {
                    float reference = i % 2 == 0 ? .01f : -.01f;
                    process(reference, reference * .01f);
                }
                Require(gain() > 3.99 && gain() <= 4, "Processed volume suppressed correction of an original active voice.");
            });

            check($"Correction recedes during a quiet fade before exact silence: {rate} Hz", () =>
            {
                var (process, gain) = HarmonizerRegression.NewLevel(rate);
                double initial = 0, quiet = 0;
                for (int i = 0; i < rate; i++)
                {
                    double amplitude = i < rate / 4 ? .0002 : .0002 * Math.Pow(.005, Math.Min(1, (i - rate / 4d) / (rate / 2d)));
                    float reference = (float)(i % 2 == 0 ? amplitude : -amplitude);
                    process(reference, reference * .5f);
                    if (i == rate / 4 - 1) initial = gain();
                    if (i == rate * 3 / 4) quiet = gain();
                }
                Require(initial > 1.8 && quiet < 1.01, "Only exact silence released the previous cancellation boost.");
                for (int i = 0; i < rate / 10; i++) Require(process(0, 0) == 0, "The release fabricated sound in silence.");
                Require(gain() == 1, "Exact silence did not reach the nominal endpoint.");
            });

            check($"One DC edge does not establish sustained confidence: {rate} Hz", () =>
            {
                var (process, gain) = HarmonizerRegression.NewLevel(rate);
                double maximum = 1;
                for (int i = 0; i < rate / 2; i++)
                {
                    float reference = i < 37 ? 0 : .001f;
                    float processed = reference * (float)Math.Exp(-Math.Max(0, i - 37) / (rate * .005));
                    process(reference, processed);
                    maximum = Math.Max(maximum, gain());
                }
                Require(maximum < 1.15 && Math.Abs(gain() - 1) < 1e-8, "A single DC edge established persistent or excessive correction.");
            });
        }
    }

    private static double SettledGain(int rate, double power, float amount = 1)
    {
        var (process, gain) = HarmonizerRegression.NewLevel(rate, amount);
        float amplitude = (float)Math.Sqrt(power);
        for (int i = 0; i < rate / 3; i++)
        {
            float reference = i % 2 == 0 ? amplitude : -amplitude;
            process(reference, reference * .5f);
        }
        return gain();
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
