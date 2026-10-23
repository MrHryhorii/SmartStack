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
        check("RingModulator retains independent full endpoints and measured level curves", () =>
        {
            Require(Fixtures.Voices.Length == 31 && Fixtures.Synthetic.Length == 27, "Incomplete ring fixtures.");
            Require(Fixtures.Voices.Count(v => v.Group == "dense") == 1, "Missing dense base curve.");
        });
        check("RingModulator correction retains unity endpoints and a modest positive range", () =>
        {
            foreach (float amount in new[] { -1f, 0f, .001f, float.NaN, 1f, 2f, float.PositiveInfinity })
                Require(Dsp.RingGain(amount) == 1f, "An endpoint or invalid-input correction changed.");
            for (int i = 0; i <= 10000; i++)
            {
                float gain = Dsp.RingGain(i / 10000f);
                Require(float.IsFinite(gain) && gain > .99f && gain < 1.5f, "Correction left its calibrated range.");
            }
        });
        check("RingModulator gain halves share midpoint value and physical slope", () =>
        {
            const float h = .001f;
            double middle = Dsp.RingGain(.5f);
            double left = (3 * middle - 4 * Dsp.RingGain(.5f-h) + Dsp.RingGain(.5f-2*h)) / (2*h);
            double right = (-3 * middle + 4 * Dsp.RingGain(.5f+h) - Dsp.RingGain(.5f+2*h)) / (2*h);
            Require(Math.Abs(left-right) < .004, "The midpoint slope is discontinuous.");
            Require(Math.Abs(Dsp.RingGain(MathF.BitDecrement(.5f))-Dsp.RingGain(MathF.BitIncrement(.5f))) < 3e-6,
                "The midpoint value is discontinuous.");
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
            Require(Value(engine, "Gain") == .91f && Value(engine, "Makeup") == 1.8f,
                "The accepted full-strength gain or modulation makeup changed.");
        });
        check("RingModulator output scalar cannot leave a hidden clean branch or alter modulation history", () =>
        {
            var a = NewEngine(22050); var b = NewEngine(22050); var muted = NewEngine(22050);
            foreach (var engine in new[] { a, b, muted })
                engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.RingModulator, .5f);
            SetValue(b, "Gain", Value(b, "Gain")*.5f); SetValue(muted, "Gain", 0f);
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
            float expected = Value(engine, "Gain"); SetValue(engine, "Gain", 0f);
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Telephone, .5f);
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.RingModulator, .5f);
            Require(Value(engine, "Gain") == expected, "The previous effect left a stale ring cache.");
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
        foreach (VoiceRecord voice in Fixtures.Voices)
        {
            check($"RingModulator retains measured RMS level bounds for {voice.Name} at scale {voice.Scale:R}", () =>
            {
                Require(voice.Points[0].Amount == 0f && voice.Points[^1].Amount == 1f, "Missing measured endpoints.");
                double full = voice.Points[^1].ExpectedDeltaDb, previous = 0;
                foreach (Point point in voice.Points)
                {
                    double level = Level(voice, point);
                    Require(Math.Abs(level-point.ExpectedDeltaDb) < .0001, "Correction no longer matches measured PCM levels.");
                    Require(Math.Abs(level-point.Amount*full) <= voice.GuideBudgetDb, "The measured guide error increased.");
                    Require(Math.Sign(full)*(previous-level) <= voice.ReverseBudgetDb, "The measured reverse-step budget increased.");
                    previous = level;
                }
            });
        }
        check("RingModulator dense base RMS curve remains ordered with a centered level midpoint", () =>
        {
            VoiceRecord voice = Fixtures.Voices.Single(x => x.Group == "dense");
            double previous = 0;
            foreach (Point point in voice.Points)
            {
                double level = Level(voice, point);
                Require(previous-level < .04, "A large base-voice reverse step returned.");
                previous = level;
            }
            double midpoint = Level(voice, voice.Points.Single(p => p.Amount == .5f));
            double full = voice.Points[^1].ExpectedDeltaDb;
            Require(midpoint > .35*full && midpoint < .65*full, "The base midpoint left its useful level window.");
        });
        foreach (SyntheticRecord record in Fixtures.Synthetic)
        {
            check($"RingModulator preserves accepted full PCM at {record.Rate} Hz and scale {record.Scale:R}", () =>
            {
                float[] actual = Signal(record.Rate, record.Rate*3, record.Scale);
                NewEngine(record.Rate).ApplyEffect(actual, VoiceEffectType.RingModulator, 1f);
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

    private static double Level(VoiceRecord voice, Point point) => point.Amount <= .001f ? 0
        : point.UntrimmedRmsDbfs-voice.DryRmsDbfs+20*Math.Log10(Dsp.RingGain(point.Amount));
    private static float Value(AudioEffectsEngine engine, string name)
    {
        object state = RingField.GetValue(engine)!;
        return (float)state.GetType().GetField(name)!.GetValue(state)!;
    }
    private static void SetValue(AudioEffectsEngine engine, string name, float value)
    {
        object state = RingField.GetValue(engine)!; state.GetType().GetField(name)!.SetValue(state, value); RingField.SetValue(engine, state);
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
