using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class InnerVoiceRegressionProgram
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly Corpus Data = Load();
    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        Check("Allocation accounting observes its positive control without background GC", () =>
        {
            Require(GC.GetConfigurationVariables().TryGetValue("ConcurrentGC", out object? value) && value is false, "Unexpected GC mode.");
            GC.KeepAlive(Allocate()); long before = GC.GetAllocatedBytesForCurrentThread(); object control = Allocate();
            Require(GC.GetAllocatedBytesForCurrentThread() > before, "The allocation counter missed its positive control."); GC.KeepAlive(control);
        });
        foreach (int rate in Rates)
        {
            Check($"InnerVoice retains finite speech and tail across streaming partitions at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate/4);
                foreach (float amount in new[] { .0011f, .01f, .25f, .5f, .75f, 1f })
                {
                    float[] output = Process(input, rate, amount, tailSeconds: 1);
                    Require(output.All(x => float.IsFinite(x) && Math.Abs(x) < 2f), "Unbounded PCM.");
                    Require(SameBits(output, Process(input, rate, amount, block: 137, tailSeconds: 1)), "Partition-dependent speech or tail.");
                }
            });
            Check($"InnerVoice bypass and intensity changes preserve streaming state at {rate} Hz", () =>
            {
                var a = new SpatialEffectsEngine(rate); var b = new SpatialEffectsEngine(rate); float[] input = Signal(rate, rate/10);
                foreach (float amount in new[] { .5f, 0f, .001f, .0011f, .25f, .4999f, .5f, .5001f, .75f, 1f, .25f })
                {
                    float[] whole = (float[])input.Clone(), split = (float[])input.Clone();
                    a.ApplyEnvironment(whole, SpatialEnvironment.InnerVoice, amount);
                    for (int i = 0; i < split.Length; i += 137)
                        b.ApplyEnvironment(split.AsSpan(i, Math.Min(137, split.Length-i)), SpatialEnvironment.InnerVoice, amount);
                    Require(SameBits(whole, split), "Changing intensity depends on partition size.");
                    if (amount <= .001f) Require(SameBits(whole, input), "Warm bypass changed samples.");
                }
            });
            Check($"First active InnerVoice intensity approaches clean PCM at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate), output = Process(input, rate, .0011f);
                double error = 0;
                for (int i = 0; i < input.Length; i++) error += Math.Pow(output[i]-input[i], 2);
                Require(error/Energy(input) < .0000035, "Abrupt coloration or saturation at the first active intensity.");
            });
            Check($"InnerVoice processing and coefficient preparation allocate zero bytes at {rate} Hz", () =>
            {
                var engine = new SpatialEffectsEngine(rate); float[] block = Signal(rate, 257);
                for (int i = 0; i < 256; i++) engine.ApplyEnvironment(block, SpatialEnvironment.InnerVoice, .01f+i%99/100f);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 256; i++) engine.ApplyEnvironment(block, SpatialEnvironment.InnerVoice, .01f+i%99/100f);
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "Hot preparation or processing allocated memory.");
            });
        }
        foreach (Reference reference in Data.Full)
            Check($"InnerVoice keeps captured legacy full speech and tail at {reference.Rate} Hz, scale {reference.Scale:R}", () => Match(reference));
        foreach (Reference reference in Data.Routing)
            Check($"InnerVoice keeps approved intermediate speech and tail at {reference.Rate} Hz, intensity {reference.Amount:R}", () => Match(reference));
        foreach (int rate in new[] { 8000, 48000 })
        foreach (float amount in new[] { .25f, .5f, 1f })
        {
            Check($"InnerVoice keeps the 8 ms tap and early presence at {rate} Hz, intensity {amount:R}", () =>
            {
                float[] impulse = new float[rate/50]; impulse[0] = .008f;
                float[] output = Process(impulse, rate, amount);
                int tap = (int)(.008f*rate);
                Require(Math.Abs(output[tap]/output[0]-.15*amount*(2-amount)) < .00005, "Delay timing or presence depth changed.");
            });
        }
        Check("InnerVoice exposes partial upper-band coloration at 25 percent", () =>
        {
            const int rate = 22050;
            float[] input = Enumerable.Range(0, rate*3).Select(i => .008f*(float)Math.Sin(2*Math.PI*6000*i/rate)).ToArray();
            float[] output = Process(input, rate, .25f);
            double gain = 10*Math.Log10(Energy(output.AsSpan(rate*2))/Energy(input.AsSpan(rate*2)));
            Require(gain > -3.1 && gain < -2.8, "The approved early upper-band coloration changed.");
        });
        Check("InnerVoice exposes the approved reverb tail at 25 and 50 percent", () =>
        {
            const int rate = 22050;
            float[] input = Enumerable.Range(0, rate).Select(i => .18f*(float)Math.Sin(2*Math.PI*250*i/rate)).ToArray();
            foreach ((float amount, double expected) in new[] { (.25f, -38.763301), (.5f, -33.795087) })
            {
                float[] output = Process(input, rate, amount, tailSeconds: 4);
                double level = 10*Math.Log10(Energy(output.AsSpan(input.Length+rate/50))/Energy(input));
                Require(Math.Abs(level-expected) < .03, "The early room-tail contribution changed.");
            }
        });
        Check("InnerVoice has no outer clean branch after its internal output controls", () =>
        {
            var engine = new SpatialEffectsEngine(22050); engine.ApplyEnvironment(Span<float>.Empty, SpatialEnvironment.InnerVoice, .5f);
            foreach (string name in new[] { "_innerVoiceLpCoeff", "_innerVoicePresenceGain", "_innerVoiceReverbGain" })
                typeof(SpatialEffectsEngine).GetField(name, BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(engine, 0f);
            float[] output = Signal(22050, 22050); engine.ApplyEnvironment(output, SpatialEnvironment.InnerVoice, .5f);
            Require(output.All(x => x == 0), "A separate clean branch survived the internal controls.");
        });
        Check("Returning to InnerVoice invalidates its prepared parameter cache", () =>
        {
            var engine = new SpatialEffectsEngine(22050); engine.ApplyEnvironment(Span<float>.Empty, SpatialEnvironment.InnerVoice, .5f);
            engine.ApplyEnvironment(Span<float>.Empty, SpatialEnvironment.Muffled, .25f);
            float[] input = Signal(22050, 22050), actual = (float[])input.Clone();
            engine.ApplyEnvironment(actual, SpatialEnvironment.InnerVoice, .5f);
            Require(SameBits(actual, Process(input, 22050, .5f)), "Returning to InnerVoice retained stale parameters.");
        });
        Check("Parallel InnerVoice requests keep independent histories", () =>
        {
            float[] source = Signal(22050, 22050), expected = Process(source, 22050, .5f, tailSeconds: 1); bool[] passed = new bool[24];
            Parallel.For(0, passed.Length, i => passed[i] = SameBits(expected, Process(source, 22050, .5f, block: 37+i, tailSeconds: 1)));
            Require(passed.All(x => x), "Shared per-request state.");
        });
        Check("InnerVoice preserves silence across the intensity range", () =>
        {
            for (int i = 0; i <= 20; i++) Require(Process(new float[22050], 22050, i/20f).All(x => x == 0), "Generated audio from silence.");
        });
        Check("InnerVoice fixtures retain every verified rate and legacy level", () =>
        {
            Require(Data.Full.Length == 27 && Data.Routing.Length == 63, "Incomplete fixture corpus.");
            foreach (int rate in Rates)
            {
                Require(Data.Full.Count(x => x.Rate == rate) == 3, "Missing legacy level probes.");
                Require(Data.Routing.Count(x => x.Rate == rate) == 7, "Missing intermediate intensity probes.");
            }
        });
        Console.WriteLine($"Result: {_checks-_failures}/{_checks} InnerVoice checks passed");
        return _failures == 0 ? 0 : 1;
    }

    private static void Match(Reference reference)
    {
        int length = reference.Rate*3;
        float[] source = Signal(reference.Rate, length).Select(x => x*reference.Scale).ToArray();
        float[] output = Process(source, reference.Rate, reference.Amount, tailSeconds: 4);
        double speech = 10*Math.Log10(Energy(output.AsSpan(0,length))/length);
        double tail = 10*Math.Log10(Energy(output.AsSpan(length))/Energy(source));
        Require(Math.Abs(speech-reference.SpeechRmsDbfs) < .002, "Captured speech level changed.");
        Require(Math.Abs(tail-reference.TailEnergyRelativeDb) < .01, "Captured tail energy changed.");
        Require(reference.Indices.Length >= 32 && reference.Samples.Length == reference.Indices.Length, "Incomplete PCM reference.");
        for (int i = 0; i < reference.Indices.Length; i++)
            Require(Math.Abs(output[reference.Indices[i]]-reference.Samples[i]) < 3e-6, "Captured speech or tail waveform changed.");
    }

    private static float[] Process(float[] source, int rate, float amount, int block = int.MaxValue, int tailSeconds = 0)
    {
        float[] output = new float[source.Length+rate*tailSeconds]; source.CopyTo(output, 0);
        var engine = new SpatialEffectsEngine(rate);
        for (int offset = 0; offset < output.Length; offset += Math.Min(block, output.Length-offset))
            engine.ApplyEnvironment(output.AsSpan(offset, Math.Min(block, output.Length-offset)), SpatialEnvironment.InnerVoice, amount);
        return output;
    }
    private static void Check(string name, Action action)
    {
        _checks++;
        try { action(); }
        catch (Exception error) { _failures++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
    }
    private static float[] Signal(int rate, int count) => Enumerable.Range(0, count).Select(i =>
    {
        double t = i/(double)rate;
        return (float)((.18*Math.Sin(2*Math.PI*110*t)+.08*Math.Sin(2*Math.PI*330*t)+.05*Math.Sin(2*Math.PI*1700*t)
            +.03*Math.Sin(2*Math.PI*Math.Min(3200, rate*.3)*t))*(.7+.3*Math.Sin(2*Math.PI*.73*t)));
    }).ToArray();
    private static double Energy(ReadOnlySpan<float> samples) { double sum = 0; foreach (float x in samples) sum += x*(double)x; return sum; }
    private static bool SameBits(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static object Allocate() => new byte[37];
    private static Corpus Load()
    {
        using Stream stream = typeof(InnerVoiceRegressionProgram).Assembly.GetManifestResourceStream("InnerVoiceFixtures.json")
            ?? throw new InvalidDataException("Missing InnerVoice fixtures.");
        return JsonSerializer.Deserialize<Corpus>(stream) ?? throw new InvalidDataException("Invalid InnerVoice fixtures.");
    }
    private sealed record Corpus(Reference[] Full, Reference[] Routing);
    private sealed record Reference(int Rate, float Scale, float Amount, double SpeechRmsDbfs, double TailEnergyRelativeDb, int[] Indices, float[] Samples);
}
