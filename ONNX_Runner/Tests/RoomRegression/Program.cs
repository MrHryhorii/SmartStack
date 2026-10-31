using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

string? output = args.Length > 0 ? args[0] : null;
int result = RoomChecks.Run(output);
return result == 0 ? RoomPipelineTailChecks.Run(output) : result;

public static class RoomChecks
{
    private static readonly List<object> Results = [];
    private static int _failures;

    internal static int Run(string? output)
    {
        string[] names = Enum.GetNames<SpatialEnvironment>();
        Check("Room precedes LivingRoom in the environment list", names.Take(3).SequenceEqual(new[] { "None", "Room", "LivingRoom" }));
        var endpoint = (IValueHttpResult)InfoEndpoints.GetEnvironments();
        string[] advertised = JsonSerializer.SerializeToElement(endpoint.Value).GetProperty("environments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Check("The actual environment endpoint advertises the same order", names.SequenceEqual(advertised));
        Check("Room is selected through the existing name parser", Enum.TryParse("room", true, out SpatialEnvironment parsed) && parsed == SpatialEnvironment.Room);
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000, 192000 })
        {
            float[] source = new float[rate]; Signal(rate, .25).CopyTo(source, 0);
            float[] full = (float[])source.Clone(); new SpatialEffectsEngine(rate).ApplyEnvironment(full, SpatialEnvironment.Room, 1);
            foreach (float amount in new[] { 0, .01f, .25f, .5f, .75f, 1 })
            {
                float[] whole = (float[])source.Clone(), split = (float[])source.Clone();
                var engine = new SpatialEffectsEngine(rate); engine.ApplyEnvironment(whole, SpatialEnvironment.Room, amount);
                var streaming = new SpatialEffectsEngine(rate); int[] sizes = [0, 1, 17, 127, 256, 4096, 777];
                for (int offset = 0, part = 0; offset < split.Length; part++)
                {
                    int length = Math.Min(sizes[part % sizes.Length], split.Length - offset);
                    streaming.ApplyEnvironment(split.AsSpan(offset, length), SpatialEnvironment.Room, amount); offset += length;
                }
                Check($"Chunk invariance/{rate}/{amount}", Bits(whole, split));
                Check($"Finite output/{rate}/{amount}", whole.All(float.IsFinite));
                engine.Reset(); float[] repeated = (float[])source.Clone(); engine.ApplyEnvironment(repeated, SpatialEnvironment.Room, amount);
                Check($"Reset/{rate}/{amount}", Bits(whole, repeated));
                if (amount == 0) Check($"Zero is exact bypass/{rate}", Bits(source, whole));
                double error = 0; float strength = amount * (2 - amount);
                for (int i = 0; i < whole.Length; i++) error = Math.Max(error, Math.Abs(whole[i] - source[i] - (full[i] - source[i]) * strength));
                Check($"Intensity scales only the fixed reflected return/{rate}/{amount}", error < 3e-7);
            }
            float[] silence = new float[rate]; new SpatialEffectsEngine(rate).ApplyEnvironment(silence, SpatialEnvironment.Room, 1);
            Check($"Fresh silence produces no signal/{rate}", silence.All(x => x == 0));
            float[] direct = new float[rate / 100]; direct[0] = 2;
            new SpatialEffectsEngine(rate).ApplyEnvironment(direct, SpatialEnvironment.Room, 1);
            Check($"Direct signal retains floating-point headroom/{rate}", direct[0] == 2);
            float[] living = (float[])source.Clone(); new SpatialEffectsEngine(rate).ApplyEnvironment(living, SpatialEnvironment.LivingRoom, 1);
            Check($"Room and LivingRoom remain distinct/{rate}", !Bits(full, living));
            foreach (SpatialEnvironment other in new[] { SpatialEnvironment.LivingRoom, SpatialEnvironment.Stage })
            {
                var reused = new SpatialEffectsEngine(rate); reused.ApplyEnvironment((float[])source.Clone(), SpatialEnvironment.Room, 1);
                reused.ApplyEnvironment((float[])source.Clone(), other, 1);
                float[] returned = (float[])source.Clone(); reused.ApplyEnvironment(returned, SpatialEnvironment.Room, 1);
                Check($"Switching back resets Room/{other}/{rate}", Bits(full, returned));
            }
        }
        var benchmarks = new List<object>();
        foreach (int rate in new[] { 22050, 48000 })
        {
            float[] source = Signal(rate, 1), expected = (float[])source.Clone();
            new SpatialEffectsEngine(rate).ApplyEnvironment(expected, SpatialEnvironment.Room, 1);
            float[][] parallel = new float[8][];
            Parallel.For(0, parallel.Length, i => { parallel[i] = (float[])source.Clone(); new SpatialEffectsEngine(rate).ApplyEnvironment(parallel[i], SpatialEnvironment.Room, 1); });
            Check($"Independent concurrent requests/{rate}", parallel.All(x => Bits(expected, x)));
            float[] final = (float[])expected.Clone(); VolumeShifter.ApplyVolume(final, 2.5118864f, rate);
            Check($"Shared final limiter protects amplified Room/{rate}", final.Length == expected.Length && final.All(float.IsFinite) && final.Max(x => Math.Abs(x)) <= VolumeShifter.Ceiling + 1e-6);
            float[] buffer = new float[rate]; var room = new SpatialEffectsEngine(rate); var living = new SpatialEffectsEngine(rate);
            for (int i = 0; i < 4; i++) { source.CopyTo(buffer, 0); room.ApplyEnvironment(buffer, SpatialEnvironment.Room, 1); source.CopyTo(buffer, 0); living.ApplyEnvironment(buffer, SpatialEnvironment.LivingRoom, 1); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4; i++) { source.CopyTo(buffer, 0); room.ApplyEnvironment(buffer, SpatialEnvironment.Room, .5f); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check($"Zero hot-path allocation/{rate}", allocated == 0);
            double[] roomMs = new double[11], livingMs = new double[11];
            for (int i = 0; i < roomMs.Length; i++)
            {
                source.CopyTo(buffer, 0); long start = Stopwatch.GetTimestamp(); room.ApplyEnvironment(buffer, SpatialEnvironment.Room, 1); roomMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                source.CopyTo(buffer, 0); start = Stopwatch.GetTimestamp(); living.ApplyEnvironment(buffer, SpatialEnvironment.LivingRoom, 1); livingMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Array.Sort(roomMs); Array.Sort(livingMs);
            benchmarks.Add(new { Rate = rate, RoomMedianMsPerAudioSecond = roomMs[5], LivingRoomMedianMsPerAudioSecond = livingMs[5] });
        }
        if (output != null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "room-checks.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "room-benchmarks.json"), JsonSerializer.Serialize(benchmarks, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { RoomChecks = Results.Count, Failures = _failures, Benchmarks = benchmarks }));
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
