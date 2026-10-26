using ONNX_Runner.Services;

internal static class CodecLoudnessChecks
{
    // Independent powers and correlations from the original unnormalized Piper recordings.
    private readonly record struct VoiceMoment(string Name, string Law, double Dry, double Wet, double Cross);

    private static readonly VoiceMoment[] Voices =
    [
        new("train_a", "MuLaw", .0021159343355676022, .0011779314087227493, .00045224514530719004),
        new("train_a", "ALaw", .0021159343355676022, .0011773979153901744, .00045222019509055203),
        new("train_b", "MuLaw", .0016530731728661844, .0009036044114557643, .0003415508783773922),
        new("train_b", "ALaw", .0016530731728661844, .0009034057627945092, .0003415621139486716),
        new("validation", "MuLaw", .0021363241410659777, .0012466290550197598, .000625699584581475),
        new("validation", "ALaw", .0021363241410659777, .0012467401684481093, .000625748769280834),
    ];

    internal static void Run(Action<string, Action> check)
    {
        foreach (VoiceMoment voice in Voices)
            check($"G.711 {voice.Law} {voice.Name} follows its measured endpoint-power window", () =>
            {
                float[] dry = [(float)Math.Sqrt(2 * voice.Dry), 0];
                float[] full = [(float)(Math.Sqrt(2) * voice.Cross / Math.Sqrt(voice.Dry)),
                    (float)Math.Sqrt(2 * (voice.Wet - voice.Cross * voice.Cross / voice.Dry))];
                double previous = double.PositiveInfinity;
                for (int step = 0; step <= 100; step++)
                {
                    float amount = step / 100f;
                    float[] actual = (float[])full.Clone();
                    Require(PowerMatchedMixer.Mix(dry, actual, amount).Matched, "The fixed codec mix cancelled.");
                    double level = 10 * Math.Log10(PowerMatcher.Measure(actual) / voice.Dry);
                    double expected = 10 * Math.Log10(((1 - (double)amount) * voice.Dry + amount * voice.Wet) / voice.Dry);
                    Require(Math.Abs(level - expected) < 1e-6, "Input correlation changed the target power.");
                    Require(level <= previous + 1e-6, "Pre-limiter codec power reversed its endpoint direction.");
                    previous = level;
                }
            });
        check("G.711 midpoint interpolates endpoint power without a fitted compensation curve", () =>
        {
            foreach (VoiceMoment voice in Voices)
            {
                double target = PowerMatcher.Target(voice.Dry, voice.Wet, .5f);
                Require(target == (voice.Dry + voice.Wet) / 2, "The midpoint used a fitted gain or dB target.");
            }
        });
        check("Both G.711 laws retain close endpoint-power trajectories", () =>
        {
            for (int recording = 0; recording < Voices.Length; recording += 2)
            for (int step = 0; step <= 100; step++)
            {
                VoiceMoment a = Voices[recording], b = Voices[recording + 1];
                double amount = step / 100.0;
                double pa = (1 - amount) * a.Dry + amount * a.Wet;
                double pb = (1 - amount) * b.Dry + amount * b.Wet;
                Require(Math.Abs(10 * Math.Log10(pa / pb)) < .01,
                    "The two codec laws developed an unexpected power difference.");
            }
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
