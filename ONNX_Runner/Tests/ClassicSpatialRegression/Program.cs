using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Dsp;
using Microsoft.AspNetCore.Http;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Services;
using ONNX_Runner.Tests;

string? output = args.Length > 0 ? args[0] : null;
var checks = new List<object>();
var timings = new List<object>();
int failures = 0;
string[] names = Enum.GetNames<SpatialEnvironment>();
Check("Classic effects follow InnerVoice", names.TakeLast(4).SequenceEqual(new[] { "InnerVoice", "Reverb", "Echo", "DualEcho" }));
var endpoint = (IValueHttpResult)InfoEndpoints.GetEnvironments();
string[] advertised = JsonSerializer.SerializeToElement(endpoint.Value).GetProperty("environments").EnumerateArray().Select(x => x.GetString()!).ToArray();
Check("The environment endpoint preserves enumeration order", names.SequenceEqual(advertised));

using Stream fixtureStream = typeof(ClassicSpatialFixture).Assembly.GetManifestResourceStream("ClassicSpatialFixtures.json")!;
ClassicSpatialFixtureCorpus corpus = JsonSerializer.Deserialize<ClassicSpatialFixtureCorpus>(fixtureStream)!;
Check("References identify the pinned native implementation", corpus.Source.Contains("OpenAL Soft 1.23.1", StringComparison.Ordinal));
Check("Native fixtures cover generic Reverb, ten rates and three intensities", corpus.References.Length == 30
    && new[] { "Reverb" }.All(name => ClassicSpatialTestData.Rates.All(rate => new[] { .25f, .5f, 1f }.All(amount =>
        corpus.References.Any(x => x.Environment == name && x.Rate == rate && x.Amount == amount)))));
foreach (ClassicSpatialFixture reference in corpus.References)
{
    float[] samples = new float[reference.Rate * ClassicSpatialTestData.ReferenceSeconds];
    ClassicSpatialTestData.Signal(reference.Rate, reference.Rate / 10).CopyTo(samples, 0);
    var environment = Enum.Parse<SpatialEnvironment>(reference.Environment);
    var engine = new SpatialEffectsEngine(reference.Rate);
    engine.ApplyEnvironment(samples, environment, reference.Amount);
    bool valid = Math.Abs(Energy(samples) - reference.Energy) < Math.Max(1e-10, reference.Energy * 2e-5)
        && reference.Indices.Length >= 64 && reference.Indices.Length == reference.Samples.Length;
    for (int i = 0; valid && i < reference.Indices.Length; i++)
        valid = Math.Abs(samples[reference.Indices[i]] - reference.Samples[i]) < 3e-6;
    Check($"Native profile, return gain and intensity/{environment}/{reference.Rate}/{reference.Amount}", valid);
}

