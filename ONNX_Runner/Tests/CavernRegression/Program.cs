using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

string? output = args.Length > 0 ? args[0] : null;
int result = CavernChecks.Run(output);
return result == 0 ? CavernPipelineTailChecks.Run(output) : result;

public static class CavernChecks
{
    private static readonly List<object> Results = [];
    private static int _failures;

    internal static int Run(string? output)
    {
        foreach (SpatialEnvironment environment in new[] { SpatialEnvironment.Dungeon, SpatialEnvironment.Cave })
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000 })
        {
            float[] source = new float[rate * 2]; Signal(rate, .25).CopyTo(source, 0);
            float[] full = (float[])source.Clone(); new SpatialEffectsEngine(rate).ApplyEnvironment(full, environment, 1);
            foreach (float amount in new[] { 0, .01f, .25f, .5f, .75f, 1 })
            {
                float[] whole = (float[])source.Clone(), split = (float[])source.Clone();
                var engine = new SpatialEffectsEngine(rate); engine.ApplyEnvironment(whole, environment, amount);
                var streaming = new SpatialEffectsEngine(rate); int[] sizes = [0, 1, 17, 127, 256, 4096, 777];
                for (int offset = 0, part = 0; offset < split.Length; part++)
                {
                    int length = Math.Min(sizes[part % sizes.Length], split.Length - offset);
                    streaming.ApplyEnvironment(split.AsSpan(offset, length), environment, amount); offset += length;
                }
                Check($"Chunk invariance/{environment}/{rate}/{amount}", Bits(whole, split));
                Check($"Finite output/{environment}/{rate}/{amount}", whole.All(float.IsFinite));
                engine.Reset(); float[] repeated = (float[])source.Clone(); engine.ApplyEnvironment(repeated, environment, amount);
                Check($"Reset/{environment}/{rate}/{amount}", Bits(whole, repeated));
                if (amount == 0) Check($"Zero is exact bypass/{environment}/{rate}", Bits(source, whole));
                double error = 0; float strength = amount * (2 - amount);
                for (int i = 0; i < whole.Length; i++) error = Math.Max(error, Math.Abs(whole[i] - source[i] - (full[i] - source[i]) * strength));
                Check($"Intensity scales only the fixed reflected return/{environment}/{rate}/{amount}", error < 1e-6);
            }
            float[] silence = new float[rate]; new SpatialEffectsEngine(rate).ApplyEnvironment(silence, environment, 1);
            Check($"Fresh silence produces no signal/{environment}/{rate}", silence.All(x => x == 0));
            float[] direct = new float[rate / 100]; direct[0] = 2;
            new SpatialEffectsEngine(rate).ApplyEnvironment(direct, environment, 1);
            Check($"Direct signal retains floating-point headroom/{environment}/{rate}", direct[0] == 2);
            foreach (SpatialEnvironment other in new[] { SpatialEnvironment.Room, SpatialEnvironment.LivingRoom, SpatialEnvironment.Stage, SpatialEnvironment.ConcreteHall, SpatialEnvironment.Dungeon, SpatialEnvironment.Cave, SpatialEnvironment.Underwater }.Where(x => x != environment))
            {
                var reused = new SpatialEffectsEngine(rate); reused.ApplyEnvironment((float[])source.Clone(), environment, 1);
                reused.ApplyEnvironment((float[])source.Clone(), other, 1);
                float[] returned = (float[])source.Clone(); reused.ApplyEnvironment(returned, environment, 1);
                Check($"Switching back resets profile/{environment}/{other}/{rate}", Bits(full, returned));
            }
            float[] changing = (float[])source.Clone(); var causal = new SpatialEffectsEngine(rate); double maximumError = 0;
            for (int offset = 0, block = 0; offset < changing.Length; block++)
            {
                int length = Math.Min(137, changing.Length - offset); float amount = (block % 3) switch { 0 => .25f, 1 => .5f, _ => 1f };
                causal.ApplyEnvironment(changing.AsSpan(offset, length), environment, amount);
                for (int i = offset; i < offset + length; i++) maximumError = Math.Max(maximumError, Math.Abs(changing[i] - source[i] - (full[i] - source[i]) * (amount * (2 - amount))));
                offset += length;
            }
            Check($"Intensity changes retain the acoustic history/{environment}/{rate}", maximumError < 1e-6);
        }
        var benchmarks = new List<object>();
        foreach (SpatialEnvironment environment in new[] { SpatialEnvironment.Dungeon, SpatialEnvironment.Cave })
        foreach (int rate in new[] { 22050, 48000 })
        {
            float[] source = Signal(rate, 1), expected = (float[])source.Clone();
            new SpatialEffectsEngine(rate).ApplyEnvironment(expected, environment, 1);
            float[][] parallel = new float[8][];
            Parallel.For(0, parallel.Length, i => { parallel[i] = (float[])source.Clone(); new SpatialEffectsEngine(rate).ApplyEnvironment(parallel[i], environment, 1); });
            Check($"Independent concurrent requests/{environment}/{rate}", parallel.All(x => Bits(expected, x)));
            float[] final = (float[])expected.Clone(); VolumeShifter.ApplyVolume(final, 2.5118864f, rate);
            Check($"Shared final limiter protects amplified profile/{environment}/{rate}", final.Length == expected.Length && final.All(float.IsFinite) && final.Max(x => Math.Abs(x)) <= VolumeShifter.Ceiling + 1e-6);
            float[] buffer = new float[rate]; var current = new SpatialEffectsEngine(rate); var legacy = new LegacySpatialEffectsEngine(rate);
            for (int i = 0; i < 4; i++) { source.CopyTo(buffer, 0); current.ApplyEnvironment(buffer, environment, 1); source.CopyTo(buffer, 0); legacy.ApplyEnvironment(buffer, environment, 1); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4; i++) { source.CopyTo(buffer, 0); current.ApplyEnvironment(buffer, environment, .5f); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check($"Zero hot-path allocation/{environment}/{rate}", allocated == 0);
            double[] currentMs = new double[11], legacyMs = new double[11];
            for (int i = 0; i < currentMs.Length; i++)
            {
                source.CopyTo(buffer, 0); long start = Stopwatch.GetTimestamp(); current.ApplyEnvironment(buffer, environment, 1); currentMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                source.CopyTo(buffer, 0); start = Stopwatch.GetTimestamp(); legacy.ApplyEnvironment(buffer, environment, 1); legacyMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Array.Sort(currentMs); Array.Sort(legacyMs);
            benchmarks.Add(new { Environment = environment.ToString(), Rate = rate, UpdatedMedianMsPerAudioSecond = currentMs[5], PreviousMedianMsPerAudioSecond = legacyMs[5] });
        }
        if (output != null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "cavern-checks.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "cavern-benchmarks.json"), JsonSerializer.Serialize(benchmarks, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { CavernChecks = Results.Count, Failures = _failures, Benchmarks = benchmarks }));
        return _failures == 0 ? 0 : 1;
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
    private static void Check(string name, bool pass)
    {
        Results.Add(new { Name = name, Passed = pass }); if (pass) return;
        _failures++; Console.WriteLine($"FAIL: {name}");
    }
}
