using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class MuffledRegressionProgram
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly List<object> Checks = [];
    private static readonly Corpus Data = Load();
    private static int Main() => Verify();

    private static float[] Process(float[] source, int rate, float amount, int block = int.MaxValue)
    {
        float[] output = (float[])source.Clone(); var engine = new SpatialEffectsEngine(rate);
        for (int offset = 0; offset < output.Length; offset += Math.Min(block, output.Length-offset))
            engine.ApplyEnvironment(output.AsSpan(offset, Math.Min(block, output.Length-offset)), SpatialEnvironment.Muffled, amount);
        return output;
    }

    private static bool Check(string name, Action action)
    {
        try { action(); Checks.Add(new { Name = name, Passed = true }); return true; }
        catch (Exception error) { Checks.Add(new { Name = name, Passed = false }); Console.Error.WriteLine($"FAIL {name}: {error.Message}"); return false; }
    }

    private static int Verify()
    {
        int failures = 0;
        void Test(string name, Action action)
        {
            if (!Check(name, action)) failures++;
        }
        Test("Allocation host disables background GC and observes the positive control", () =>
        {
            Require(GC.GetConfigurationVariables().TryGetValue("ConcurrentGC", out object? v) && v is false, "Unexpected GC mode.");
            GC.KeepAlive(Allocate()); long before = GC.GetAllocatedBytesForCurrentThread(); object value = Allocate();
            Require(GC.GetAllocatedBytesForCurrentThread() > before, "Allocation counter missed the positive control."); GC.KeepAlive(value);
        });
        foreach (int rate in Rates)
        {
            Test($"Muffled stays finite and independent of partitions at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate/8);
                foreach (float amount in new[] { .0011f, .01f, .25f, .5f, .75f, 1f })
                {
                    float[] output = Process(input, rate, amount);
                    Require(output.All(x => float.IsFinite(x) && Math.Abs(x) < 2f), "Unbounded PCM.");
                    Require(SameBits(output, Process(input, rate, amount, block: 37)), "Partition-dependent PCM.");
                }
            });
            Test($"Muffled bypass and changing intensity retain the same streaming state at {rate} Hz", () =>
            {
                var a = new SpatialEffectsEngine(rate); var b = new SpatialEffectsEngine(rate);
                float[] input = Signal(rate, rate/8);
                foreach (float amount in new[] { .5f, 0f, .001f, .0011f, .25f, .4999f, .5f, .5001f, .75f, 1f, .25f })
                {
                    float[] whole = (float[])input.Clone(), split = (float[])input.Clone();
                    a.ApplyEnvironment(whole, SpatialEnvironment.Muffled, amount);
                    for (int i = 0; i < split.Length; i += 137)
                        b.ApplyEnvironment(split.AsSpan(i, Math.Min(137, split.Length-i)), SpatialEnvironment.Muffled, amount);
                    Require(SameBits(whole, split), "Changing intensity depends on partition size.");
                    if (amount <= .001f) Require(SameBits(input, whole), "Warm bypass changed samples.");
                }
            });
            Test($"First active Muffled intensity approaches clean PCM at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate), output = Process(input, rate, .0011f);
                double error = 0, power = 0;
                for (int i = 0; i < input.Length; i++) { error += Math.Pow(output[i]-input[i], 2); power += Math.Pow(input[i], 2); }
                Require(error < power*.0001, "Abrupt filtering or saturation at the first active intensity.");
            });
            Test($"Muffled hot processing and coefficient preparation allocate zero bytes at {rate} Hz", () =>
            {
                var engine = new SpatialEffectsEngine(rate); float[] block = Signal(rate, 257);
                for (int i = 0; i < 256; i++) engine.ApplyEnvironment(block, SpatialEnvironment.Muffled, .01f+i%99/100f);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 256; i++) engine.ApplyEnvironment(block, SpatialEnvironment.Muffled, .01f+i%99/100f);
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "Hot preparation allocated memory.");
            });
        }
        foreach (int rate in new[] { 8000, 22050, 48000 })
        {
            Test($"Full Muffled keeps the fixed 450 Hz filter characteristic at {rate} Hz", () =>
            {
                double low = ToneGain(rate, 100, 1), corner = ToneGain(rate, 450, 1), octave = ToneGain(rate, 900, 1);
                Require(Math.Abs(low) < .1, "Unexpected low-band gain correction.");
                Require(Math.Abs(corner+6.0232) < .06, "The two 450 Hz filter corners changed.");
                Require(octave < -24, "The full upper-band attenuation changed.");
            });
            foreach (double hz in new[] { 100d, 450d, 900d, 1800d, 3000d })
            {
                Test($"Muffled attenuation grows at 21 intensities for {hz} Hz at Fs {rate} Hz", () =>
                {
                    double previous = 0;
                    for (int i = 0; i <= 20; i++)
                    {
                        double gain = ToneGain(rate, hz, i/20f);
                        Require(gain <= previous+.002, $"Attenuation reversed: {gain:R} after {previous:R}."); previous = gain;
                    }
                });
            }
        }
        foreach (FullReference reference in Data.Full)
        {
            Test($"Muffled keeps legacy full PCM at {reference.Rate} Hz and scale {reference.Scale:R}", () =>
            {
                float[] output = Process(Signal(reference.Rate, reference.Rate*3).Select(x => x*reference.Scale).ToArray(), reference.Rate, 1f);
                Require(Math.Abs(Rms(output)-reference.RmsDbfs) < .002, "The legacy full level changed.");
                Require(reference.Indices.Length == 16 && reference.Samples.Length == 16, "Incomplete legacy PCM reference.");
                for (int i = 0; i < reference.Indices.Length; i++)
                    Require(Math.Abs(output[reference.Indices[i]]-reference.Samples[i]) < 3e-6, "The legacy full waveform changed.");
            });
        }
        foreach (RoutingReference reference in Data.Routing)
        {
            Test($"Muffled matches captured intermediate PCM at {reference.Rate} Hz and scale {reference.Scale:R}", () =>
            {
                Require(reference.Points.Length == 21, "Incomplete routing reference.");
                foreach (LevelPoint point in reference.Points)
                {
                    float[] input = Signal(reference.Rate, reference.Rate*3).Select(x => x*reference.Scale).ToArray();
                    Require(Math.Abs(Rms(Process(input, reference.Rate, point.Amount))-point.RmsDbfs) < .002,
                        "Intermediate processing differs from the independently captured PCM.");
                }
            });
        }
        foreach (VoiceReference voice in Data.Voices)
        {
            Test($"Measured Muffled RMS attenuation stays ordered for {voice.Name} at {voice.Rate} Hz, scale {voice.Scale:R}, dense {voice.Dense}", () =>
            {
                Require(voice.Points[0].Amount == 0 && voice.Points[^1].Amount == 1, "Missing measured endpoints.");
                double previous = voice.Points[0].RmsDbfs;
                foreach (VoicePoint point in voice.Points)
                {
                    Require(double.IsFinite(point.RmsDbfs) && point.RmsDbfs <= previous+.000001,
                        "The measured no-gain attenuation is no longer ordered.");
                    previous = point.RmsDbfs;
                }
            });
        }
        Test("Muffled fixture coverage retains the measured base and holdout curves", () =>
        {
            Require(Data.Full.Length == 27 && Data.Routing.Length == 9 && Data.Voices.Length == 55, "Incomplete fixture corpus.");
            VoiceReference primary = Data.Voices.Single(x => x.Dense);
            Require(primary.Points.Length == 203, "The dense base curve is incomplete.");
            VoicePoint midpoint = primary.Points.Single(x => x.Amount == .5f);
            Require(midpoint.RmsDbfs > primary.Points[^1].RmsDbfs && midpoint.RmsDbfs < primary.Points[0].RmsDbfs,
                "The base midpoint left its endpoint window.");
            Require(midpoint.LegacyRmsDbfs < primary.Points[^1].LegacyRmsDbfs, "The recorded preceding midpoint dip changed.");
        });
        Test("Muffled midpoint attenuation has no separate clean high-frequency branch", () =>
        {
            Require(ToneGain(22050, 3000, .5f) < -34, "Clean high frequencies bypassed the moving low-pass filters.");
        });
        Test("Returning to Muffled invalidates the prepared filter cache", () =>
        {
            var engine = new SpatialEffectsEngine(22050);
            engine.ApplyEnvironment(Span<float>.Empty, SpatialEnvironment.Muffled, .5f);
            engine.ApplyEnvironment(Span<float>.Empty, SpatialEnvironment.LivingRoom, .25f);
            float[] input = Signal(22050, 22050), actual = (float[])input.Clone();
            engine.ApplyEnvironment(actual, SpatialEnvironment.Muffled, .5f);
            Require(SameBits(actual, Process(input, 22050, .5f)), "An empty environment switch left stale Muffled parameters.");
        });
        Test("Parallel Muffled requests have independent filters and parameters", () =>
        {
            float[] source = Signal(22050, 22050), expected = Process(source, 22050, .5f);
            bool[] passed = new bool[24];
            Parallel.For(0, passed.Length, i => passed[i] = SameBits(expected, Process(source, 22050, .5f, block: i+37)));
            Require(passed.All(x => x), "Shared request state.");
        });
        Test("Muffled preserves digital silence throughout the intensity range", () =>
        {
            for (int i = 0; i <= 20; i++) Require(Process(new float[22050], 22050, i/20f).All(x => x == 0), "Generated sound from silence.");
        });
        Console.WriteLine($"Result: {Checks.Count-failures}/{Checks.Count} Muffled checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static double ToneGain(int rate, double hz, float amount)
    {
        float[] tone = Enumerable.Range(0, rate*2).Select(i => .001f*(float)Math.Sin(2*Math.PI*hz*i/rate)).ToArray();
        float[] output = Process(tone, rate, amount);
        double dry = 0, wet = 0;
        for (int i = rate; i < tone.Length; i++) { dry += tone[i]*(double)tone[i]; wet += output[i]*(double)output[i]; }
        return 10*Math.Log10(wet/dry);
    }

    private static float[] Signal(int rate, int count) => Enumerable.Range(0, count).Select(i =>
    {
        double t = i/(double)rate;
        return (float)((.18*Math.Sin(2*Math.PI*110*t)+.08*Math.Sin(2*Math.PI*330*t)+.05*Math.Sin(2*Math.PI*1700*t)
            +.03*Math.Sin(2*Math.PI*Math.Min(3200, rate*.3)*t))*(.7+.3*Math.Sin(2*Math.PI*.73*t)));
    }).ToArray();
    private static double Rms(ReadOnlySpan<float> samples)
    {
        double power = 0; foreach (float x in samples) power += x*(double)x;
        return 10*Math.Log10(power/samples.Length);
    }
    private static Corpus Load()
    {
        using Stream stream = typeof(MuffledRegressionProgram).Assembly.GetManifestResourceStream("MuffledFixtures.json")
            ?? throw new InvalidDataException("Missing Muffled fixtures.");
        return JsonSerializer.Deserialize<Corpus>(stream) ?? throw new InvalidDataException("Invalid Muffled fixtures.");
    }
    private static bool SameBits(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static object Allocate() => new byte[37];
    private sealed record Corpus(FullReference[] Full, RoutingReference[] Routing, VoiceReference[] Voices);
    private sealed record FullReference(int Rate, float Scale, double RmsDbfs, int[] Indices, float[] Samples);
    private sealed record RoutingReference(int Rate, float Scale, LevelPoint[] Points);
    private sealed record LevelPoint(float Amount, double RmsDbfs);
    private sealed record VoiceReference(string Name, float Scale, int Rate, bool Dense, VoicePoint[] Points);
    private sealed record VoicePoint(float Amount, double RmsDbfs, double LegacyRmsDbfs);
}