foreach (string name in ClassicSpatialTestData.Environments)
{
    var environment = Enum.Parse<SpatialEnvironment>(name);
    Check($"Case-insensitive request parsing/{name}", Enum.TryParse(name.ToLowerInvariant(), true, out SpatialEnvironment parsed) && parsed == environment);
    foreach (int rate in ClassicSpatialTestData.Rates)
    {
        float[] source = new float[rate * 2];
        ClassicSpatialTestData.Signal(rate, rate / 5).CopyTo(source, 0);
        float[] full = (float[])source.Clone();
        new SpatialEffectsEngine(rate).ApplyEnvironment(full, environment, 1);
        foreach (float amount in new[] { 0, .01f, .25f, .5f, .75f, 1 })
        {
            float[] whole = (float[])source.Clone(), split = (float[])source.Clone();
            var engine = new SpatialEffectsEngine(rate);
            engine.ApplyEnvironment(whole, environment, amount);
            var streaming = new SpatialEffectsEngine(rate);
            int[] sizes = [0, 1, 17, 127, 256, 4096, 777];
            for (int offset = 0, part = 0; offset < split.Length; part++)
            {
                int length = Math.Min(sizes[part % sizes.Length], split.Length - offset);
                streaming.ApplyEnvironment(split.AsSpan(offset, length), environment, amount);
                offset += length;
            }
            Check($"Chunk invariance/{environment}/{rate}/{amount}", Bits(whole, split));
            Check($"Finite output/{environment}/{rate}/{amount}", whole.All(float.IsFinite));
            if (environment is SpatialEnvironment.Echo or SpatialEnvironment.DualEcho)
            {
                float[] reference = EchoReference(source, rate, environment, amount);
                double maximum = whole.Select((x, i) => Math.Abs(x - reference[i])).Max();
                Check($"Independent offline delay/filter reference/{environment}/{rate}/{amount}", maximum < 3e-6);
            }
            engine.Reset();
            float[] repeated = (float[])source.Clone();
            engine.ApplyEnvironment(repeated, environment, amount);
            Check($"Reset/{environment}/{rate}/{amount}", Bits(whole, repeated));
            if (amount == 0) Check($"Zero is exact bypass/{environment}/{rate}", Bits(source, whole));
            double error = 0;
            float strength = amount * (2 - amount);
            for (int i = 0; i < whole.Length; i++)
                error = Math.Max(error, Math.Abs(whole[i] - source[i] - (full[i] - source[i]) * strength));
            Check($"Intensity scales only the reflected return/{environment}/{rate}/{amount}", error < 3e-7);
        }
        var prepared = new SpatialEffectsEngine(rate);
        prepared.ApplyEnvironment(Span<float>.Empty, environment, 1);
        int maximumTail = (int)typeof(SpatialEffectsEngine).GetProperty("MaximumTailSamples", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(prepared)!;
        Check($"Profile-derived tail budget/{environment}/{rate}", maximumTail == rate * (environment switch { SpatialEnvironment.Echo => 6, SpatialEnvironment.DualEcho => 9, _ => 4 }));
        float[] silence = new float[rate];
        new SpatialEffectsEngine(rate).ApplyEnvironment(silence, environment, 1);
        Check($"Fresh silence remains silent/{environment}/{rate}", silence.All(x => x == 0));
        float[] direct = new float[Math.Max(1, rate / 100)];
        direct[0] = 2;
        new SpatialEffectsEngine(rate).ApplyEnvironment(direct, environment, 1);
        Check($"Direct speech retains floating-point headroom/{environment}/{rate}", direct[0] == 2);
        foreach (SpatialEnvironment other in new[] { SpatialEnvironment.Room, SpatialEnvironment.DeepWater, environment == SpatialEnvironment.Echo ? SpatialEnvironment.DualEcho : SpatialEnvironment.Echo })
        {
            float[] otherOutput = (float[])source.Clone();
            new SpatialEffectsEngine(rate).ApplyEnvironment(otherOutput, other, 1);
            Check($"Different impulse response/{environment}/{other}/{rate}", !Bits(full, otherOutput));
            var reused = new SpatialEffectsEngine(rate);
            reused.ApplyEnvironment((float[])source.Clone(), environment, 1);
            reused.ApplyEnvironment(otherOutput, other, 1);
            float[] returned = (float[])source.Clone();
            reused.ApplyEnvironment(returned, environment, 1);
            Check($"Switching environments resets state/{environment}/{other}/{rate}", Bits(full, returned));
        }
    }
    foreach (int rate in new[] { 16000, 22050, 48000 })
    {
        float[] input = ClassicSpatialTestData.Signal(rate, rate), expected = (float[])input.Clone();
        new SpatialEffectsEngine(rate).ApplyEnvironment(expected, environment, 1);
        float[][] concurrent = new float[8][];
        Parallel.For(0, concurrent.Length, i =>
        {
            concurrent[i] = (float[])input.Clone();
            new SpatialEffectsEngine(rate).ApplyEnvironment(concurrent[i], environment, 1);
        });
        Check($"Independent concurrent requests/{environment}/{rate}", concurrent.All(x => Bits(expected, x)));
        VolumeShifter.ApplyVolume(expected, 2.5118864f, rate);
        Check($"Shared final limiter bounds amplified output/{environment}/{rate}", expected.All(float.IsFinite)
            && expected.Max(x => Math.Abs(x)) <= VolumeShifter.Ceiling + 1e-6);
        var engine = new SpatialEffectsEngine(rate);
        float[] buffer = new float[rate];
        for (int i = 0; i < 4; i++) { input.CopyTo(buffer, 0); engine.ApplyEnvironment(buffer, environment, .5f); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; i++) { input.CopyTo(buffer, 0); engine.ApplyEnvironment(buffer, environment, (i & 1) == 0 ? .25f : 1f); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check($"Zero hot-path allocation, including intensity changes/{environment}/{rate}", allocated == 0);
        double[] measurements = new double[11];
        for (int i = 0; i < measurements.Length; i++)
        {
            input.CopyTo(buffer, 0);
            long start = Stopwatch.GetTimestamp();
            engine.ApplyEnvironment(buffer, environment, 1);
            measurements[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(measurements);
        timings.Add(new { Environment = name, Rate = rate, MedianMillisecondsPerAudioSecond = measurements[5] });
    }
}
foreach (int rate in ClassicSpatialTestData.Rates)
foreach (SpatialEnvironment environment in new[] { SpatialEnvironment.Echo, SpatialEnvironment.DualEcho })
{
    float[] impulse = new float[rate * 4]; impulse[0] = .5f;
    new SpatialEffectsEngine(rate).ApplyEnvironment(impulse, environment, 1);
    int first = (int)((environment == SpatialEnvironment.Echo ? .3f : .18f) * rate + .5f);
    float gain = environment == SpatialEnvironment.Echo ? .4f : .3f;
    Check($"Direct sample is retained/{environment}/{rate}", impulse[0] == .5f);
    Check($"No return before first tap/{environment}/{rate}", impulse.AsSpan(1, first - 1).ToArray().All(x => x == 0));
    Check($"Exact first tap time and level/{environment}/{rate}", impulse[first] == .5f * gain);
    if (environment == SpatialEnvironment.DualEcho)
    {
        int second = first + (int)(.25f * rate + .5f);
        Check($"Exact second initial tap/{rate}", impulse[second] == .5f * gain);
    }
    var prepared = new SpatialEffectsEngine(rate);
    prepared.ApplyEnvironment(Span<float>.Empty, environment, 1);
    int quiet = (int)typeof(SpatialEffectsEngine).GetProperty("TailSilenceWindowSamples", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(prepared)!;
    int longest = environment == SpatialEnvironment.Echo ? first : first + (int)(.25f * rate + .5f);
    Check($"Quiet hold spans two feedback cycles/{environment}/{rate}", quiet >= longest * 2 + 1);
}
foreach (int rate in new[] { 8000, 22050, 48000 })
foreach (SpatialEnvironment environment in new[] { SpatialEnvironment.Echo, SpatialEnvironment.DualEcho })
foreach (float amount in new[] { .25f, .5f, 1f })
{
    float[] dc = Enumerable.Repeat(.1f, rate * 10).ToArray();
    new SpatialEffectsEngine(rate).ApplyEnvironment(dc, environment, amount);
    float strength = amount * (2 - amount);
    double expected = .1 * (1 + strength * (environment == SpatialEnvironment.Echo ? .4 : .6) / .6);
    double actual = dc.AsSpan(dc.Length - rate).ToArray().Average();
    Check($"Stable feedback and fixed decay/{environment}/{rate}/{amount}", Math.Abs(actual - expected) < 2e-5);
}
if (output != null)
{
    Directory.CreateDirectory(output);
    var options = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(Path.Combine(output, "classic-spatial-checks.json"), JsonSerializer.Serialize(checks, options));
    File.WriteAllText(Path.Combine(output, "classic-spatial-timings.json"), JsonSerializer.Serialize(timings, options));
}
Console.WriteLine(JsonSerializer.Serialize(new { Checks = checks.Count, Failures = failures, Timings = timings }));
return failures == 0 ? 0 : 1;

void Check(string name, bool passed)
{
    checks.Add(new { Name = name, Passed = passed });
    if (passed) return;
    failures++;
    Console.Error.WriteLine($"FAIL: {name}");
}
static bool Bits(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
static double Energy(ReadOnlySpan<float> samples)
{
    double sum = 0;
    foreach (float sample in samples) sum += sample * (double)sample;
    return sum;
}

// Offline reference uses unbounded history and NAudio's independent RBJ shelf.
static float[] EchoReference(float[] source, int rate, SpatialEnvironment environment, float amount)
{
    float[] output = (float[])source.Clone();
    if (amount <= .001f) return output;
    float[] stored = new float[source.Length];
    int first = (int)((environment == SpatialEnvironment.Echo ? .3f : .18f) * rate + .5f);
    int second = environment == SpatialEnvironment.Echo ? 0 : first + (int)(.25f * rate + .5f);
    float gain = (environment == SpatialEnvironment.Echo ? .4f : .3f) * (amount * (2 - amount));
    var damping = BiQuadFilter.HighShelf(rate, Math.Min(5000f, rate * .49f), 1, (float)(20 * Math.Log10(.25)));
    for (int i = 0; i < source.Length; i++)
    {
        float a = i >= first ? stored[i - first] : 0;
        float b = second > 0 && i >= second ? stored[i - second] : 0;
        stored[i] = source[i] + damping.Transform(second == 0 ? a : b) * .4f;
        output[i] = source[i] + (second == 0 ? a : a + b) * gain;
    }
    return output;
}
