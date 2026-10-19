using System.Runtime.InteropServices;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class RingMixingChecks
{
    private static readonly Corpus Fixtures = Load();

    /// <summary>Checks the actual eased wet path, prepared level curve and unchanged effect proportions.</summary>
    internal static void Run(Action<string, Action> check)
    {
        VoiceRecord[] voices = Fixtures.Voices;
        VoiceRecord[] basis = voices.Where(v => v.Voice == "piper_base").ToArray();
        Require(voices.Length == 48 && basis.Length == 3 && Fixtures.Synthetic.Length == 9,
            "The independent current-path fixtures are incomplete.");

        check("RingModulator preserves exact endpoint and invalid-input gains", () =>
        {
            foreach (float amount in new[] { float.NegativeInfinity, -1f, 0f, .001f, float.NaN, 1f, 2f, float.PositiveInfinity })
            {
                Dsp.RingGains(amount, out float dry, out float wet);
                bool full = amount >= 1f;
                Require(dry == (full ? 0f : 1f) && wet == (full ? 1f : 0f), "An endpoint gain changed.");
                Require(Dsp.RingCrossfade(.25f, -.75f, dry, wet) == (full ? -.75f : .25f), "Endpoint selection changed.");
            }
        });
        check("RingModulator common correction is finite, positive and preserves increasing wet share", () =>
        {
            double previousShare = 0;
            for (int i = 0; i <= 100000; i++)
            {
                float amount = i / 100000f;
                Dsp.RingGains(amount, out float dry, out float wet);
                double correction = Math.Sqrt(dry * (double)dry + wet * (double)wet);
                Require(float.IsFinite(dry) && float.IsFinite(wet) && dry >= 0 && wet >= 0,
                    "A prepared gain is invalid.");
                Require(correction > .85 && correction < 1.1, "The common correction left its bounded range.");
                double share = wet / (dry + (double)wet);
                Require(share >= previousShare - 1e-7, "The wet share moved backwards.");
                previousShare = share;
                if (amount <= .001f || amount >= 1f) continue;
                Dsp.EqualPowerGains(amount, out float a, out float b);
                Require(Math.Abs(dry * b - wet * a) < 3e-7, "The correction changed the original dry/wet ratio.");
            }
        });
        check("RingModulator root-coordinate halves share midpoint values and physical slopes", () =>
        {
            const float h = .001f;
            double middle = Correction(.5f);
            double left = (3 * middle - 4 * Correction(.5f - h) + Correction(.5f - 2 * h)) / (2 * h);
            double right = (-3 * middle + 4 * Correction(.5f + h) - Correction(.5f + 2 * h)) / (2 * h);
            Require(Math.Abs(left - right) < .003, "The midpoint slope is discontinuous.");
            Require(Math.Abs(Correction(MathF.BitDecrement(.5f)) - Correction(MathF.BitIncrement(.5f))) < 5e-7,
                "The midpoint value is discontinuous.");
        });
        check("RingModulator prepared gain and mixing allocate zero managed bytes", () =>
        {
            double checksum = 0;
            for (int i = 0; i < 20000; i++) checksum += PreparedSample(i);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++) checksum += PreparedSample(i);
            Require(GC.GetAllocatedBytesForCurrentThread() == before && double.IsFinite(checksum),
                "Prepared mixing allocated memory.");
        });

        foreach (VoiceRecord voice in voices)
        {
            Require(voice.Points.Length == 21, "A recorded current path is incomplete.");
            check($"RingModulator current-path guide band holds for {voice.Name}", () =>
            {
                foreach (Point point in voice.Points)
                    Require(Math.Abs(Level(voice, point) - point.Amount * voice.FullChangeDb) < 1,
                        "A current-path level exceeded the declared 1 dB guide band.");
            });
            check($"RingModulator current-path reverse-step band holds for {voice.Name}", () =>
            {
                double previous = 0;
                foreach (Point point in voice.Points)
                {
                    double current = Level(voice, point);
                    Require(Math.Sign(voice.FullChangeDb) * (previous - current) < .51,
                        "An individual current-path reverse step exceeded its reported budget.");
                    previous = current;
                }
            });
            check($"RingModulator current-path endpoint escape band holds for {voice.Name}", () =>
            {
                foreach (Point point in voice.Points)
                {
                    double level = Level(voice, point);
                    Require(level >= Math.Min(0, voice.FullChangeDb) - .70 && level <= Math.Max(0, voice.FullChangeDb) + .70,
                        "An individual current-path endpoint escape exceeded its reported budget.");
                }
            });
        }
        foreach (VoiceRecord voice in basis)
        {
            check($"RingModulator base path stays ordered inside its clean/full window for {voice.Name}", () =>
            {
                double previous = 0;
                foreach (Point point in voice.Points)
                {
                    double current = Level(voice, point);
                    Require(current >= previous - .0001 && current >= -.0001 && current <= voice.FullChangeDb + .0001,
                        "A base-voice path reversed or left its clean/full window.");
                    previous = current;
                }
            });
        }
        check("RingModulator base mean follows an ordered transition with a centered midpoint", () =>
        {
            double full = basis.Average(v => v.FullChangeDb), previous = 0, error = 0, baselineError = 0;
            for (int i = 0; i <= 20; i++)
            {
                double mean = basis.Average(v => Level(v, v.Points[i]));
                Require(mean >= previous - .0001, "The base mean reversed at a captured point.");
                error += Math.Abs(mean - i / 20d * full) / 21;
                baselineError += Math.Abs(basis.Average(v => v.Points[i].BaselineChangeDb) - i / 20d * full) / 21;
                previous = mean;
            }
            Require(error < .04 && error < baselineError * .1, "The base-mean guide improvement regressed.");
            Require(Math.Abs(basis.Average(v => Level(v, v.Points[10])) - full / 2) < 1e-5,
                "The base mean midpoint moved from the half-dB guide.");
        });
        check("RingModulator catalog controls retain the declared midpoint and average-error budgets", () =>
        {
            int windows = 0; double error = 0, baselineError = 0, meanError = 0;
            foreach (VoiceRecord voice in voices)
            {
                double midpoint = Level(voice, voice.Points[10]);
                if (midpoint >= Math.Min(0, voice.FullChangeDb) && midpoint <= Math.Max(0, voice.FullChangeDb)) windows++;
                foreach (Point point in voice.Points)
                {
                    error += Math.Abs(Level(voice, point) - point.Amount * voice.FullChangeDb) / (48 * 21);
                    baselineError += Math.Abs(point.BaselineChangeDb - point.Amount * voice.FullChangeDb) / (48 * 21);
                }
            }
            for (int i = 0; i <= 20; i++)
                meanError += Math.Abs(voices.Average(v => Level(v, v.Points[i]))
                    - voices.Average(v => v.FullChangeDb) * i / 20d) / 21;
            Require(windows >= 25, "Catalog midpoint coverage fell below the declared 25/48.");
            Require(error < .19 && error < baselineError * .6 && meanError < .18,
                "Catalog average-error improvement regressed.");
        });

        foreach (SyntheticRecord record in Fixtures.Synthetic)
        foreach (SyntheticPoint point in record.Points)
        {
            check($"RingModulator independent wet routing at {record.Rate} Hz, scale {record.Scale:R}, amount {point.Amount:R}", () =>
            {
                float[] clean = Signal(record.Rate, record.Rate * 3, record.Scale), actual = (float[])clean.Clone();
                NewEngine(record.Rate).ApplyEffect(actual, VoiceEffectType.RingModulator, point.Amount);
                double power = 0;
                foreach (float sample in actual)
                {
                    Require(float.IsFinite(sample), "Non-finite effect output.");
                    power += sample * (double)sample;
                }
                Dsp.RingGains(point.Amount, out float dryGain, out float wetGain); wetGain *= .91f;
                double expectedPower = dryGain * (double)dryGain * record.DryPower
                    + wetGain * (double)wetGain * point.WetPower + 2 * dryGain * (double)wetGain * point.Cross;
                Require(Math.Abs(10 * Math.Log10(power / actual.Length / expectedPower)) < .0001,
                    "Output power differs from independently captured post-DC wet moments.");
                Require(point.WetIndices.Length == 16 && point.WetSamples.Length == 16, "Wet checkpoints are incomplete.");
                for (int i = 0; i < point.WetIndices.Length; i++)
                {
                    int index = point.WetIndices[i];
                    float expected = clean[index] * dryGain + point.WetSamples[i] * wetGain;
                    Require(Math.Abs(actual[index] - expected) < 3e-7, "Correction changed a frozen wet waveform checkpoint.");
                }
            });
        }
        foreach (int rate in new[] { 8000, 22050, 48000 })
        {
            check($"RingModulator changing intensity is independent of streaming partitions at {rate} Hz", () =>
            {
                float[] amounts = [0, .001f, .00101f, .05f, .25f, .4995f, .5f, .5005f, .85f, 1, .45f];
                int size = rate / 8;
                float[] whole = Signal(rate, size * amounts.Length, 1), split = (float[])whole.Clone();
                var a = NewEngine(rate); var b = NewEngine(rate);
                for (int section = 0; section < amounts.Length; section++)
                {
                    a.ApplyEffect(whole.AsSpan(section * size, size), VoiceEffectType.RingModulator, amounts[section]);
                    for (int i = 0; i < size; i += 257)
                        b.ApplyEffect(split.AsSpan(section * size + i, Math.Min(257, size - i)),
                            VoiceEffectType.RingModulator, amounts[section]);
                }
                Require(MemoryMarshal.AsBytes(whole.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(split.AsSpan())),
                    "Streaming partitions changed output.");
            });
            check($"RingModulator warm bypass remains exact at {rate} Hz", () =>
            {
                float[] clean = Signal(rate, rate / 4, 1); var engine = NewEngine(rate);
                engine.ApplyEffect((float[])clean.Clone(), VoiceEffectType.RingModulator, .7f);
                foreach (float amount in new[] { 0f, .001f })
                {
                    float[] output = (float[])clean.Clone(); engine.ApplyEffect(output, VoiceEffectType.RingModulator, amount);
                    Require(output.SequenceEqual(clean), "Warm bypass changed the original samples.");
                }
            });
        }
    }

    /// <summary>Evaluates current prepared gains against the frozen independent wet moments.</summary>
    private static double Level(VoiceRecord voice, Point point)
    {
        Dsp.RingGains(point.Amount, out float dry, out float wet); wet *= .91f;
        double power = dry * (double)dry * voice.DryPower + wet * (double)wet * point.WetPower
            + 2 * dry * (double)wet * point.Cross;
        return 10 * Math.Log10(power / voice.DryPower);
    }

    /// <summary>Recovers the common scalar independently of its coordinate representation.</summary>
    private static double Correction(float amount)
    {
        Dsp.RingGains(amount, out float dry, out float wet);
        return Math.Sqrt(dry * (double)dry + wet * (double)wet);
    }

    /// <summary>Exercises both prepared gains and the unchanged per-sample combination.</summary>
    private static float PreparedSample(int index)
    {
        Dsp.RingGains(index % 1001 / 1000f, out float dry, out float wet);
        return Dsp.RingCrossfade(.2f, -.1f, dry, wet);
    }

    /// <summary>Recreates the exact multi-band routing input without audio assets.</summary>
    private static float[] Signal(int rate, int count, float scale)
    {
        var signal = new float[count];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / rate;
            signal[i] = scale * (float)((.18 * Math.Sin(2 * Math.PI * 110 * t) + .08 * Math.Sin(2 * Math.PI * 330 * t)
                + .05 * Math.Sin(2 * Math.PI * 1700 * t) + .03 * Math.Sin(2 * Math.PI * Math.Min(3200, rate * .3) * t))
                * (.7 + .3 * Math.Sin(2 * Math.PI * .73 * t)));
        }
        return signal;
    }

    /// <summary>Creates isolated per-request effect state.</summary>
    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456); return engine;
    }

    /// <summary>Loads numeric moments only in the standalone regression runner.</summary>
    private static Corpus Load()
    {
        using Stream stream = typeof(RingMixingChecks).Assembly.GetManifestResourceStream("RingCurveMoments.json")
            ?? throw new InvalidDataException("Missing current-path RingModulator moments.");
        return JsonSerializer.Deserialize<Corpus>(stream) ?? throw new InvalidDataException("Invalid RingModulator moments.");
    }

    /// <summary>Reports an explicit signal contract failure.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record Corpus(VoiceRecord[] Voices, SyntheticRecord[] Synthetic);
    private sealed record VoiceRecord(string Name, string Voice, int Rate, double DryPower, double FullChangeDb, Point[] Points);
    private sealed record Point(float Amount, double WetPower, double Cross, double BaselineChangeDb);
    private sealed record SyntheticRecord(string Name, int Rate, float Scale, double DryPower, SyntheticPoint[] Points);
    private sealed record SyntheticPoint(float Amount, double WetPower, double Cross, int[] WetIndices, float[] WetSamples);
}
