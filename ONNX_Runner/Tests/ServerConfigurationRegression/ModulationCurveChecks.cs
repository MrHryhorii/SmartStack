using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class ModulationCurveChecks
{
    private static readonly Corpus Fixtures = Load();
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly FieldInfo ModulationField = typeof(AudioEffectsEngine)
        .GetField("_mod", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Checks only the complete Chorus/Flanger paths and their shared modulation helpers.</summary>
    internal static void Run(Action<string, Action> check, VoiceEffectType effect)
    {
        bool chorus = effect == VoiceEffectType.Chorus;
        string name = effect.ToString();
        VoiceRecord[] voices = Fixtures.Voices.Where(x => x.Effect == name).ToArray();
        SyntheticRecord[] synthetic = Fixtures.Synthetic.Where(x => x.Effect == name).ToArray();
        check($"{name} retains its measured voice curves and independent full-strength fixtures", () =>
        {
            Require(voices.Length == 31 && synthetic.Length == 27, "Incomplete modulation fixtures.");
            Require(voices.Count(x => x.Group == "dense") == 1, "Missing dense base curve.");
        });
        check($"{name} gain preserves unity endpoints and remains modest throughout the active range", () =>
        {
            foreach (float amount in new[] { -1f, 0f, .001f, float.NaN, 1f, 2f, float.PositiveInfinity })
                Require(Gain(chorus, amount) == 1f, "An endpoint or invalid-input gain changed.");
            for (int i = 0; i <= 10000; i++)
            {
                float gain = Gain(chorus, i / 10000f);
                Require(float.IsFinite(gain) && gain > .9f && gain < 1.1f, "Playback scalar left its calibrated range.");
            }
        });
        check($"{name} scalar curve has continuous midpoint value and slope", () =>
        {
            const float h = .001f;
            double middle = Gain(chorus, .5f);
            double left = (3 * middle - 4 * Gain(chorus, .5f - h) + Gain(chorus, .5f - 2 * h)) / (2 * h);
            double right = (-3 * middle + 4 * Gain(chorus, .5f + h) - Gain(chorus, .5f + 2 * h)) / (2 * h);
            Require(Math.Abs(left - right) < .004, "The scalar curve has a midpoint slope jump.");
            Require(Math.Abs(Gain(chorus, MathF.BitDecrement(.5f)) - Gain(chorus, MathF.BitIncrement(.5f))) < 3e-6,
                "The scalar curve has a midpoint value jump.");
        });
        check($"{name} saturation converges to identity near zero drive and retains full tanh", () =>
        {
            foreach (float sample in new[] { -.9f, -.25f, 0f, .2f, .8f })
            {
                Require(Dsp.ModulationSaturate(sample, 0f, float.PositiveInfinity) == sample, "Zero drive changed input.");
                Require(Math.Abs(Dsp.ModulationSaturate(sample, .001f, 1000f) - sample) < 1e-6,
                    "Tiny modulation drive immediately saturated the dry signal.");
                Require(Dsp.ModulationSaturate(sample, 1f, 1f) == MathF.Tanh(sample), "Full saturation changed.");
            }
        });
        check($"{name} midpoint controls delayed level and modulation depth internally", () =>
        {
            var engine = NewEngine(22050);
            engine.ApplyEffect(Span<float>.Empty, effect, .5f);
            Require(Value<float>(engine, "Strength") == .75f && Value<float>(engine, "SaturationDrive") == .75f,
                "Midpoint lost its internal depth or gradual saturation.");
            Require(Math.Abs(Value<float>(engine, "WetScale") - (chorus ? .3375f : .54f)) < 1e-7,
                "Delayed contribution no longer follows internal intensity.");
            Require(Math.Abs(Value<float>(engine, "Base1") - (chorus ? 15f : 1.8f)) < 1e-6,
                "The base delay varies with intensity.");
            if (chorus) Require(Value<float>(engine, "Base2") == 24f, "The second chorus base delay changed.");
            else Require(Value<float>(engine, "Feedback") == .5625f, "Flanger feedback does not follow intensity.");
        });
        check($"{name} gain scales only the output and cannot alter delay or feedback history", () =>
        {
            var a = NewEngine(22050); var b = NewEngine(22050);
            a.ApplyEffect(Span<float>.Empty, effect, .5f); b.ApplyEffect(Span<float>.Empty, effect, .5f);
            object state = ModulationField.GetValue(b)!;
            state.GetType().GetField("Gain")!.SetValue(state, Value<float>(b, "Gain") * .5f);
            ModulationField.SetValue(b, state);
            float[] first = Signal(22050, 22050, 1f), second = (float[])first.Clone();
            a.ApplyEffect(first, effect, .5f); b.ApplyEffect(second, effect, .5f);
            for (int i = 0; i < first.Length; i++) Require(second[i] == first[i] * .5f,
                "The scalar entered the delay or feedback path.");
        });
        check($"{name} identical intensity is re-prepared after switching the modulation type", () =>
        {
            var engine = NewEngine(22050);
            VoiceEffectType other = chorus ? VoiceEffectType.Flanger : VoiceEffectType.Chorus;
            engine.ApplyEffect(Span<float>.Empty, other, .5f);
            engine.ApplyEffect(Span<float>.Empty, effect, .5f);
            Require(Math.Abs(Value<float>(engine, "Base1") - (chorus ? 15f : 1.8f)) < 1e-6,
                "The shared parameter cache retained the previous effect's delay.");
            Require(Value<float>(engine, "Gain") == Gain(chorus, .5f), "The previous effect's scalar leaked.");
            float[] switched = Signal(22050, 11025, 1f), fresh = (float[])switched.Clone();
            engine.ApplyEffect(switched, effect, .5f);
            NewEngine(22050).ApplyEffect(fresh, effect, .5f);
            Require(SameBits(switched, fresh), "An empty type switch changed the new effect's state.");
        });
        check($"{name} parallel requests retain independent modulation state", () =>
        {
            float[] source = Signal(22050, 22050, 1f), expected = (float[])source.Clone();
            NewEngine(22050).ApplyEffect(expected, effect, .5f);
            bool[] passed = new bool[24];
            Parallel.For(0, passed.Length, i =>
            {
                float[] output = (float[])source.Clone();
                var engine = NewEngine(22050);
                int block = 37 + i;
                for (int offset = 0; offset < output.Length; offset += block)
                    engine.ApplyEffect(output.AsSpan(offset, Math.Min(block, output.Length - offset)), effect, .5f);
                passed[i] = SameBits(output, expected);
            });
            Require(passed.All(x => x), "Parallel requests shared a delay, phase or preparation cache.");
        });
        foreach (VoiceRecord voice in voices)
        {
            check($"{name} retains offline level bounds for {voice.Name} at scale {voice.Scale:R}", () =>
            {
                Require(voice.Points[0].Amount == 0 && voice.Points[^1].Amount == 1f, "Missing level endpoints.");
                double full = voice.Points[^1].ExpectedDeltaLU, previous = 0;
                foreach (Point point in voice.Points)
                {
                    double level = Level(chorus, voice, point);
                    Require(Math.Abs(level - point.ExpectedDeltaLU) < .005, "Playback no longer matches recorded wet levels.");
                    Require(Math.Abs(level - point.Amount * full) <= voice.GuideBudgetLU, "Recorded guide error increased.");
                    Require(Math.Sign(full) * (previous - level) <= voice.ReverseBudgetLU, "Recorded reverse-step budget increased.");
                    previous = level;
                }
            });
        }
        check($"{name} dense base curve retains a useful midpoint and small reverse steps", () =>
        {
            VoiceRecord basis = voices.Single(x => x.Group == "dense");
            double previous = 0;
            foreach (Point point in basis.Points)
            {
                double level = Level(chorus, basis, point);
                Require(previous - level < .04, "A large base-voice reverse step returned.");
                previous = level;
            }
            double full = basis.Points[^1].ExpectedDeltaLU;
            double midpoint = Level(chorus, basis, basis.Points.Single(x => x.Amount == .5f));
            Require(midpoint > .35 * full && midpoint < .65 * full, "Base midpoint left its useful level window.");
        });
        foreach (SyntheticRecord record in synthetic)
        {
            check($"{name} preserves accepted full PCM at {record.Rate} Hz and scale {record.Scale:R}", () =>
            {
                float[] source = Signal(record.Rate, record.Rate * 3, record.Scale);
                NewEngine(record.Rate).ApplyEffect(source, effect, 1f);
                Require(source.All(float.IsFinite), "Non-finite full output.");
                Require(Math.Abs(RmsDb(source) - record.RmsDbfs) < .002, "Full-effect level changed.");
                Require(record.Indices.Length == 16 && record.Samples.Length == 16, "Missing full waveform checkpoints.");
                for (int i = 0; i < record.Indices.Length; i++)
                    Require(Math.Abs(source[record.Indices[i]] - record.Samples[i]) < 3e-6, "Full-effect waveform changed.");
            });
        }
        foreach (int rate in Rates)
        {
            check($"{name} remains finite and independent of stream partitions at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate / 8, 1f);
                foreach (float amount in new[] { .0011f, .01f, .25f, .5f, .75f, 1f })
                {
                    float[] whole = (float[])input.Clone(), split = (float[])input.Clone();
                    NewEngine(rate).ApplyEffect(whole, effect, amount);
                    var engine = NewEngine(rate);
                    for (int i = 0; i < split.Length; i += 37)
                        engine.ApplyEffect(split.AsSpan(i, Math.Min(37, split.Length - i)), effect, amount);
                    Require(SameBits(whole, split), "Streaming boundaries changed output.");
                    Require(whole.All(x => float.IsFinite(x) && Math.Abs(x) < 2), "Invalid modulation output.");
                }
            });
            check($"{name} changing intensity preserves cache, streaming, reset and warm bypass at {rate} Hz", () =>
            {
                float[] amounts = [0f, .001f, .0011f, .01f, .25f, .4995f, .5f, .5005f, .75f, 1f, .45f];
                int size = rate / 20;
                float[] whole = Signal(rate, size * amounts.Length, 1f), split = (float[])whole.Clone();
                var a = NewEngine(rate); var b = NewEngine(rate);
                for (int section = 0; section < amounts.Length; section++)
                {
                    a.ApplyEffect(whole.AsSpan(section * size, size), effect, amounts[section]);
                    for (int i = 0; i < size; i += 257)
                        b.ApplyEffect(split.AsSpan(section * size + i, Math.Min(257, size - i)), effect, amounts[section]);
                }
                Require(SameBits(whole, split), "Changing preparation depends on block size.");
                foreach (float amount in new[] { 0f, .001f })
                {
                    float[] clean = Signal(rate, 257, 1f), output = (float[])clean.Clone();
                    a.ApplyEffect(output, effect, amount);
                    Require(SameBits(clean, output), "Warm bypass changed clean samples.");
                }
                a.Reset(123456); b.Reset(123456);
                float[] resetA = Signal(rate, rate / 8, 1f), resetB = (float[])resetA.Clone();
                a.ApplyEffect(resetA, effect, .5f); b.ApplyEffect(resetB, effect, .5f);
                Require(SameBits(resetA, resetB), "Seeded reset is not reproducible.");
            });
            check($"{name} gradual preparation and hot processing allocate zero managed bytes at {rate} Hz", () =>
            {
                var engine = NewEngine(rate);
                float[] block = Signal(rate, 257, 1f);
                for (int i = 0; i < 256; i++) engine.ApplyEffect(block, effect, .01f + i % 99 / 100f);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 256; i++) engine.ApplyEffect(block, effect, .01f + i % 99 / 100f);
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "Modulation preparation or processing allocated memory.");
            });
        }
        check($"{name} approaches a clean signal at the first active intensity", () =>
        {
            float[] source = Signal(22050, 22050, 1f), output = (float[])source.Clone();
            NewEngine(22050).ApplyEffect(output, effect, .0011f);
            double error = 0, power = 0;
            for (int i = 0; i < source.Length; i++) { error += Math.Pow(output[i] - source[i], 2); power += source[i] * (double)source[i]; }
            Require(error < power * .0001, "The first active intensity introduced a large discontinuity.");
        });
        check($"{name} preserves digital silence throughout the intensity range", () =>
        {
            for (int i = 0; i <= 20; i++)
            {
                float[] silence = new float[22050];
                NewEngine(22050).ApplyEffect(silence, effect, i / 20f);
                Require(silence.All(x => x == 0f), "Modulation generated sound from silence.");
            }
        });
    }

    private static double Level(bool chorus, VoiceRecord voice, Point point) => point.Amount <= .001f ? 0
        : point.UntrimmedLufs - voice.DryLufs + 20 * Math.Log10(Gain(chorus, point.Amount));
    private static float Gain(bool chorus, float amount) => chorus ? Dsp.ChorusGain(amount) : Dsp.FlangerGain(amount);
    private static T Value<T>(AudioEffectsEngine engine, string name)
    {
        object state = ModulationField.GetValue(engine)!;
        return (T)state.GetType().GetField(name)!.GetValue(state)!;
    }
    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456); return engine;
    }
    private static float[] Signal(int rate, int count, float scale)
    {
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            double t = i / (double)rate;
            output[i] = scale * (float)((.18 * Math.Sin(2 * Math.PI * 110 * t) + .08 * Math.Sin(2 * Math.PI * 330 * t)
                + .05 * Math.Sin(2 * Math.PI * 1700 * t) + .03 * Math.Sin(2 * Math.PI * Math.Min(3200, rate * .3) * t))
                * (.7 + .3 * Math.Sin(2 * Math.PI * .73 * t)));
        }
        return output;
    }
    private static double RmsDb(ReadOnlySpan<float> samples)
    {
        double power = 0;
        foreach (float sample in samples) power += sample * (double)sample;
        return 10 * Math.Log10(power / samples.Length);
    }
    private static bool SameBits(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static Corpus Load()
    {
        using Stream stream = typeof(ModulationCurveChecks).Assembly.GetManifestResourceStream("ModulationCurveMoments.json")
            ?? throw new InvalidDataException("Missing modulation-only level fixtures.");
        return JsonSerializer.Deserialize<Corpus>(stream) ?? throw new InvalidDataException("Invalid modulation-only level fixtures.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed record Corpus(VoiceRecord[] Voices, SyntheticRecord[] Synthetic);
    private sealed record VoiceRecord(string Name, string Effect, string Group, float Scale, double DryLufs,
        double GuideBudgetLU, double ReverseBudgetLU, Point[] Points);
    private sealed record Point(float Amount, double UntrimmedLufs, double ExpectedDeltaLU);
    private sealed record SyntheticRecord(string Effect, int Rate, float Scale, double RmsDbfs, int[] Indices, float[] Samples);
}
