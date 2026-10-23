using System.Reflection;
using System.Runtime.InteropServices;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class OverdriveMixingChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly FieldInfo DriveField = typeof(AudioEffectsEngine)
        .GetField("_drive", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Checks only direct Overdrive processing, adaptive filtering and gradual saturation.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Overdrive correction retains unity endpoints and a bounded positive scalar", () =>
        {
            foreach (float amount in new[] { -1f, 0f, .001f, float.NaN, 1f, 2f, float.PositiveInfinity })
                Require(Dsp.DriveGain(amount) == 1f, "An endpoint or invalid-input correction changed.");
            for (int i = 0; i <= 10000; i++)
            {
                float gain = Dsp.DriveGain(i / 10000f);
                Require(float.IsFinite(gain) && gain > .65f && gain <= 1.001f, "Correction left its calibrated range.");
            }
        });
        check("Overdrive gain halves share midpoint value and physical slope", () =>
        {
            const float h = .001f;
            double middle = Dsp.DriveGain(.5f);
            double left = (3 * middle - 4 * Dsp.DriveGain(.5f-h) + Dsp.DriveGain(.5f-2*h)) / (2*h);
            double right = (-3 * middle + 4 * Dsp.DriveGain(.5f+h) - Dsp.DriveGain(.5f+2*h)) / (2*h);
            Require(Math.Abs(left-right) < .004, "The midpoint slope is discontinuous.");
            Require(Math.Abs(Dsp.DriveGain(MathF.BitDecrement(.5f))-Dsp.DriveGain(MathF.BitIncrement(.5f))) < 3e-6,
                "The midpoint value is discontinuous.");
        });
        check("Overdrive saturation approaches identity and retains the accepted full operating point", () =>
        {
            var engine = NewEngine(22050);
            foreach (float amount in new[] { .0011f, .01f, .1f, .25f, .5f, .75f, 1f })
            {
                engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Overdrive, amount);
                float strength = amount*(2f-amount);
                Require(Math.Abs(Value(engine, "Drive")-5.808956f*strength) < 1e-6,
                    "Saturation drive does not approach zero with intensity.");
                Require(Math.Abs(Value(engine, "BiasScale")-.06f*strength*strength) < 1e-8,
                    "Normalized bias no longer fades with intensity.");
            }
            Require(Value(engine, "Gain") == .5347782f && Value(engine, "Drive") == 5.808956f,
                "The accepted full-strength trim or drive changed.");
            Require(Value(engine, "BiasScale") == .06f, "The full thermal drift changed.");
        });
        check("Overdrive output scalar cannot leave a hidden clean branch or alter saturation history", () =>
        {
            var a = NewEngine(22050); var b = NewEngine(22050); var muted = NewEngine(22050);
            foreach (var engine in new[] { a, b, muted })
                engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Overdrive, .5f);
            SetValue(b, "Gain", Value(b, "Gain")*.5f); SetValue(muted, "Gain", 0f);
            float[] first = Signal(22050, 22050, 1f), second = (float[])first.Clone(), zero = (float[])first.Clone();
            a.ApplyEffect(first, VoiceEffectType.Overdrive, .5f);
            b.ApplyEffect(second, VoiceEffectType.Overdrive, .5f);
            muted.ApplyEffect(zero, VoiceEffectType.Overdrive, .5f);
            for (int i = 0; i < first.Length; i++)
            {
                Require(second[i] == first[i]*.5f, "Output gain changed state or internal processing.");
                Require(zero[i] == 0f, "A separate clean branch survived the final scalar.");
            }
        });
        check("Overdrive preparation is invalidated when returning from another effect", () =>
        {
            var engine = NewEngine(22050);
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Overdrive, .5f);
            float expected = Value(engine, "Gain"); SetValue(engine, "Gain", 0f);
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Telephone, .5f);
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Overdrive, .5f);
            Require(Value(engine, "Gain") == expected, "The previous effect left a stale drive cache.");
            float[] actual = Signal(22050, 11025, 1f), fresh = (float[])actual.Clone();
            engine.ApplyEffect(actual, VoiceEffectType.Overdrive, .5f);
            NewEngine(22050).ApplyEffect(fresh, VoiceEffectType.Overdrive, .5f);
            Require(SameBits(actual, fresh), "An empty effect switch changed the drive path.");
        });
        check("Overdrive parallel requests retain independent phase, noise, filters and gain", () =>
        {
            float[] input = Signal(22050, 22050, 1f), expected = (float[])input.Clone();
            NewEngine(22050).ApplyEffect(expected, VoiceEffectType.Overdrive, .5f);
            bool[] passed = new bool[24];
            Parallel.For(0, passed.Length, i =>
            {
                float[] actual = (float[])input.Clone(); var engine = NewEngine(22050);
                int block = 37+i;
                for (int offset = 0; offset < actual.Length; offset += block)
                    engine.ApplyEffect(actual.AsSpan(offset, Math.Min(block, actual.Length-offset)), VoiceEffectType.Overdrive, .5f);
                passed[i] = SameBits(actual, expected);
            });
            Require(passed.All(x => x), "Parallel requests shared DSP state.");
        });
        OverdriveCurveChecks.Run(check);
        OverdriveCharacterChecks.Run(check);
        foreach (int rate in Rates)
        {
            check($"Overdrive remains finite and independent of stream partitions at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate/8, 1f);
                foreach (float amount in new[] { .0011f, .01f, .25f, .5f, .75f, 1f })
                {
                    float[] whole = (float[])input.Clone(), split = (float[])input.Clone();
                    NewEngine(rate).ApplyEffect(whole, VoiceEffectType.Overdrive, amount);
                    var engine = NewEngine(rate);
                    for (int i = 0; i < split.Length; i += 37)
                        engine.ApplyEffect(split.AsSpan(i, Math.Min(37, split.Length-i)), VoiceEffectType.Overdrive, amount);
                    Require(SameBits(whole, split), "Streaming boundaries changed output.");
                    Require(whole.All(x => float.IsFinite(x) && Math.Abs(x) < 2), "Invalid direct output.");
                }
            });
            check($"Overdrive changing intensity preserves streaming, reset and warm bypass at {rate} Hz", () =>
            {
                float[] amounts = [0f, .001f, .0011f, .01f, .25f, .4995f, .5f, .5005f, .75f, 1f, .45f];
                int size = rate/20;
                float[] whole = Signal(rate, size*amounts.Length, 1f), split = (float[])whole.Clone();
                var a = NewEngine(rate); var b = NewEngine(rate);
                for (int section = 0; section < amounts.Length; section++)
                {
                    a.ApplyEffect(whole.AsSpan(section*size, size), VoiceEffectType.Overdrive, amounts[section]);
                    for (int i = 0; i < size; i += 257)
                        b.ApplyEffect(split.AsSpan(section*size+i, Math.Min(257, size-i)), VoiceEffectType.Overdrive, amounts[section]);
                }
                Require(SameBits(whole, split), "Changing intensity depends on partition size.");
                foreach (float amount in new[] { 0f, .001f })
                {
                    float[] clean = Signal(rate, 257, 1f), actual = (float[])clean.Clone();
                    a.ApplyEffect(actual, VoiceEffectType.Overdrive, amount);
                    Require(SameBits(clean, actual), "Warm bypass changed samples.");
                }
                float[] expected = Signal(rate, rate/8, 1f); NewEngine(rate).ApplyEffect(expected, VoiceEffectType.Overdrive, .5f);
                a.Reset(123456); float[] reset = Signal(rate, rate/8, 1f); a.ApplyEffect(reset, VoiceEffectType.Overdrive, .5f);
                Require(SameBits(reset, expected), "Reset retained prior phase, filters or cached parameters.");
            });
            check($"Overdrive preparation and hot processing allocate zero managed bytes at {rate} Hz", () =>
            {
                var engine = NewEngine(rate); float[] block = Signal(rate, 257, 1f);
                for (int i = 0; i < 256; i++) engine.ApplyEffect(block, VoiceEffectType.Overdrive, .01f+i%99/100f);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 256; i++) engine.ApplyEffect(block, VoiceEffectType.Overdrive, .01f+i%99/100f);
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "Preparation or processing allocated managed memory.");
            });
            check($"Overdrive first active intensity approaches clean audio at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate, 1f), actual = (float[])input.Clone();
                NewEngine(rate).ApplyEffect(actual, VoiceEffectType.Overdrive, .0011f);
                double error = 0, power = 0;
                for (int i = 0; i < input.Length; i++) { error += Math.Pow(actual[i]-input[i], 2); power += input[i]*(double)input[i]; }
                Require(error < power*.0001, "Tiny intensity introduced a large filter or saturation discontinuity.");
            });
        }
        foreach (int rate in new[] { 16000, 22050, 48000 })
        {
            check($"Overdrive upper filter progressively attenuates the available band at {rate} Hz", () =>
            {
                double previous = double.PositiveInfinity;
                foreach (float amount in new[] { .01f, .25f, .5f, .75f, 1f })
                {
                    var engine = NewEngine(rate);
                    engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Overdrive, amount);
                    var filters = (NAudio.Dsp.BiQuadFilter[])typeof(AudioEffectsEngine)
                        .GetField("_filters", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
                    float[] tone = Enumerable.Range(0, rate*2)
                        .Select(i => (float)Math.Sin(2*Math.PI*(rate*.45)*i/rate)).ToArray();
                    for (int i = 0; i < tone.Length; i++) tone[i] = filters[1].Transform(tone[i]);
                    double level = RmsDb(tone.AsSpan(rate));
                    Require(level <= previous+.0001, "The upper-band attenuation moved backwards.");
                    previous = level;
                }
                Require(previous < (rate == 48000 ? -20 : -6), "The full upper cutoff no longer limits the input band.");
            });
        }
        foreach (int rate in new[] { 8000, 48000, 192000 })
        {
            check($"Overdrive boundary filters remain bounded on edge-band input at {rate} Hz", () =>
            {
                foreach (float amount in new[] { .0011f, .01f, .5f, 1f })
                {
                    float[] edge = Enumerable.Range(0, rate*2)
                        .Select(i => .1f*(float)Math.Sin(2*Math.PI*.499*i)).ToArray();
                    NewEngine(rate).ApplyEffect(edge, VoiceEffectType.Overdrive, amount);
                    Require(edge.All(x => float.IsFinite(x) && Math.Abs(x) < .5f), "A boundary filter became unstable.");
                }
            });
        }
        check("Overdrive thermal drift stays below the declared silence floor", () =>
        {
            for (int i = 1; i <= 20; i++)
            {
                float[] silence = new float[22050]; NewEngine(22050).ApplyEffect(silence, VoiceEffectType.Overdrive, i/20f);
                Require(silence.All(x => float.IsFinite(x) && Math.Abs(x) < .0001f), "Excessive signal was generated from silence.");
            }
        });
    }

    private static float Value(AudioEffectsEngine engine, string name)
    {
        object state = DriveField.GetValue(engine)!;
        return (float)state.GetType().GetField(name)!.GetValue(state)!;
    }
    private static void SetValue(AudioEffectsEngine engine, string name, float value)
    {
        object state = DriveField.GetValue(engine)!; state.GetType().GetField(name)!.SetValue(state, value); DriveField.SetValue(engine, state);
    }
    internal static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456); return engine;
    }
    internal static float[] Signal(int rate, int count, float scale)
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
    internal static double RmsDb(ReadOnlySpan<float> samples)
    {
        double power = 0; foreach (float sample in samples) power += sample*(double)sample; return 10*Math.Log10(power/samples.Length);
    }
    private static bool SameBits(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
