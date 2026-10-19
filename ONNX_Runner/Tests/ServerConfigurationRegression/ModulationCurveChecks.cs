using System.Runtime.InteropServices;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class ModulationCurveChecks
{
    private static readonly Corpus Fixtures = Load();

    /// <summary>Checks current-path moments, independent waveform checkpoints and bounded calibration tradeoffs.</summary>
    internal static void Run(Action<string, Action> check, VoiceEffectType effect)
    {
        bool chorus = effect == VoiceEffectType.Chorus;
        string name = effect.ToString();
        VoiceRecord[] voices = Fixtures.Voices.Where(v => v.Effect == name).ToArray();
        SyntheticRecord[] synthetic = Fixtures.Synthetic.Where(v => v.Effect == name).ToArray();
        VoiceRecord[] basis = voices.Where(v => v.Voice == "piper_base").ToArray();
        Require(voices.Length == 48 && basis.Length == 3 && synthetic.Length == 9, "Current-path fixtures are incomplete.");
        double guideBudget = chorus ? 1.16 : 1.40;
        double reverseBudget = chorus ? .47 : .415;
        double escapeBudget = chorus ? .365 : .76;

        check($"{name} gain preserves exact endpoint and invalid-input guards", () =>
        {
            foreach (float amount in new[] { -1f, 0f, .001f, float.NaN, 1f, 2f, float.PositiveInfinity })
                Require(Gain(chorus, amount) == 1f, "An endpoint or invalid-input gain changed.");
            Require(Mix(chorus, .25f, -.75f, 0f, 1f) == .25f, "The internal clean endpoint changed.");
        });
        check($"{name} prepared gain remains finite, positive and bounded", () =>
        {
            for (int i = 0; i <= 100000; i++)
            {
                float gain = Gain(chorus, i / 100000f);
                Require(float.IsFinite(gain) && gain > .9f && gain < 1.1f, "Prepared gain left its calibrated bounded range.");
            }
        });
        check($"{name} halves share midpoint values and slopes", () =>
        {
            const float h = .001f;
            double middle = Gain(chorus, .5f);
            double left = (3 * middle - 4 * Gain(chorus, .5f - h) + Gain(chorus, .5f - 2 * h)) / (2 * h);
            double right = (-3 * middle + 4 * Gain(chorus, .5f + h) - Gain(chorus, .5f + 2 * h)) / (2 * h);
            Require(Math.Abs(left - right) < .008, "The midpoint derivative has a discontinuity.");
            Require(Math.Abs(Gain(chorus, MathF.BitDecrement(.5f)) - Gain(chorus, MathF.BitIncrement(.5f))) < 3e-6,
                "The midpoint value is discontinuous.");
        });
        check($"{name} correction scales the existing mixture without changing its proportions", () =>
        {
            foreach (float dry in new[] { -.8f, -.2f, 0f, .15f, .9f })
            foreach (float wet in new[] { -.7f, 0f, .1f, .8f })
            for (int i = 0; i <= 20; i++)
            {
                float amount = i / 20f, gain = Gain(chorus, amount);
                float uncorrected = Mix(chorus, dry, wet, amount, 1f);
                Require(Mix(chorus, dry, wet, amount, gain) == uncorrected * gain,
                    "Level correction changed the additive mixture.");
            }
        });
        check($"{name} gain preparation and mixing allocate zero managed bytes", () =>
        {
            double checksum = 0;
            for (int i = 0; i < 20000; i++) checksum += PreparedSample(chorus, i);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++) checksum += PreparedSample(chorus, i);
            Require(GC.GetAllocatedBytesForCurrentThread() == before && double.IsFinite(checksum), "Prepared mixing allocated memory.");
        });

        foreach (VoiceRecord voice in voices)
        {
            Require(voice.Points.Length == 21, "A recorded current path is incomplete.");
            double full = voice.Points[^1].BaselineChangeDb;
            check($"{name} current-path guide budget holds for {voice.Name}", () =>
            {
                foreach (Point point in voice.Points)
                    Require(Math.Abs(Level(chorus, point) - point.Amount * full) < guideBudget,
                        "A current guide-error budget regressed.");
            });
            check($"{name} current-path reversal budget holds for {voice.Name}", () =>
            {
                double previous = 0;
                foreach (Point point in voice.Points)
                {
                    double current = Level(chorus, point);
                    Require(Math.Sign(full) * (previous - current) < reverseBudget, "A current reverse-step budget regressed.");
                    previous = current;
                }
            });
            check($"{name} current-path endpoint-window budget holds for {voice.Name}", () =>
            {
                foreach (Point point in voice.Points)
                {
                    double level = Level(chorus, point);
                    Require(level >= Math.Min(0, full) - escapeBudget && level <= Math.Max(0, full) + escapeBudget,
                        "A current endpoint-window budget regressed.");
                }
            });
            if (!chorus) continue;
            check($"Chorus current midpoint stays inside its endpoint window for {voice.Name}", () =>
            {
                double midpoint = Level(true, voice.Points[10]);
                Require(midpoint >= Math.Min(0, full) && midpoint <= Math.Max(0, full), "A Chorus midpoint left its endpoint window.");
            });
        }
        check($"{name} base mean follows the bounded ordered transition", () =>
        {
            double full = basis.Average(v => v.Points[^1].BaselineChangeDb), previous = 0;
            double error = 0, baselineError = 0;
            for (int i = 0; i <= 20; i++)
            {
                double mean = basis.Average(v => Level(chorus, v.Points[i]));
                double original = basis.Average(v => v.Points[i].BaselineChangeDb);
                Require(previous - mean < (chorus ? .0001 : .012), "The base mean reverse-step budget regressed.");
                Require(mean >= -.001 && mean <= full + (chorus ? .001 : .01), "The base mean left its bounded endpoint window.");
                error += Math.Abs(mean - i / 20d * full) / 21;
                baselineError += Math.Abs(original - i / 20d * full) / 21;
                previous = mean;
            }
            Require(error < (chorus ? .06 : .035) && error < baselineError * .8, "Base guide-error reduction regressed.");
            double midpoint = basis.Average(v => Level(chorus, v.Points[10]));
            Require(midpoint > .35 * full && midpoint < .65 * full, "The base midpoint left its central endpoint window.");
            if (!chorus) Require(Math.Abs(midpoint - full / 2) < 1e-5, "The Flanger mean midpoint moved from the half-dB anchor.");
        });
        check($"{name} catalog controls retain midpoint coverage and average-error budgets", () =>
        {
            int windows = 0; double error = 0, baselineError = 0, meanError = 0;
            foreach (VoiceRecord voice in voices)
            {
                double full = voice.Points[^1].BaselineChangeDb, midpoint = Level(chorus, voice.Points[10]);
                if (midpoint >= Math.Min(0, full) && midpoint <= Math.Max(0, full)) windows++;
                foreach (Point point in voice.Points)
                {
                    error += Math.Abs(Level(chorus, point) - point.Amount * full) / (48 * 21);
                    baselineError += Math.Abs(point.BaselineChangeDb - point.Amount * full) / (48 * 21);
                }
            }
            for (int i = 0; i <= 20; i++)
                meanError += Math.Abs(voices.Average(v => Level(chorus, v.Points[i]))
                    - voices.Average(v => v.Points[^1].BaselineChangeDb) * i / 20d) / 21;
            Require(windows >= (chorus ? 48 : 30), "Recorded midpoint coverage regressed.");
            Require(error < baselineError + .001 && meanError < (chorus ? .066 : .06), "Recorded average-error budgets regressed.");
        });

        foreach (SyntheticRecord record in synthetic)
        foreach (SyntheticPoint point in record.Points)
        {
            check($"{name} independent current PCM at {record.Rate} Hz, scale {record.Scale:R}, amount {point.Amount:R}", () =>
            {
                float[] signal = Signal(record.Rate, record.Rate * 3, record.Scale);
                NewEngine(record.Rate).ApplyEffect(signal, effect, point.Amount);
                double power = 0;
                foreach (float sample in signal)
                {
                    Require(float.IsFinite(sample), "Non-finite waveform.");
                    power += sample * (double)sample;
                }
                float gain = Gain(chorus, point.Amount);
                Require(Math.Abs(10 * Math.Log10(power / signal.Length) - point.OriginalDbfs - 20 * Math.Log10(gain)) < .002,
                    "Output no longer matches the independently captured additive waveform and prepared gain.");
                Require(point.Indices.Length == 16 && point.Samples.Length == 16, "Waveform checkpoints are incomplete.");
                for (int i = 0; i < point.Indices.Length; i++)
                    Require(Math.Abs(signal[point.Indices[i]] - point.Samples[i] * gain) < 3e-6,
                        "Level correction changed a captured waveform checkpoint.");
            });
        }
        foreach (int rate in new[] { 8000, 22050, 48000 })
        {
            check($"{name} changing intensity is independent of streaming partitions at {rate} Hz", () =>
            {
                float[] amounts = [0, .001f, .00101f, .1f, .4995f, .5f, .5005f, .9f, 1, .45f];
                int size = rate / 8;
                float[] whole = Signal(rate, size * amounts.Length, 1), split = (float[])whole.Clone();
                var a = NewEngine(rate); var b = NewEngine(rate);
                for (int section = 0; section < amounts.Length; section++)
                {
                    a.ApplyEffect(whole.AsSpan(section * size, size), effect, amounts[section]);
                    for (int i = 0; i < size; i += 257)
                        b.ApplyEffect(split.AsSpan(section * size + i, Math.Min(257, size - i)), effect, amounts[section]);
                }
                Require(MemoryMarshal.AsBytes(whole.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(split.AsSpan())),
                    "Streaming partitions changed output.");
            });
            check($"{name} warm bypass remains clean at {rate} Hz", () =>
            {
                float[] clean = Signal(rate, rate / 4, 1); var engine = NewEngine(rate);
                engine.ApplyEffect((float[])clean.Clone(), effect, .7f);
                foreach (float amount in new[] { 0f, .001f })
                {
                    float[] output = (float[])clean.Clone(); engine.ApplyEffect(output, effect, amount);
                    Require(output.SequenceEqual(clean), "Warm bypass changed clean samples.");
                }
            });
        }
    }

    /// <summary>Applies prepared gain to an independently captured uncorrected current-path RMS.</summary>
    private static double Level(bool chorus, Point point) => point.UncorrectedChangeDb + 20 * Math.Log10(Gain(chorus, point.Amount));

    /// <summary>Selects the effect-specific once-per-buffer scalar.</summary>
    private static float Gain(bool chorus, float amount) => chorus ? Dsp.ChorusGain(amount) : Dsp.FlangerGain(amount);

    /// <summary>Routes the existing additive mixture without changing its composition.</summary>
    private static float Mix(bool chorus, float dry, float wet, float amount, float gain) => chorus
        ? Dsp.ChorusCrossfade(dry, wet, amount, gain) : Dsp.FlangerCrossfade(dry, wet, amount, gain);

    /// <summary>Exercises warmed runtime preparation and mixing without allocating test objects.</summary>
    private static float PreparedSample(bool chorus, int i)
    {
        float amount = i % 1001 / 1000f;
        return Mix(chorus, .2f, -.1f, amount, Gain(chorus, amount));
    }

    /// <summary>Recreates the frozen multitone at its original input scale.</summary>
    private static float[] Signal(int rate, int count, float scale)
    {
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / rate;
            output[i] = scale * (float)((.18 * Math.Sin(2 * Math.PI * 110 * t) + .08 * Math.Sin(2 * Math.PI * 330 * t)
                + .05 * Math.Sin(2 * Math.PI * 1700 * t) + .03 * Math.Sin(2 * Math.PI * Math.Min(3200, rate * .3) * t))
                * (.7 + .3 * Math.Sin(2 * Math.PI * .73 * t)));
        }
        return output;
    }

    /// <summary>Creates replayable effect state without model weights or native synthesis.</summary>
    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456); return engine;
    }

    /// <summary>Loads recorded numeric fixtures only when the test runner invokes these checks.</summary>
    private static Corpus Load()
    {
        using Stream stream = typeof(ModulationCurveChecks).Assembly.GetManifestResourceStream("ModulationCurveMoments.json")
            ?? throw new InvalidDataException("Missing current-path modulation fixtures.");
        return JsonSerializer.Deserialize<Corpus>(stream) ?? throw new InvalidDataException("Invalid modulation fixtures.");
    }

    /// <summary>Reports an independently observed contract failure.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Corpus(VoiceRecord[] Voices, SyntheticRecord[] Synthetic);
    private sealed record VoiceRecord(string Name, string Voice, string Effect, int Rate, Point[] Points);
    private sealed record Point(float Amount, double UncorrectedChangeDb, double BaselineChangeDb);
    private sealed record SyntheticRecord(string Effect, int Rate, float Scale, SyntheticPoint[] Points);
    private sealed record SyntheticPoint(float Amount, double OriginalDbfs, int[] Indices, float[] Samples);
}
