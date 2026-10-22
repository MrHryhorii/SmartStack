using ONNX_Runner.Services;

internal static class LufsMeterChecks
{
    internal static void Run(Action<string, Action> check)
    {
        check("LUFS meter rejects silence and invalid PCM without producing gain", () =>
        {
            Require(double.IsNegativeInfinity(LufsMeter.MeasureIntegratedLoudness([], 22050)), "Empty PCM has loudness.");
            Require(double.IsNegativeInfinity(LufsMeter.MeasureIntegratedLoudness(new float[22050], 22050)), "Silence has loudness.");
            Require(double.IsNegativeInfinity(LufsMeter.MeasureIntegratedLoudness([float.NaN], 22050)), "Invalid PCM has loudness.");
            Require(LufsMeter.GainForTarget(double.NaN, -23) == 1 &&
                LufsMeter.GainForTarget(-23, double.PositiveInfinity) == 1, "Invalid levels produced gain.");
        });
        // Frozen results from an independent explicit-window BS.1770 calculation.
        foreach (var (rate, frequency, expected) in new (int, int, double)[]
        {
            (8000, 40, -29.055904277368),
            (8000, 100, -24.6201461463161),
            (8000, 1000, -22.9907118798914),
            (8000, 3000, -19.4716515587597),
            (11025, 40, -29.1263555161964),
            (11025, 100, -24.6910206819425),
            (11025, 1000, -22.9631488091315),
            (11025, 3000, -19.620921092894),
            (16000, 40, -29.1844916695958),
            (16000, 100, -24.7494042961069),
            (16000, 1000, -22.9636058675781),
            (16000, 3000, -19.7460986252498),
            (22050, 40, -29.2199315907388),
            (22050, 100, -24.7849506931442),
            (22050, 1000, -22.9743925905615),
            (22050, 3000, -19.8135440694635),
            (24000, 40, -29.2275558658207),
            (24000, 100, -24.7925935621065),
            (24000, 1000, -22.9777485960069),
            (24000, 3000, -19.826873207862),
            (44100, 40, -29.2669009175105),
            (44100, 100, -24.8320105284069),
            (44100, 1000, -23.0008491538139),
            (44100, 3000, -19.8883212358905),
            (48000, 40, -29.2707225843832),
            (48000, 100, -24.8358370570528),
            (48000, 1000, -23.0036066432634),
            (48000, 3000, -19.8936114991655),
        })
            check($"LUFS K weighting matches the independent {frequency} Hz reference at {rate} Hz", () =>
                Require(Math.Abs(LufsMeter.MeasureIntegratedLoudness(Tone(rate, frequency, 3), rate) - expected) < .00001,
                    "K weighting differs from the independent reference."));
        foreach (var (rate, expected) in new (int, double)[]
        {
            (8000, -24.1300394544151),
            (11025, -24.1016807191694),
            (16000, -24.1030453046203),
            (22050, -24.1138498542458),
            (24000, -24.117208904844),
            (44100, -24.140320707572),
            (48000, -24.1430788902005),
        })
            check($"LUFS gates preserve the independent speech-and-pause reference at {rate} Hz", () =>
            {
                var samples = new float[5 * rate];
                for (int i = rate; i < 2 * rate; i++) samples[i] = .1f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate);
                for (int i = 3 * rate; i < 4 * rate; i++) samples[i] = .001f * (float)Math.Sin(2 * Math.PI * 500 * i / rate);
                Require(Math.Abs(LufsMeter.MeasureIntegratedLoudness(samples, rate) - expected) < .00001,
                    "Absolute or relative gating differs from the independent reference.");
            });
        check("LUFS reference gain reaches the requested voice normalization target", () =>
        {
            const int rate = 22050;
            float[] samples = Tone(rate, 1000, 3);
            double measured = LufsMeter.MeasureIntegratedLoudness(samples, rate);
            float gain = LufsMeter.GainForTarget(measured, -23);
            for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
            Require(Math.Abs(LufsMeter.MeasureIntegratedLoudness(samples, rate) + 23) < .00001,
                "Reference normalization missed its target.");
        });
        check("Short reference clips use the documented single-block extension", () =>
        {
            const int rate = 22050;
            float[] samples = Tone(rate, 1000, 1)[..2205];
            double measured = LufsMeter.MeasureIntegratedLoudness(samples, rate);
            Require(double.IsFinite(measured) && Math.Abs(measured + 23.01) < .05,
                "A short usable reference became silent or lost its expected level.");
        });
    }

    private static float[] Tone(int rate, int frequency, int seconds) => Enumerable.Range(0, seconds * rate)
        .Select(i => .1f * (float)Math.Sin(2 * Math.PI * frequency * i / rate)).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
