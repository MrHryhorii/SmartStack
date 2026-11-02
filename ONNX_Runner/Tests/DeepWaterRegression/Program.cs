using ONNX_Runner.Services.Audio.Levels;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Services;

var checks = new List<object>();
var timings = new List<object>();
int failures = 0;
int[] rates = [8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000, 192000];
float[] amounts = [0, .001f, .0011f, .01f, .25f, .5f, .75f, 1];
string[] names = Enum.GetNames<SpatialEnvironment>();
Check("DeepWater follows Underwater and precedes Muffled", names.Skip(Array.IndexOf(names, "SwimmingPool")).Take(5).SequenceEqual(
    new[] { "SwimmingPool", "Underwater", "DeepWater", "Muffled", "InnerVoice" }));
var endpoint = (IValueHttpResult)InfoEndpoints.GetEnvironments();
string[] advertised = JsonSerializer.SerializeToElement(endpoint.Value).GetProperty("environments")
    .EnumerateArray().Select(x => x.GetString()!).ToArray();
Check("Actual API preserves environment order", names.SequenceEqual(advertised));
Check("Case-insensitive request parsing", Enum.TryParse("deepwater", true, out SpatialEnvironment parsed)
    && parsed == SpatialEnvironment.DeepWater);

foreach (int rate in rates)
{
    float[] source = new float[rate * 2];
    Signal(rate, rate / 4).CopyTo(source, 0);
    foreach (float amount in amounts)
    {
        float[] expected = (float[])source.Clone();
        new SpatialEffectsEngine(rate).ApplyEnvironment(expected, SpatialEnvironment.Underwater, amount);
        new SpatialEffectsEngine(rate).ApplyEnvironment(expected, SpatialEnvironment.Muffled, amount);
        float[] whole = (float[])source.Clone();
        var engine = new SpatialEffectsEngine(rate);
        engine.ApplyEnvironment(whole, SpatialEnvironment.DeepWater, amount);
        Check($"Exact sequential composition at original intensity/{rate}/{amount}", Bits(expected, whole));
        Check($"Finite output/{rate}/{amount}", whole.All(float.IsFinite));
        float[] split = (float[])source.Clone();
        var streaming = new SpatialEffectsEngine(rate);
        int[] sizes = [0, 1, 17, 137, 256, 4096, 777];
        for (int offset = 0, part = 0; offset < split.Length; part++)
        {
            int count = Math.Min(sizes[part % sizes.Length], split.Length - offset);
            streaming.ApplyEnvironment(split.AsSpan(offset, count), SpatialEnvironment.DeepWater, amount);
            offset += count;
        }
        Check($"Chunk and empty-buffer continuity/{rate}/{amount}", Bits(whole, split));
        engine.Reset();
        float[] repeated = (float[])source.Clone();
        engine.ApplyEnvironment(repeated, SpatialEnvironment.DeepWater, amount);
        Check($"Reset clears both stages/{rate}/{amount}", Bits(whole, repeated));
        if (amount <= .001f) Check($"Existing bypass threshold remains exact/{rate}/{amount}", Bits(source, whole));
    }

    float[] changing = (float[])source.Clone(), sequential = (float[])source.Clone();
    var changingEngine = new SpatialEffectsEngine(rate);
    var water = new SpatialEffectsEngine(rate);
    var occlusion = new SpatialEffectsEngine(rate);
    for (int offset = 0, part = 0; offset < changing.Length; part++)
    {
        int count = Math.Min(137, changing.Length - offset);
        float amount = amounts[part % amounts.Length];
        changingEngine.ApplyEnvironment(changing.AsSpan(offset, count), SpatialEnvironment.DeepWater, amount);
        water.ApplyEnvironment(sequential.AsSpan(offset, count), SpatialEnvironment.Underwater, amount);
        occlusion.ApplyEnvironment(sequential.AsSpan(offset, count), SpatialEnvironment.Muffled, amount);
        offset += count;
    }
    Check($"Changing intensity retains independent stage histories/{rate}", Bits(changing, sequential));
    float[] silence = new float[rate];
    new SpatialEffectsEngine(rate).ApplyEnvironment(silence, SpatialEnvironment.DeepWater, 1);
    Check($"Fresh silence remains silent/{rate}", silence.All(x => x == 0));

    var prepared = new SpatialEffectsEngine(rate);
    prepared.ApplyEnvironment(Span<float>.Empty, SpatialEnvironment.DeepWater, 1);
    int maximumTail = Property(prepared, "MaximumTailSamples");
    int quietWindow = Property(prepared, "TailSilenceWindowSamples");
    Check($"Tail budget inherits the underwater profile/{rate}", maximumTail == rate * 4);
    Check($"Tail observation covers delayed underwater returns/{rate}", quietWindow >= Math.Ceiling(rate * .65f));

    float[] normal = Signal(rate, rate), amplified = normal.Select(x => x * 16).ToArray();
    new SpatialEffectsEngine(rate).ApplyEnvironment(normal, SpatialEnvironment.DeepWater, 1);
    new SpatialEffectsEngine(rate).ApplyEnvironment(amplified, SpatialEnvironment.DeepWater, 1);
    Check($"No local clipping or normalization/{rate}", amplified.Max(x => Math.Abs(x)) > 1
        && amplified.Select((x, i) => Math.Abs(x - normal[i] * 16)).Max() < 1e-5);

    float[] underwater = (float[])source.Clone(), muffled = (float[])source.Clone(), deep = (float[])source.Clone();
    new SpatialEffectsEngine(rate).ApplyEnvironment(underwater, SpatialEnvironment.Underwater, 1);
    new SpatialEffectsEngine(rate).ApplyEnvironment(muffled, SpatialEnvironment.Muffled, 1);
    new SpatialEffectsEngine(rate).ApplyEnvironment(deep, SpatialEnvironment.DeepWater, 1);
    Check($"DeepWater has a different response from Underwater/{rate}", !Bits(deep, underwater));
    Check($"DeepWater has a different response from Muffled/{rate}", !Bits(deep, muffled));
    Check($"Underwater reflections survive the final low-pass/{rate}", Power(deep.AsSpan(rate / 2)) > 1e-12
        && Power(deep.AsSpan(rate / 2)) > Power(muffled.AsSpan(rate / 2)) * 1000);
    foreach (SpatialEnvironment other in Enum.GetValues<SpatialEnvironment>().Where(x => x != SpatialEnvironment.DeepWater))
    {
        var reused = new SpatialEffectsEngine(rate);
        reused.ApplyEnvironment((float[])source.Clone(), SpatialEnvironment.DeepWater, 1);
        float[] switched = (float[])source.Clone(), isolated = (float[])source.Clone();
        reused.ApplyEnvironment(switched, other, 1);
        new SpatialEffectsEngine(rate).ApplyEnvironment(isolated, other, 1);
        Check($"Post-filter does not leak when switching environments/{rate}/{other}", Bits(switched, isolated));
        reused.ApplyEnvironment((float[])source.Clone(), SpatialEnvironment.None, 1);
        float[] returned = (float[])source.Clone();
        reused.ApplyEnvironment(returned, SpatialEnvironment.DeepWater, 1);
        // None is an intentional bypass and does not reset the active processor.
        if (other == SpatialEnvironment.None) continue;
        Check($"Returning to DeepWater resets both histories/{rate}/{other}", Bits(returned, deep));
    }
}

