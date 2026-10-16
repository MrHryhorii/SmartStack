using ONNX_Runner.Services;

internal static class CodecLoudnessChecks
{
    // Current G.711 wet processing is independent of amount. These fixed-path moments
    // remain valid across the mix and come from three original, unnormalized Piper recordings.
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

    // Frozen before the common level correction; only their dry/wet proportions are protected.
    private static readonly (float Dry, float Wet)[] OriginalGains =
    [
        (1f, 0f), (.9734474f, .08369329f), (.9441179f, .16454768f),
        (.9125038f, .24222498f), (.87909746f, .3163869f), (.84439135f, .38669533f),
        (.8088778f, .452812f), (.7730492f, .51439863f), (.7373978f, .5711171f),
        (.7024162f, .6226291f), (.6685965f, .6685965f), (.6282155f, .71871257f),
        (.5749671f, .779328f), (.5114702f, .84509075f), (.44034332f, .9106492f),
        (.36420518f, .97065127f), (.28567445f, 1.0197451f), (.2073699f, 1.0525788f),
        (.13191023f, 1.0638005f), (.06191402f, 1.0480582f), (0f, 1f),
    ];

    /// <summary>Checks the shared correction against independent base-voice moments at 21 points.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("G.711 level correction retains the original dry/wet ratio at all 21 points", () =>
        {
            Require(OriginalGains.Length == 21, "Missing original ratio references.");
            for (int i = 0; i <= 20; i++)
            {
                Dsp.CubicGains(i / 20f, out float dry, out float wet);
                var original = OriginalGains[i];
                Require(Math.Abs(dry * original.Wet - wet * original.Dry) < 3e-7,
                    "Loudness correction changed the accepted effect proportion.");
            }
        });

        foreach (VoiceMoment voice in Voices)
        {
            check($"G.711 {voice.Law} base-voice {voice.Name} decreases through its 21-point loudness window", () =>
            {
                double full = 10 * Math.Log10(voice.Wet / voice.Dry);
                double previous = 0;
                Require(full < 0, "The reference no longer represents the accepted telephone bandwidth.");
                for (int i = 0; i <= 20; i++)
                {
                    double level = Level(voice, i / 20f);
                    Require(level >= full - .01 && level <= .01,
                        $"At {i / 20f:R}, level left the clean/full window.");
                    Require(level <= previous + .01,
                        $"At {i / 20f:R}, level reversed its decreasing direction.");
                    if (i == 10)
                        Require(level > full && level < 0, "Midpoint is outside the endpoint window.");
                    previous = level;
                }
            });
        }

        check("Shared G.711 midpoint follows the base-voice mean dB guide", () =>
        {
            double midpoint = 0, target = 0;
            foreach (VoiceMoment voice in Voices)
            {
                midpoint += Level(voice, .5f);
                target += 5 * Math.Log10(voice.Wet / voice.Dry);
            }
            Require(Math.Abs((midpoint - target) / Voices.Length) < .01,
                "The mean midpoint moved away from the agreed center of the endpoint window.");
        });

        check("Both G.711 laws retain close base-voice loudness with their shared mixer", () =>
        {
            for (int recording = 0; recording < Voices.Length; recording += 2)
                for (int i = 0; i <= 20; i++)
                    Require(Math.Abs(Level(Voices[recording], i / 20f) - Level(Voices[recording + 1], i / 20f)) < .01,
                        "The shared mixer introduced an unexpected level difference between the laws.");
        });
    }

    /// <summary>Evaluates exact dry/wet power and correlation using current prepared gains.</summary>
    private static double Level(VoiceMoment voice, float amount)
    {
        Dsp.CubicGains(amount, out float dry, out float wet);
        double power = dry * (double)dry * voice.Dry + wet * (double)wet * voice.Wet
            + 2 * dry * (double)wet * voice.Cross;
        return 10 * Math.Log10(power / voice.Dry);
    }

    /// <summary>Reports a failed calibrated-mixer contract.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
