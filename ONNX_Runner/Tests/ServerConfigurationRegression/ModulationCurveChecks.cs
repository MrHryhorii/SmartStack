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

    /// <summary>Checks intrinsic modulation, raw character and independent streaming power correction.</summary>
    internal static void Run(Action<string, Action> check, VoiceEffectType effect)
    {
        bool chorus = effect == VoiceEffectType.Chorus;
        string name = effect.ToString();
        SyntheticRecord[] synthetic = Fixtures.Synthetic.Where(x => x.Effect == name).ToArray();
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
        check($"{name} power correction cannot add clean audio or change delay history", () =>
        {
            var a = NewEngine(22050); var b = NewEngine(22050);
            a.ApplyEffect(Span<float>.Empty, effect, .5f); b.ApplyEffect(Span<float>.Empty, effect, .5f);
            SetLevel(b, new WindowedPowerMatcher(22050, initialGain: 0, windowMs: 60000, warmupMs: 60000));
            float[] first = Signal(22050, 22050, 1), muted = (float[])first.Clone();
            a.ApplyEffect(first, effect, .5f); b.ApplyEffect(muted, effect, .5f);
            Require(muted.All(x => x == 0), "A clean branch bypassed output correction.");
            FreezeLevel(a, 22050); FreezeLevel(b, 22050);
            first = Signal(22050, 22050, 1); float[] second = (float[])first.Clone();
            a.ApplyEffect(first, effect, .5f); b.ApplyEffect(second, effect, .5f);
            Require(SameBits(first, second), "Output correction entered the delay or feedback path.");
        });
        check($"{name} bypass creates no level-analysis state", () =>
        {
            var engine = NewEngine(22050); float[] input = Signal(22050, 257, 1);
            engine.ApplyEffect(input, effect, 0);
            Require(Value<WindowedPowerMatcher?>(engine, "Level") == null, "Bypass created a level tracker.");
        });
        check($"{name} a new tiny intensity cannot inherit earlier makeup gain", () =>
        {
            var engine = NewEngine(22050);
            engine.ApplyEffect(Span<float>.Empty, effect, 1);
            SetLevel(engine, new WindowedPowerMatcher(22050, initialGain: 4));
            engine.ApplyEffect(Span<float>.Empty, effect, .0011f);
            Require(Value<WindowedPowerMatcher>(engine, "Level").Gain == 1,
                "A prior intensity left a stale output gain.");
        });
        check($"{name} identical intensity is re-prepared after switching the modulation type", () =>
        {
            var engine = NewEngine(22050);
            VoiceEffectType other = chorus ? VoiceEffectType.Flanger : VoiceEffectType.Chorus;
            engine.ApplyEffect(Span<float>.Empty, other, .5f);
            engine.ApplyEffect(Span<float>.Empty, effect, .5f);
            Require(Math.Abs(Value<float>(engine, "Base1") - (chorus ? 15f : 1.8f)) < 1e-6,
                "The shared parameter cache retained the previous effect's delay.");
            Require(Value<WindowedPowerMatcher>(engine, "Level").Gain == 1, "The previous effect's scalar leaked.");
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
        foreach (SyntheticRecord record in synthetic)
        {
            check($"{name} preserves accepted full raw PCM at {record.Rate} Hz and scale {record.Scale:R}", () =>
            {
                float[] source = Signal(record.Rate, record.Rate * 3, record.Scale);
                var engine = NewEngine(record.Rate);
                engine.ApplyEffect(Span<float>.Empty, effect, 1f);
                FreezeLevel(engine, record.Rate);
                engine.ApplyEffect(source, effect, 1f);
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

    private static void SetLevel(AudioEffectsEngine engine, WindowedPowerMatcher level)
    {
        object state = ModulationField.GetValue(engine)!;
        state.GetType().GetField("Level")!.SetValue(state, level);
        ModulationField.SetValue(engine, state);
    }
    private static void FreezeLevel(AudioEffectsEngine engine, int rate) =>
        SetLevel(engine, new WindowedPowerMatcher(rate, windowMs: 60000, warmupMs: 60000));

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
    private sealed record Corpus(SyntheticRecord[] Synthetic);
    private sealed record SyntheticRecord(string Effect, int Rate, float Scale, double RmsDbfs, int[] Indices, float[] Samples);
}