foreach (int rate in new[] { 16000, 22050, 48000 })
{
    float[] source = Signal(rate, rate), expected = (float[])source.Clone();
    new SpatialEffectsEngine(rate).ApplyEnvironment(expected, SpatialEnvironment.DeepWater, 1);
    float[][] concurrent = new float[8][];
    Parallel.For(0, concurrent.Length, i =>
    {
        concurrent[i] = (float[])source.Clone();
        new SpatialEffectsEngine(rate).ApplyEnvironment(concurrent[i], SpatialEnvironment.DeepWater, 1);
    });
    Check($"Concurrent requests have independent state/{rate}", concurrent.All(x => Bits(expected, x)));
    VolumeShifter.ApplyVolume(expected, 2.5118864f, rate);
    Check($"Shared final peak protection/{rate}", expected.All(float.IsFinite)
        && expected.Max(x => Math.Abs(x)) <= VolumeShifter.Ceiling + 1e-6);
    var engine = new SpatialEffectsEngine(rate);
    float[] buffer = new float[rate];
    for (int i = 0; i < 4; i++) { source.CopyTo(buffer, 0); engine.ApplyEnvironment(buffer, SpatialEnvironment.DeepWater, .5f); }
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 4; i++)
    {
        source.CopyTo(buffer, 0);
        engine.ApplyEnvironment(buffer, SpatialEnvironment.DeepWater, (i & 1) == 0 ? .25f : 1f);
    }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check($"Zero warm allocation including intensity changes/{rate}", allocated == 0);
    double[] elapsed = new double[11];
    for (int i = 0; i < elapsed.Length; i++)
    {
        source.CopyTo(buffer, 0);
        long start = Stopwatch.GetTimestamp();
        engine.ApplyEnvironment(buffer, SpatialEnvironment.DeepWater, 1);
        elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    Array.Sort(elapsed);
    timings.Add(new { Rate = rate, MedianMillisecondsPerAudioSecond = elapsed[5] });
}
if (args.Length > 0)
{
    Directory.CreateDirectory(args[0]);
    var options = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(Path.Combine(args[0], "deepwater-checks.json"), JsonSerializer.Serialize(checks, options));
    File.WriteAllText(Path.Combine(args[0], "deepwater-timings.json"), JsonSerializer.Serialize(timings, options));
}
Console.WriteLine(JsonSerializer.Serialize(new { Checks = checks.Count, Failures = failures, Timings = timings }));
return failures == 0 ? 0 : 1;

void Check(string name, bool pass)
{
    checks.Add(new { Name = name, Passed = pass });
    if (pass) return;
    failures++;
    Console.Error.WriteLine($"FAIL: {name}");
}
static int Property(SpatialEffectsEngine engine, string name) =>
    (int)typeof(SpatialEffectsEngine).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
static bool Bits(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
static double Power(ReadOnlySpan<float> samples)
{
    double sum = 0;
    foreach (float sample in samples) sum += sample * (double)sample;
    return sum / Math.Max(1, samples.Length);
}
static float[] Signal(int rate, int length)
{
    float[] samples = new float[length];
    uint noise = 0x834af1;
    for (int i = 0; i < samples.Length; i++)
    {
        noise ^= noise << 13; noise ^= noise >> 17; noise ^= noise << 5;
        samples[i] = .15f * MathF.Sin(2 * MathF.PI * 211 * i / rate)
            + .08f * MathF.Sin(2 * MathF.PI * 1713 * i / rate)
            + (noise / (float)uint.MaxValue * 2 - 1) * .025f;
    }
    samples[0] = .4f;
    return samples;
}
