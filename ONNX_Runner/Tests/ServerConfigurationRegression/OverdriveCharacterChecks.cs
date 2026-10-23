using ONNX_Runner.Models;

internal static class OverdriveCharacterChecks
{
    /// <summary>Protects the calibrated reference distortion and its growth at the 21 declared strengths.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Overdrive reaches ten percent THD on the declared reference tone", () =>
        {
            float[] tone = Tone(22050, .18f);
            OverdriveMixingChecks.NewEngine(22050).ApplyEffect(tone, VoiceEffectType.Overdrive, 1f);
            // Independent sine/cosine projections; the offline drive calibration used Goertzel bins.
            Require(Math.Abs(ProjectedThd(tone, 22050) - .10) < 2e-6,
                "The 250 Hz, peak .18 reference no longer reaches its full-strength target.");
        });
        check("Overdrive midpoint retains lighter saturation than its strengthened full output", () =>
        {
            float[] middle = Tone(22050, .18f), full = (float[])middle.Clone();
            OverdriveMixingChecks.NewEngine(22050).ApplyEffect(middle, VoiceEffectType.Overdrive, .5f);
            OverdriveMixingChecks.NewEngine(22050).ApplyEffect(full, VoiceEffectType.Overdrive, 1f);
            double midpoint = ProjectedThd(middle, 22050);
            Require(midpoint is > .04 and < .10 && midpoint < ProjectedThd(full, 22050),
                "The reference midpoint lost its milder harmonic character.");
        });
        foreach (int rate in new[] { 8000, 22050, 48000 })
        foreach (float peak in new[] { .05f, .18f, .5f })
        {
            check($"Overdrive harmonic strength grows at 21 positions: {rate} Hz, peak {peak:R}", () =>
            {
                float[] clean = Tone(rate, peak);
                double previous = 0;
                for (int index = 0; index <= 20; index++)
                {
                    float[] output = (float[])clean.Clone();
                    OverdriveMixingChecks.NewEngine(rate).ApplyEffect(output, VoiceEffectType.Overdrive, index / 20f);
                    double current = GoertzelThd(output, rate);
                    Require(double.IsFinite(current) && current >= previous - 1e-7,
                        "Reference-tone distortion reversed as intensity increased.");
                    previous = current;
                }
            });
        }
    }

    /// <summary>Creates the coherent control used for offline drive calibration without normalizing its peak.</summary>
    private static float[] Tone(int rate, float peak)
    {
        var result = new float[rate * 3];
        for (int i = 0; i < result.Length; i++)
            result[i] = peak * (float)Math.Sin(2 * Math.PI * 250 * i / rate);
        return result;
    }

    /// <summary>Measures harmonics 2-9 relative to the fundamental after two seconds of warmup.</summary>
    private static double ProjectedThd(float[] input, int rate)
    {
        double fundamental = 0, harmonics = 0;
        for (int harmonic = 1; harmonic <= 9; harmonic++)
        {
            double real = 0, imaginary = 0;
            for (int i = 0; i < rate; i++)
            {
                double angle = 2 * Math.PI * 250 * harmonic * i / rate;
                real += input[2 * rate + i] * Math.Cos(angle);
                imaginary += input[2 * rate + i] * Math.Sin(angle);
            }
            double power = real * real + imaginary * imaginary;
            if (harmonic == 1) fundamental = power; else harmonics += power;
        }
        return Math.Sqrt(harmonics / fundamental);
    }

    /// <summary>Checks harmonic progression using an independent recurrence at the same nine frequency bins.</summary>
    private static double GoertzelThd(float[] input, int rate)
    {
        double fundamental = 0, harmonics = 0;
        for (int harmonic = 1; harmonic <= 9; harmonic++)
        {
            double coefficient = 2 * Math.Cos(2 * Math.PI * 250 * harmonic / rate), first = 0, second = 0;
            foreach (float sample in input.AsSpan(2 * rate))
            {
                double next = sample + coefficient * first - second;
                second = first; first = next;
            }
            double power = Math.Max(0, first * first + second * second - coefficient * first * second);
            if (harmonic == 1) fundamental = power; else harmonics += power;
        }
        return Math.Sqrt(harmonics / fundamental);
    }

    /// <summary>Reports a violated reference-tone contract without interpreting THD as perceived quality.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
