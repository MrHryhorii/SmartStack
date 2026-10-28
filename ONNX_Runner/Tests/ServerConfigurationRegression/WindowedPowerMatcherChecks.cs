using ONNX_Runner.Services;

internal static class WindowedPowerMatcherChecks
{
    internal static void Run(Action<string, Action> check)
    {
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000 })
        {
            check($"Windowed power matching restores steady attenuation at {rate} Hz", () =>
            {
                var matcher = new WindowedPowerMatcher(rate);
                double target = 0, actual = 0;
                for (int i = 0; i < rate * 2; i++)
                {
                    float reference = .2f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate);
                    float output = matcher.Process(reference, reference * .5f);
                    if (i < rate) continue;
                    target += reference * (double)reference; actual += output * (double)output;
                }
                Require(Math.Abs(10 * Math.Log10(actual / target)) < .001, "The settled power ratio is incorrect.");
                Require(Math.Abs(matcher.Gain - 2) < 1e-8, "The gain did not settle to the measured inverse attenuation.");
            });
            check($"Windowed power matching bounds gain without clipping at {rate} Hz", () =>
            {
                var matcher = new WindowedPowerMatcher(rate);
                for (int i = 0; i < rate * 2; i++) matcher.Process(i % 2 == 0 ? .2f : -.2f, i % 2 == 0 ? .0002f : -.0002f);
                Require(matcher.Gain == 4, "An unattainable power target exceeded its gain bound.");
                matcher.Reset(2);
                Require(matcher.Process(.75f, .75f) == 1.5f, "The matcher clipped floating-point headroom or delayed the signal.");
            });
        }
        check("Windowed power matching retains digital silence and freezes analysis below its reference floor", () =>
        {
            var matcher = new WindowedPowerMatcher(22050, 1.25);
            Require(!matcher.HasSamples, "A new matcher advanced its audio clock.");
            for (int i = 0; i < 22050; i++) Require(matcher.Process(0, 0) == 0, "Silence gained artificial sound.");
            for (int i = 0; i < 22050; i++) matcher.Process(0, .001f);
            Require(matcher.Gain == 1.25 && matcher.HasSamples, "A missing reference was treated as recoverable voice.");
        });
        check("Windowed power matching reset clears analysis and reproduces the same output", () =>
        {
            var matcher = new WindowedPowerMatcher(16000, .8);
            float[] expected = new float[32000];
            for (int i = 0; i < expected.Length; i++)
            {
                float source = .2f * (float)Math.Sin(i * .13);
                expected[i] = matcher.Process(source, source * .4f);
            }
            matcher.Reset(.8);
            Require(!matcher.HasSamples && matcher.Gain == .8, "Reset retained gain or energy history.");
            for (int i = 0; i < expected.Length; i++)
            {
                float source = .2f * (float)Math.Sin(i * .13);
                Require(matcher.Process(source, source * .4f) == expected[i], "Reset did not reproduce PCM.");
            }
        });
        check("Windowed power matching does not learn excessive gain from a DC lead-in", () =>
        {
            const int rate = 22050;
            var matcher = new WindowedPowerMatcher(rate, 1.25);
            for (int i = 0; i < rate; i++) matcher.Process(.001f, .001f * (float)Math.Exp(-i / (rate * .005)));
            Require(matcher.Gain == 1.25, "A removed DC offset was treated as voice energy to recover.");
            for (int i = 0; i < rate * 2; i++) matcher.Process(i % 2 == 0 ? .2f : -.2f, i % 2 == 0 ? .1f : -.1f);
            Require(Math.Abs(matcher.Gain - 2) < 1e-8, "Valid audio could not resume adaptation after a DC lead-in.");
        });
        check("Windowed power matching active analysis allocates no managed bytes", () =>
        {
            var matcher = new WindowedPowerMatcher(22050);
            for (int i = 0; i < 22050; i++) matcher.Process(i % 2 == 0 ? .2f : -.2f, i % 2 == 0 ? .1f : -.1f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 44100; i++) matcher.Process(i % 2 == 0 ? .2f : -.2f, i % 2 == 0 ? .1f : -.1f);
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "Window updates or gain transitions allocated.");
        });
        check("Windowed power matching limits gain rise in audio time", () =>
        {
            const int rate = 48000;
            var matcher = new WindowedPowerMatcher(rate);
            for (int i = 0; i < rate * 2; i++)
            {
                double previous = matcher.Gain;
                matcher.Process(i % 2 == 0 ? .2f : -.2f, i % 2 == 0 ? .0002f : -.0002f);
                double rise = 20 * Math.Log10(matcher.Gain / previous) * rate;
                Require(rise <= 12.00001, "Gain rose too quickly after a low-energy analysis window.");
            }
            Require(matcher.Gain == 4, "The rise limit prevented a steady bounded target from being reached.");
        });
        check("Windowed power matching rejects invalid configuration and reset gains", () =>
        {
            Action[] invalid = [() => new WindowedPowerMatcher(0),
                () => new WindowedPowerMatcher(22050, double.NaN),
                () => new WindowedPowerMatcher(22050, windowMs: 0),
                () => new WindowedPowerMatcher(22050, updateMs: 500),
                () => new WindowedPowerMatcher(22050, warmupMs: 500),
                () => new WindowedPowerMatcher(22050, transitionMs: float.PositiveInfinity),
                () => new WindowedPowerMatcher(22050, maximumGain: .5),
                () => new WindowedPowerMatcher(22050, minimumPower: 0),
                () => new WindowedPowerMatcher(22050, maximumRiseDbPerSecond: double.NaN)];
            foreach (Action action in invalid) Throws(action);
            var matcher = new WindowedPowerMatcher(22050);
            foreach (double gain in new[] { -1, double.NaN, double.PositiveInfinity, 5 }) Throws(() => matcher.Reset(gain));
        });
    }

    private static void Throws(Action action)
    {
        try { action(); } catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Invalid configuration reached processing.");
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
