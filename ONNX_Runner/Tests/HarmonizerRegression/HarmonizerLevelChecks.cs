using ONNX_Runner.Services.Audio.Levels;

internal static class HarmonizerLevelChecks
{
    internal static readonly List<object> Evidence = [];

    internal static void Run(Action<string, Action> check)
    {
        foreach (int rate in new[] { 8000, 16000, 22050, 24000, 44100, 48000, 96000, 192000 })
        {
            foreach (double frequency in new[] { 120d, 180d, 1000d })
            foreach (int durationMs in new[] { 40, 60, 100 })
                check($"Local power correction recovers a {durationMs} ms cancellation at {frequency} Hz, {rate} Hz PCM", () =>
                {
                    var (process, _) = HarmonizerRegression.NewLevel(rate);
                    var old = new WindowedPowerMatcher(rate);
                    float[] reference = Tone(rate, rate, frequency), actual = new float[rate], previous = new float[rate];
                    int start = rate / 3, length = rate * durationMs / 1000, end = start + length;
                    for (int i = 0; i < rate; i++)
                    {
                        float mixed = reference[i] * (i >= start && i < end ? .5f : 1f);
                        actual[i] = process(reference[i], mixed);
                        previous[i] = old.Process(reference[i], mixed);
                    }
                    double oldDip = Ratio(previous, reference, start + length / 2, length / 2);
                    double newDip = Ratio(actual, reference, start + length / 2, length / 2);
                    double recovery = Ratio(actual, reference, end, rate / 50);
                    Evidence.Add(new { Rate = rate, Frequency = frequency, DurationMs = durationMs,
                        OldLateDipDb = oldDip, NewLateDipDb = newDip, Recovery20MsDb = recovery });
                    Require(newDip > oldDip + 3 && newDip > -1.3, $"The local dip remained too deep: {newDip:F3} dB (old {oldDip:F3} dB).");
                    Require(recovery < 2.5, $"The correction retained excessive gain after the dip: {recovery:F3} dB.");
                });

            check($"Steady attenuation is restored without changing polarity or delaying samples: {rate} Hz", () =>
            {
                var (process, gain) = HarmonizerRegression.NewLevel(rate);
                double referencePower = 0, outputPower = 0;
                for (int i = 0; i < rate * 2; i++)
                {
                    float reference = i % 2 == 0 ? .2f : -.2f;
                    float mixed = reference * -.5f;
                    float output = process(reference, mixed);
                    Require(Math.Sign(output) == Math.Sign(mixed), "Correction mixed in a clean waveform or reversed polarity.");
                    if (i < rate) continue;
                    referencePower += reference * (double)reference;
                    outputPower += output * (double)output;
                }
                Require(Math.Abs(10 * Math.Log10(outputPower / referencePower)) < .001 && Math.Abs(gain() - 2) < 1e-8,
                    "Steady inverse attenuation is incorrect.");
                Require(process(.75f, .75f) == 1.5f, "The correction clipped floating-point headroom or delayed the output.");
            });

            check($"Deep cancellations retain the gain and rise limits: {rate} Hz", () =>
            {
                var (process, gain) = HarmonizerRegression.NewLevel(rate);
                for (int i = 0; i < rate; i++)
                {
                    double before = gain();
                    float reference = i % 2 == 0 ? .2f : -.2f;
                    Require(float.IsFinite(process(reference, reference * .001f)), "A deep cancellation produced non-finite PCM.");
                    double current = gain();
                    Require(current <= 4 && current > 0, "The correction exceeded its gain bound.");
                    Require(20 * Math.Log10(current / before) * rate <= 480.00001, "Gain rose too quickly in audio time.");
                }
                Require(gain() > 3.99, "A deep recoverable cancellation did not approach the gain bound.");
            });

            foreach (float amount in new[] { .25f, .5f, 1f })
                check($"Lingering harmonies cannot suppress the next speech onset: {rate} Hz, {amount}", () =>
                {
                    var (process, gain) = HarmonizerRegression.NewLevel(rate, amount);
                    double nominal = gain();
                    double harmony = amount * (1 / Math.Sqrt(3)), direct = 1d - amount + harmony;
                    Require(Math.Abs(nominal * nominal * (direct * direct + 2 * harmony * harmony) - 1) < 1e-12,
                        "Prepared nominal voice power is incorrect.");
                    int quietStart = rate / 5, onset = quietStart + rate / 10;
                    for (int i = 0; i < rate; i++)
                    {
                        bool quiet = i >= quietStart && i < onset;
                        float reference = (quiet ? .0001f : .2f) * (float)Math.Sin(2 * Math.PI * 180 * i / rate);
                        float mixed = quiet ? .02f * (float)Math.Sin(2 * Math.PI * 270 * i / rate) : reference * .7f;
                        float output = process(reference, mixed);
                        Require(gain() >= nominal - 1e-12 && float.IsFinite(output),
                            "A quiet reference learned attenuation below the voice-power baseline.");
                        if (i >= onset && i < onset + rate / 50)
                            Require(Math.Abs(output) + 1e-8 >= Math.Abs(mixed * nominal), "The next onset inherited a volume dip.");
                    }
                });
        }

        check("Quiet, DC and absent references cannot establish makeup gain", () =>
        {
            const int rate = 22050;
            var (process, gain) = HarmonizerRegression.NewLevel(rate);
            for (int i = 0; i < rate; i++) Require(process(0, 0) == 0, "Silence produced audio.");
            for (int i = 0; i < rate; i++) process(.001f, .001f * (float)Math.Exp(-i / (rate * .005)));
            for (int i = 0; i < rate; i++) process(0, .001f);
            for (int i = 0; i < rate; i++) process(i % 2 == 0 ? 1e-7f : -1e-7f, 1e-10f);
            Require(gain() == 1, "A DC, quiet or missing reference established new gain.");
            for (int i = 0; i < rate; i++) process(i % 2 == 0 ? .2f : -.2f, i % 2 == 0 ? .1f : -.1f);
            Require(Math.Abs(gain() - 2) < 1e-8, "Real audio could not resume correction after the lead-in.");
        });

        check("Complete cancellation remains silent and does not raise gain", () =>
        {
            var (process, gain) = HarmonizerRegression.NewLevel(22050);
            for (int i = 0; i < 22050; i++) Require(process(i % 2 == 0 ? .2f : -.2f, 0) == 0, "Gain fabricated audio from zero output.");
            Require(gain() == 1, "An unrecoverable cancellation established new gain.");
        });

        check("Inaudible noise cannot establish fast gain for the next speech onset", () =>
        {
            const int rate = 22050;
            var (process, gain) = HarmonizerRegression.NewLevel(rate);
            for (int i = 0; i < rate; i++)
            {
                float noise = 1e-5f * (float)Math.Sin(2 * Math.PI * 180 * i / rate);
                process(noise, noise * .02f);
            }
            Require(gain() <= 1.001, "A quiet noise floor established excessive gain.");
            for (int i = 0; i < rate / 5; i++)
            {
                float reference = .2f * (float)Math.Sin(2 * Math.PI * 180 * i / rate);
                process(reference, reference * .5f);
                Require(gain() <= 2 + 1e-8, "The next speech onset inherited noise-floor gain.");
            }
        });

        check("Correction releases stale boost after excitation ends and allocates no managed bytes", () =>
        {
            const int rate = 22050;
            var (process, gain) = HarmonizerRegression.NewLevel(rate);
            for (int i = 0; i < rate; i++) process(i % 2 == 0 ? .2f : -.2f, i % 2 == 0 ? .1f : -.1f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < rate; i++) process(i % 2 == 0 ? .2f : -.2f, i % 2 == 0 ? .01f : -.01f);
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "Active power tracking allocated.");
            double previous = gain();
            for (int i = 0; i < rate; i++)
            {
                process(0, .00001f);
                Require(gain() <= previous + 1e-12, "A decay without excitation established new gain.");
                previous = gain();
            }
            Require(gain() == 1, "A quiet tail retained stale cancellation boost for the next onset.");
        });

        HarmonizerSmoothLevelChecks.Run(check, Evidence);
    }

    private static float[] Tone(int rate, int length, double frequency) => Enumerable.Range(0, length)
        .Select(i => .2f * (float)Math.Sin(2 * Math.PI * frequency * i / rate)).ToArray();

    private static double Ratio(float[] output, float[] reference, int start, int length) =>
        10 * Math.Log10(PowerMatcher.Measure(output.AsSpan(start, length)) / PowerMatcher.Measure(reference.AsSpan(start, length)));

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
