using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;
using ONNX_Runner.Services.Spatial;

public static class LivingRoomChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000, 192000];
    private static readonly SpatialReverbParameters Profile = new(.9766f, 1, .3162f, .001f, 1, .5f, .1f, 1,
        .2051f, .003f, .2805f, .004f, .25f, 0, .9943f, 5000, 250, true);
    private static readonly PropertyInfo QuietWindow = typeof(SpatialEffectsEngine).GetProperty("TailSilenceWindowSamples", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly List<object> Results = [];
    private static int _failures;

    public static int Run(string? outputDirectory)
    {
        Results.Clear(); _failures = 0;
        foreach (int rate in Rates)
        {
            float[] input = Signal(rate, .35), padded = new float[input.Length + rate]; input.CopyTo(padded, 0);
            foreach (float amount in new[] { 0, .01f, .25f, .5f, .75f, 1 })
            {
                float[] whole = (float[])padded.Clone(), split = (float[])padded.Clone();
                var engine = new SpatialEffectsEngine(rate); engine.ApplyEnvironment(whole, SpatialEnvironment.LivingRoom, amount);
                var streaming = new SpatialEffectsEngine(rate);
                int[] sizes = [1, 13, 0, 127, 256, 4096, 777];
                for (int offset = 0, part = 0; offset < split.Length; part++)
                {
                    int length = Math.Min(sizes[part % sizes.Length], split.Length - offset);
                    streaming.ApplyEnvironment(split.AsSpan(offset, length), SpatialEnvironment.LivingRoom, amount); offset += length;
                }
                Check($"Chunk invariance/{rate}/{amount}", Bits(whole, split));
                Check($"Finite output/{rate}/{amount}", whole.All(float.IsFinite));
                if (amount == 0) Check($"Zero is exact bypass/{rate}", Bits(padded, whole));
                engine.Reset(); float[] repeated = (float[])padded.Clone(); engine.ApplyEnvironment(repeated, SpatialEnvironment.LivingRoom, amount);
                Check($"Reset/{rate}/{amount}", Bits(whole, repeated));
                float[] expected = new float[padded.Length]; var reference = new SpatialReverbKernel(rate, Profile);
                float strength = amount * (2 - amount), gain = 64 * strength;
                for (int i = 0; i < padded.Length; i++) expected[i] = padded[i] + reference.Process(padded[i]) * gain;
                Check($"Fixed profile and additive intensity/{rate}/{amount}", Bits(expected, whole));
            }
            float[] direct = new float[Math.Max(1, rate / 100)]; direct[0] = 2;
            new SpatialEffectsEngine(rate).ApplyEnvironment(direct, SpatialEnvironment.LivingRoom, 1);
            Check($"Direct speech and floating-point headroom retained/{rate}", direct[0] == 2);
            foreach (float amount in new[] { .25f, .5f, 1f })
            foreach (float volume in new[] { .1f, 1f, 2.5118864f })
                CheckTail(rate, amount, volume);
        }
        foreach (int rate in new[] { 8000, 22050, 48000 })
        foreach (SpatialEnvironment environment in Enum.GetValues<SpatialEnvironment>().Where(x => x is not SpatialEnvironment.Room and not SpatialEnvironment.LivingRoom))
        foreach (float amount in new[] { .25f, .5f, 1f })
        {
            float[] input = Signal(rate, .15), old = new float[rate], updated = new float[rate];
            input.CopyTo(old, 0); input.CopyTo(updated, 0);
            new LegacySpatialEffectsEngine(rate).ApplyEnvironment(old, environment, amount);
            new SpatialEffectsEngine(rate).ApplyEnvironment(updated, environment, amount);
            Check($"Other profile unchanged/{environment}/{rate}/{amount}", Bits(old, updated));
        }
        foreach (int rate in new[] { 16000, 22050, 48000 })
        {
            float[] input = Signal(rate, .8), expected = (float[])input.Clone();
            new SpatialEffectsEngine(rate).ApplyEnvironment(expected, SpatialEnvironment.LivingRoom, 1);
            float[][] concurrent = new float[8][];
            Parallel.For(0, concurrent.Length, i =>
            {
                concurrent[i] = (float[])input.Clone(); new SpatialEffectsEngine(rate).ApplyEnvironment(concurrent[i], SpatialEnvironment.LivingRoom, 1);
            });
            Check($"Independent concurrent requests/{rate}", concurrent.All(x => Bits(expected, x)));
            var reused = new SpatialEffectsEngine(rate);
            reused.ApplyEnvironment((float[])input.Clone(), SpatialEnvironment.LivingRoom, 1);
            reused.ApplyEnvironment((float[])input.Clone(), SpatialEnvironment.Stage, 1);
            float[] returned = (float[])input.Clone(); reused.ApplyEnvironment(returned, SpatialEnvironment.LivingRoom, 1);
            Check($"Switching back resets LivingRoom/{rate}", Bits(expected, returned));
            float[] final = (float[])expected.Clone(); VolumeShifter.ApplyVolume(final, 2.5118864f, rate);
            Check($"Shared final limiter bounds output/{rate}", final.Length == expected.Length && final.All(float.IsFinite)
                && final.Max(x => Math.Abs(x)) <= VolumeShifter.Ceiling + 1e-6);
            var engine = new SpatialEffectsEngine(rate); engine.ApplyEnvironment((float[])input.Clone(), SpatialEnvironment.LivingRoom, .5f);
            float[] buffer = (float[])input.Clone(); long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4; i++) { input.CopyTo(buffer, 0); engine.ApplyEnvironment(buffer, SpatialEnvironment.LivingRoom, .5f); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check($"Zero hot-path allocation/{rate}", allocated == 0);
        }
        var benchmark = new List<object>();
        foreach (int rate in new[] { 22050, 48000 })
        {
            float[] input = Signal(rate, 1), a = new float[rate], b = new float[rate];
            var engine = new SpatialEffectsEngine(rate); var legacy = new LegacySpatialEffectsEngine(rate);
            for (int i = 0; i < 4; i++) { input.CopyTo(a, 0); input.CopyTo(b, 0); engine.ApplyEnvironment(a, SpatialEnvironment.LivingRoom, 1); legacy.ApplyEnvironment(b, SpatialEnvironment.LivingRoom, 1); }
            double[] current = new double[11], previous = new double[11];
            for (int i = 0; i < current.Length; i++)
            {
                input.CopyTo(a, 0); long time = Stopwatch.GetTimestamp(); engine.ApplyEnvironment(a, SpatialEnvironment.LivingRoom, 1); current[i] = Stopwatch.GetElapsedTime(time).TotalMilliseconds;
                input.CopyTo(b, 0); time = Stopwatch.GetTimestamp(); legacy.ApplyEnvironment(b, SpatialEnvironment.LivingRoom, 1); previous[i] = Stopwatch.GetElapsedTime(time).TotalMilliseconds;
            }
            Array.Sort(current); Array.Sort(previous);
            benchmark.Add(new { Rate = rate, UpdatedMedianMsPerAudioSecond = current[5], PreviousMedianMsPerAudioSecond = previous[5] });
        }
        if (outputDirectory != null)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "checks.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(outputDirectory, "benchmarks.json"), JsonSerializer.Serialize(benchmark, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Checks = Results.Count, Failures = _failures, Benchmarks = benchmark }));
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckTail(int rate, float amount, float volume)
    {
        float[] input = Signal(rate, .1); var engine = new SpatialEffectsEngine(rate);
        engine.ApplyEnvironment((float[])input.Clone(), SpatialEnvironment.LivingRoom, amount);
        int quietWindow = (int)QuietWindow.GetValue(engine)!;
        int probe = Math.Max(1, rate / 50), cap = 4 * rate, quiet = 0, generated = 0;
        float[] block = new float[probe];
        while (generated < cap)
        {
            int length = Math.Min(probe, cap - generated); block.AsSpan().Clear();
            engine.ApplyEnvironment(block.AsSpan(0, length), SpatialEnvironment.LivingRoom, amount);
            float peak = 0; for (int i = 0; i < length; i++) peak = Math.Max(peak, Math.Abs(block[i]));
            generated += length; quiet = peak * volume < .005f ? quiet + length : 0;
            if (quiet >= quietWindow) break;
        }
        float[] remaining = new float[rate]; engine.ApplyEnvironment(remaining, SpatialEnvironment.LivingRoom, amount);
        Check($"Quiet-window drain retains audible tail/{rate}/{amount}/{volume}", generated < cap && remaining.Max(x => Math.Abs(x)) * volume < .005f);
    }
    private static void Check(string name, bool pass)
    {
        Results.Add(new { Name = name, Passed = pass }); if (pass) return; _failures++; Console.WriteLine($"FAIL: {name}");
    }
    public static float[] Signal(int rate, double seconds)
    {
        float[] samples = new float[(int)(rate * seconds)]; uint state = 0x834af1;
        for (int i = 0; i < samples.Length; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            samples[i] = .15f * MathF.Sin(2 * MathF.PI * 211 * i / rate) + .08f * MathF.Sin(2 * MathF.PI * 1713 * i / rate)
                + (state / (float)uint.MaxValue * 2 - 1) * .025f;
        }
        samples[0] = .4f; return samples;
    }
    private static bool Bits(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
}
