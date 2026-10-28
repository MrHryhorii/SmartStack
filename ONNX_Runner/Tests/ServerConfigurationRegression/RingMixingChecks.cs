using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class RingMixingChecks
{
    private static readonly Corpus Fixtures = Load();
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly FieldInfo RingField = typeof(AudioEffectsEngine)
        .GetField("_ring", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Checks only direct RingModulator processing and the tremolo-to-ring transition.</summary>
    internal static void Run(Action<string, Action> check)
    {
        WindowedPowerMatcherChecks.Run(check);
        check("RingModulator bypass does not construct or advance a level matcher", () =>
        {
            foreach (float amount in new[] { 0f, .001f })
            {
                var engine = NewEngine(22050);
                float[] source = Signal(22050, 4096, 1f), output = (float[])source.Clone();
                engine.ApplyEffect(output, VoiceEffectType.RingModulator, amount);
                Require(SameBits(source, output) && Level(engine) is null, "Bypass prepared normalization or changed PCM.");
            }
        });
        check("RingModulator tiny intensity cannot inherit a preceding tremolo makeup gain", () =>
        {
            const int rate = 48000;
            float[] tone = Enumerable.Range(0, rate).Select(i => .1f*(float)Math.Sin(2*Math.PI*1000*i/rate)).ToArray();
            var engine = NewEngine(rate);
            engine.ApplyEffect((float[])tone.Clone(), VoiceEffectType.RingModulator, .5f);
            float[] actual = (float[])tone.Clone();
            engine.ApplyEffect(actual, VoiceEffectType.RingModulator, .0011f);
            Require(Math.Abs(RmsDb(actual)-RmsDb(tone)) < .05, "An earlier effect intensity left a hidden output boost.");
        });
        check("RingModulator quiet lead-in cannot create a loud first-syllable gain spike", () =>
        {
            const int rate = 48000;
            float[] source = Enumerable.Range(0, rate/2).Select(i => i < rate/10 ? .001f
                : .1f*(float)Math.Sin(2*Math.PI*1000*i/rate)).ToArray();
            float[] actual = (float[])source.Clone(), reference = (float[])source.Clone();
            var adaptive = NewEngine(rate); var fixedGain = NewEngine(rate);
            adaptive.ApplyEffect(Span<float>.Empty, VoiceEffectType.RingModulator, .5f);
            fixedGain.ApplyEffect(Span<float>.Empty, VoiceEffectType.RingModulator, .5f);
            SetLevel(fixedGain, FixedGain(rate, Level(adaptive).Gain));
            adaptive.ApplyEffect(actual, VoiceEffectType.RingModulator, .5f);
            fixedGain.ApplyEffect(reference, VoiceEffectType.RingModulator, .5f);
            float actualPeak = actual.AsSpan(rate/10, rate/20).ToArray().Max(MathF.Abs);
            float referencePeak = reference.AsSpan(rate/10, rate/20).ToArray().Max(MathF.Abs);
            Require(actualPeak <= referencePeak*1.15f, "A quiet lead-in caused excessive first-syllable makeup.");
        });
        foreach (int rate in Rates)
        foreach (float amount in new[] { .25f, .5f, .75f, 1f })
        {
            check($"RingModulator restores settled tone power at {rate} Hz and intensity {amount:R}", () =>
            {
                float[] source = Enumerable.Range(0, rate*2).Select(i => .1f*(float)Math.Sin(2*Math.PI*1000*i/rate)).ToArray();
                float[] output = (float[])source.Clone();
                NewEngine(rate).ApplyEffect(output, VoiceEffectType.RingModulator, amount);
                double delta = RmsDb(output.AsSpan(rate))-RmsDb(source.AsSpan(rate));
                Require(Math.Abs(delta) < .05, "The settled tone missed its reference power.");
            });
        }
        check("RingModulator retains deep midpoint tremolo with tiny incoming buffers", () =>
        {
            const int rate = 48000;
            float[] samples = Enumerable.Range(0, rate*4).Select(i => .1f*(float)Math.Sin(2*Math.PI*1000*i/rate)).ToArray();
            var engine = NewEngine(rate);
            for (int offset = 0; offset < samples.Length; offset += 37)
                engine.ApplyEffect(samples.AsSpan(offset, Math.Min(37, samples.Length-offset)), VoiceEffectType.RingModulator, .5f);
            double sum = 0, sine = 0, cosine = 0;
            for (int offset = rate/2; offset+48 <= samples.Length; offset += 48)
            {
                double envelope = Math.Sqrt(PowerMatcher.Measure(samples.AsSpan(offset, 48)));
                double phase = 2*Math.PI*30*(offset+23.5)/rate;
                sum += envelope; sine += envelope*Math.Sin(phase); cosine += envelope*Math.Cos(phase);
            }
            double depth = 2*Math.Sqrt(sine*sine+cosine*cosine)/sum;
            Require(depth > .98 && depth < 1.02, "Normalization followed and flattened the intended tremolo.");
        });
        check("RingModulator full ring retains its sixty-hertz amplitude envelope", () =>
        {
            const int rate = 48000;
            float[] samples = Enumerable.Range(0, rate*4).Select(i => .1f*(float)Math.Sin(2*Math.PI*1000*i/rate)).ToArray();
            var engine = NewEngine(rate);
            for (int offset = 0; offset < samples.Length; offset += 37)
                engine.ApplyEffect(samples.AsSpan(offset, Math.Min(37, samples.Length-offset)), VoiceEffectType.RingModulator, 1f);
            double sum = 0, sine = 0, cosine = 0;
            for (int offset = rate/2; offset+48 <= samples.Length; offset += 48)
            {
                double envelope = Math.Sqrt(PowerMatcher.Measure(samples.AsSpan(offset, 48)));
                double phase = 2*Math.PI*60*(offset+23.5)/rate;
                sum += envelope; sine += envelope*Math.Sin(phase); cosine += envelope*Math.Cos(phase);
            }
            double depth = 2*Math.Sqrt(sine*sine+cosine*cosine)/sum;
            Require(depth > .63 && depth < .69, "Normalization flattened the absolute envelope of full ring modulation.");
        });
        check("RingModulator midpoint remains the boundary between tremolo and polarity reversal", () =>
        {
            var engine = NewEngine(22050);
            foreach (float amount in new[] { .0011f, .01f, .1f, .25f, .5f, .75f, 1f })
            {
                engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.RingModulator, amount);
                float depth = Value(engine, "Depth");
                Require(depth == amount, "Carrier depth no longer follows the declared intensity.");
                Require(Value(engine, "SaturationDrive") == amount, "Saturation no longer approaches identity near zero.");
                float minimum = 1f-2f*depth;
                Require(amount <= .5f ? minimum >= 0f : minimum < 0f, "The tremolo-to-ring boundary moved.");
            }
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.RingModulator, 1f);
            Require(!Level(engine).HasSamples && Value(engine, "Makeup") == 1.8f,
                "Empty preparation advanced analysis or changed modulation makeup.");
        });
        check("RingModulator output scalar cannot leave a hidden clean branch or alter modulation history", () =>
        {
            var a = NewEngine(22050); var b = NewEngine(22050); var muted = NewEngine(22050);
            foreach (var engine in new[] { a, b, muted })
                engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.RingModulator, .5f);
            SetLevel(a, FixedGain(22050, 1f)); SetLevel(b, FixedGain(22050, .5f)); SetLevel(muted, FixedGain(22050, 0f));
            float[] first = Signal(22050, 22050, 1f), second = (float[])first.Clone(), zero = (float[])first.Clone();
            a.ApplyEffect(first, VoiceEffectType.RingModulator, .5f);
            b.ApplyEffect(second, VoiceEffectType.RingModulator, .5f);
            muted.ApplyEffect(zero, VoiceEffectType.RingModulator, .5f);
            for (int i = 0; i < first.Length; i++)
            {
                Require(second[i] == first[i]*.5f, "Output gain changed state or internal processing.");
                Require(zero[i] == 0f, "A separate clean branch survived the final scalar.");
            }
        });
        check("RingModulator preparation is invalidated when returning from another effect", () =>
        {
            var engine = NewEngine(22050);
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.RingModulator, .5f);
            double expected = Level(engine).Gain; Level(engine).Reset(0);
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Telephone, .5f);
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.RingModulator, .5f);
            Require(Level(engine).Gain == expected, "The previous effect left a stale ring cache.");
            float[] actual = Signal(22050, 11025, 1f), fresh = (float[])actual.Clone();
            engine.ApplyEffect(actual, VoiceEffectType.RingModulator, .5f);
            NewEngine(22050).ApplyEffect(fresh, VoiceEffectType.RingModulator, .5f);
            Require(SameBits(actual, fresh), "An empty effect switch changed the ring path.");
        });
        check("RingModulator parallel requests retain independent phase, noise, filters and gain", () =>
        {
            float[] input = Signal(22050, 22050, 1f), expected = (float[])input.Clone();
            NewEngine(22050).ApplyEffect(expected, VoiceEffectType.RingModulator, .5f);
            bool[] passed = new bool[24];
            Parallel.For(0, passed.Length, i =>
            {
                float[] actual = (float[])input.Clone(); var engine = NewEngine(22050);
                int block = 37+i;
                for (int offset = 0; offset < actual.Length; offset += block)
                    engine.ApplyEffect(actual.AsSpan(offset, Math.Min(block, actual.Length-offset)), VoiceEffectType.RingModulator, .5f);
                passed[i] = SameBits(actual, expected);
            });
            Require(passed.All(x => x), "Parallel requests shared DSP state.");
        });
        foreach (SyntheticRecord record in Fixtures.Synthetic)
        {
            check($"RingModulator preserves the accepted full modulation kernel at {record.Rate} Hz and scale {record.Scale:R}", () =>
            {
                float[] actual = Signal(record.Rate, record.Rate*3, record.Scale);
                var engine = NewEngine(record.Rate);
                engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.RingModulator, 1f);
                SetLevel(engine, FixedGain(record.Rate, .91f));
                engine.ApplyEffect(actual, VoiceEffectType.RingModulator, 1f);
                Require(actual.All(float.IsFinite), "Non-finite full output.");
                Require(Math.Abs(RmsDb(actual)-record.RmsDbfs) < .002, "Full-output level changed.");
                Require(record.Indices.Length == 16 && record.Samples.Length == 16, "Incomplete full PCM fixture.");
                for (int i = 0; i < record.Indices.Length; i++)
                    Require(Math.Abs(actual[record.Indices[i]]-record.Samples[i]) < 3e-6, "Full-output waveform changed.");
            });
        }
        foreach (int rate in Rates)
        {
            check($"RingModulator remains finite and independent of stream partitions at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate/8, 1f);
                foreach (float amount in new[] { .0011f, .01f, .25f, .5f, .75f, 1f })
                {
                    float[] whole = (float[])input.Clone(), split = (float[])input.Clone();
                    NewEngine(rate).ApplyEffect(whole, VoiceEffectType.RingModulator, amount);
                    var engine = NewEngine(rate);
                    for (int i = 0; i < split.Length; i += 37)
                        engine.ApplyEffect(split.AsSpan(i, Math.Min(37, split.Length-i)), VoiceEffectType.RingModulator, amount);
                    Require(SameBits(whole, split), "Streaming boundaries changed output.");
                    Require(whole.All(x => float.IsFinite(x) && Math.Abs(x) < 2), "Invalid direct output.");
                }
            });
            check($"RingModulator changing intensity preserves streaming, reset and warm bypass at {rate} Hz", () =>
            {
                float[] amounts = [0f, .001f, .0011f, .01f, .25f, .4995f, .5f, .5005f, .75f, 1f, .45f];
                int size = rate/20;
                float[] whole = Signal(rate, size*amounts.Length, 1f), split = (float[])whole.Clone();
                var a = NewEngine(rate); var b = NewEngine(rate);
                for (int section = 0; section < amounts.Length; section++)
                {
                    a.ApplyEffect(whole.AsSpan(section*size, size), VoiceEffectType.RingModulator, amounts[section]);
                    for (int i = 0; i < size; i += 257)
                        b.ApplyEffect(split.AsSpan(section*size+i, Math.Min(257, size-i)), VoiceEffectType.RingModulator, amounts[section]);
                }
                Require(SameBits(whole, split), "Changing intensity depends on partition size.");
                foreach (float amount in new[] { 0f, .001f })
                {
                    float[] clean = Signal(rate, 257, 1f), actual = (float[])clean.Clone();
                    a.ApplyEffect(actual, VoiceEffectType.RingModulator, amount);
                    Require(SameBits(clean, actual), "Warm bypass changed samples.");
                }
                float[] expected = Signal(rate, rate/8, 1f); NewEngine(rate).ApplyEffect(expected, VoiceEffectType.RingModulator, .5f);
                a.Reset(123456); float[] reset = Signal(rate, rate/8, 1f); a.ApplyEffect(reset, VoiceEffectType.RingModulator, .5f);
                Require(SameBits(reset, expected), "Reset retained prior phase, filters or cached parameters.");
            });
            check($"RingModulator preparation and hot processing allocate zero managed bytes at {rate} Hz", () =>
            {
                var engine = NewEngine(rate); float[] block = Signal(rate, 257, 1f);
                for (int i = 0; i < 256; i++) engine.ApplyEffect(block, VoiceEffectType.RingModulator, .01f+i%99/100f);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 256; i++) engine.ApplyEffect(block, VoiceEffectType.RingModulator, .01f+i%99/100f);
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "Preparation or processing allocated managed memory.");
            });
            check($"RingModulator first active intensity approaches clean audio at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate, 1f), actual = (float[])input.Clone();
                NewEngine(rate).ApplyEffect(actual, VoiceEffectType.RingModulator, .0011f);
                double error = 0, power = 0;
                for (int i = 0; i < input.Length; i++) { error += Math.Pow(actual[i]-input[i], 2); power += input[i]*(double)input[i]; }
                Require(error < power*.0001, "Tiny intensity introduced a large filter or saturation discontinuity.");
            });
        }
        foreach (int rate in new[] { 8000, 22050, 48000 })
        foreach (float amount in new[] { .25f, .5f, .75f, 1f })
        {
            check($"RingModulator has the declared carrier-to-sideband ratio at {rate} Hz and intensity {amount:R}", () =>
            {
                float[] tone = Enumerable.Range(0, rate*2).Select(i => .008f*(float)Math.Sin(2*Math.PI*1000*i/rate)).ToArray();
                NewEngine(rate).ApplyEffect(tone, VoiceEffectType.RingModulator, amount);
                double center = Amplitude(tone, rate, 1000), lower = Amplitude(tone, rate, 970), upper = Amplitude(tone, rate, 1030);
                double side = (lower+upper)*.5, ratio = center/side, expected = 2*(1-amount)/amount;
                Require(side > .0001 && Math.Abs(lower-upper) < side*.1, "Expected modulation sidebands are missing or unbalanced.");
                Require(Math.Abs(ratio-expected) < .08*(1+expected), $"The tremolo-to-ring ratio changed: {ratio:R}, expected {expected:R}.");
            });
        }
        check("RingModulator preserves digital silence throughout the intensity range", () =>
        {
            for (int i = 0; i <= 20; i++)
            {
                float[] silence = new float[22050]; NewEngine(22050).ApplyEffect(silence, VoiceEffectType.RingModulator, i/20f);
                Require(silence.All(x => x == 0f), "The ring path generated audio from silence.");
            }
        });
    }

    private static WindowedPowerMatcher Level(AudioEffectsEngine engine)
    {
        object state = RingField.GetValue(engine)!;
        return (WindowedPowerMatcher)state.GetType().GetField("Level")!.GetValue(state)!;
    }
    private static WindowedPowerMatcher FixedGain(int rate, double gain) =>
        new(rate, gain, windowMs: 60000, warmupMs: 60000);
    private static void SetLevel(AudioEffectsEngine engine, WindowedPowerMatcher level)
    {
        object state = RingField.GetValue(engine)!;
        state.GetType().GetField("Level")!.SetValue(state, level); RingField.SetValue(engine, state);
    }
    private static float Value(AudioEffectsEngine engine, string name)
    {
        object state = RingField.GetValue(engine)!;
        return (float)state.GetType().GetField(name)!.GetValue(state)!;
    }
    private static double Amplitude(float[] samples, int rate, double hz)
    {
        double sine = 0, cosine = 0;
        for (int i = rate/2; i < rate/2+rate; i++)
        {
            double phase = 2*Math.PI*hz*i/rate; sine += samples[i]*Math.Sin(phase); cosine += samples[i]*Math.Cos(phase);
        }
        return 2*Math.Sqrt(sine*sine+cosine*cosine)/rate;
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
            double t = i/(double)rate;
            output[i] = scale*(float)((.18*Math.Sin(2*Math.PI*110*t)+.08*Math.Sin(2*Math.PI*330*t)
                +.05*Math.Sin(2*Math.PI*1700*t)+.03*Math.Sin(2*Math.PI*Math.Min(3200, rate*.3)*t))
                *(.7+.3*Math.Sin(2*Math.PI*.73*t)));
        }
        return output;
    }
    private static double RmsDb(ReadOnlySpan<float> samples)
    {
        double power = 0; foreach (float sample in samples) power += sample*(double)sample; return 10*Math.Log10(power/samples.Length);
    }
    private static bool SameBits(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static Corpus Load()
    {
        using Stream stream = typeof(RingMixingChecks).Assembly.GetManifestResourceStream("RingCurveMoments.json")
            ?? throw new InvalidDataException("Missing direct RingModulator level fixtures.");
        return JsonSerializer.Deserialize<Corpus>(stream) ?? throw new InvalidDataException("Invalid direct RingModulator fixtures.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed record Corpus(VoiceRecord[] Voices, SyntheticRecord[] Synthetic);
    private sealed record VoiceRecord(string Name, string Group, float Scale, double DryRmsDbfs, double GuideBudgetDb, double ReverseBudgetDb, Point[] Points);
    private sealed record Point(float Amount, double UntrimmedRmsDbfs, double ExpectedDeltaDb);
    private sealed record SyntheticRecord(int Rate, float Scale, double RmsDbfs, int[] Indices, float[] Samples);
}
