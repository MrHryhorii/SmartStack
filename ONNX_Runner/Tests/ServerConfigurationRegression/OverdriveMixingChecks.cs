using System.Runtime.InteropServices;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class OverdriveMixingChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];

    /// <summary>Checks fixed wet processing, measured-power interpolation and request-local state.</summary>
    internal static void Run(Action<string, Action> check)
    {
        OverdriveCurveChecks.Run(check);
        OverdriveCharacterChecks.Run(check);
        foreach (int rate in Rates)
        {
            check($"Overdrive bypass and empty calls preserve PCM and state at {rate} Hz", () =>
            {
                var a = NewEngine(rate); var b = NewEngine(rate);
                foreach (float amount in new[] { 0f, .001f })
                {
                    float[] dry = Signal(rate, 257, 1), actual = (float[])dry.Clone();
                    a.ApplyEffect(actual, VoiceEffectType.Overdrive, amount);
                    Require(SameBits(dry, actual), "Bypass changed PCM.");
                }
                a.ApplyEffect(Span<float>.Empty, VoiceEffectType.Overdrive, .25f);
                float[] expected = Signal(rate, 257, 1), output = (float[])expected.Clone();
                a.ApplyEffect(output, VoiceEffectType.Overdrive, .5f);
                b.ApplyEffect(expected, VoiceEffectType.Overdrive, .5f);
                Require(SameBits(output, expected), "An empty call advanced processing state.");
            });
            check($"Overdrive uses the fixed wet endpoint for changing intensity and chunks at {rate} Hz", () =>
            {
                foreach (int size in new[] { 1, 37, rate/50, rate/2 })
                {
                    var actual = NewEngine(rate); var wet = NewEngine(rate);
                    for (int section = 0; section < 12; section++)
                    {
                        float amount = new[] { .0011f, .01f, .25f, .5f, .75f, 1f }[section%6];
                        float[] dry = Signal(rate, size, .2f+section*.1f);
                        float[] expected = (float[])dry.Clone(), output = (float[])dry.Clone();
                        wet.ApplyEffect(expected, VoiceEffectType.Overdrive, 1f);
                        PowerMatchedMixer.Mix(dry, expected, amount);
                        actual.ApplyEffect(output, VoiceEffectType.Overdrive, amount);
                        Require(SameBits(output, expected), "Intensity changed the wet kernel or mixer routing.");
                    }
                }
            });
            check($"Overdrive full wet output is independent of stream partitions at {rate} Hz", () =>
            {
                float[] whole = Signal(rate, rate, 1), split = (float[])whole.Clone();
                NewEngine(rate).ApplyEffect(whole, VoiceEffectType.Overdrive, 1);
                var engine = NewEngine(rate);
                for (int i = 0; i < split.Length; i += 37)
                    engine.ApplyEffect(split.AsSpan(i, Math.Min(37, split.Length-i)), VoiceEffectType.Overdrive, 1);
                Require(SameBits(whole, split), "Full wet state depends on buffer boundaries.");
            });
            check($"Overdrive follows measured endpoint power at 101 positions at {rate} Hz", () =>
            {
                float[] dry = Signal(rate, rate, 1), full = (float[])dry.Clone();
                NewEngine(rate).ApplyEffect(full, VoiceEffectType.Overdrive, 1);
                double dp = PowerMatcher.Measure(dry), wp = PowerMatcher.Measure(full);
                for (int i = 0; i <= 100; i++)
                {
                    float t = i/100f; float[] output = (float[])dry.Clone();
                    NewEngine(rate).ApplyEffect(output, VoiceEffectType.Overdrive, t);
                    double target = PowerMatcher.Target(dp, wp, t);
                    Require(Math.Abs(10*Math.Log10(PowerMatcher.Measure(output)/target)) < .00001,
                        "The mixed buffer missed its endpoint-power target.");
                }
            });
            check($"Overdrive hot processing allocates zero managed bytes at {rate} Hz", () =>
            {
                var engine = NewEngine(rate); float[] block = Signal(rate, 257, 1);
                for (int i = 0; i < 256; i++) engine.ApplyEffect(block, VoiceEffectType.Overdrive, .5f);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 256; i++) engine.ApplyEffect(block, VoiceEffectType.Overdrive, .5f);
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "The warmed pool path allocated managed memory.");
            });
            check($"Overdrive tiny intensity and edge-band processing remain bounded at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate, 1), actual = (float[])input.Clone();
                NewEngine(rate).ApplyEffect(actual, VoiceEffectType.Overdrive, .0011f);
                double error = 0, power = 0;
                for (int i = 0; i < input.Length; i++) { error += Math.Pow(actual[i]-input[i], 2); power += input[i]*(double)input[i]; }
                Require(error < power*.0001, "Tiny intensity introduced a large discontinuity.");
                foreach (float amount in new[] { .0011f, .25f, .5f, 1f })
                {
                    float[] edge = Enumerable.Range(0, rate/4).Select(i => .1f*(float)Math.Sin(2*Math.PI*.499*i)).ToArray();
                    NewEngine(rate).ApplyEffect(edge, VoiceEffectType.Overdrive, amount);
                    Require(edge.All(x => float.IsFinite(x) && Math.Abs(x) < 1), "Unbounded edge-band output.");
                }
            });
        }
        check("Overdrive parallel requests and reset retain independent state", () =>
        {
            float[] input = Signal(22050, 22050, 1), expected = (float[])input.Clone();
            NewEngine(22050).ApplyEffect(expected, VoiceEffectType.Overdrive, .5f);
            Parallel.For(0, 24, i =>
            {
                var engine = NewEngine(22050); float[] output = (float[])input.Clone();
                engine.ApplyEffect(output, VoiceEffectType.Overdrive, .25f);
                engine.Reset(123456); input.CopyTo(output, 0);
                engine.ApplyEffect(output, VoiceEffectType.Overdrive, .5f);
                Require(SameBits(output, expected), "Requests shared state or reset retained history.");
            });
        });
        check("Overdrive silence retains the full thermal drift floor", () =>
        {
            foreach (float amount in new[] { .0011f, .25f, .5f, 1f })
            {
                float[] silence = new float[22050]; NewEngine(22050).ApplyEffect(silence, VoiceEffectType.Overdrive, amount);
                Require(silence.All(x => float.IsFinite(x) && Math.Abs(x) < .0001f), "Excessive generated silence output.");
            }
        });
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
