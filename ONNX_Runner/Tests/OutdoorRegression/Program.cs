using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models;
using ONNX_Runner.Services;
using ONNX_Runner.Tests;

string? output = args.Length > 0 ? args[0] : null;
var checks = new List<object>();
var timings = new List<object>();
int failures = 0;
string[] names = Enum.GetNames<SpatialEnvironment>();
Check("Canyon follows Forest without reordering existing environments", names.SequenceEqual(new[] { "None", "Room", "LivingRoom", "Bathroom", "Stage", "ConcreteHall", "Hangar", "Cathedral", "Dungeon", "Cave", "Forest", "Canyon", "Muffled", "Underwater", "InnerVoice" }));
var endpoint = (IValueHttpResult)InfoEndpoints.GetEnvironments();
string[] advertised = JsonSerializer.SerializeToElement(endpoint.Value).GetProperty("environments").EnumerateArray().Select(x => x.GetString()!).ToArray();
Check("The environment endpoint preserves enumeration order", names.SequenceEqual(advertised));

using Stream fixtureStream = typeof(OutdoorFixture).Assembly.GetManifestResourceStream("OutdoorFixtures.json")!;
OutdoorFixtureCorpus corpus = JsonSerializer.Deserialize<OutdoorFixtureCorpus>(fixtureStream)!;
Check("References identify the pinned native implementation", corpus.Source.Contains("OpenAL Soft 1.23.1", StringComparison.Ordinal));
Check("Native fixtures cover Canyon, ten rates and three intensities", corpus.References.Length == 30
    && OutdoorTestData.Environments.All(name => OutdoorTestData.Rates.All(rate => new[] { .25f, .5f, 1f }.All(amount =>
        corpus.References.Any(x => x.Environment == name && x.Rate == rate && x.Amount == amount)))));
foreach (OutdoorFixture reference in corpus.References)
{
    float[] samples = new float[reference.Rate * OutdoorTestData.ReferenceSeconds];
    OutdoorTestData.Signal(reference.Rate, reference.Rate / 10).CopyTo(samples, 0);
    var environment = Enum.Parse<SpatialEnvironment>(reference.Environment);
    new SpatialEffectsEngine(reference.Rate).ApplyEnvironment(samples, environment, reference.Amount);
    bool valid = Math.Abs(Energy(samples) - reference.Energy) < Math.Max(1e-10, reference.Energy * 2e-5)
        && reference.Indices.Length >= 64 && reference.Indices.Length == reference.Samples.Length;
    for (int i = 0; valid && i < reference.Indices.Length; i++)
        valid = Math.Abs(samples[reference.Indices[i]] - reference.Samples[i]) < 3e-6;
    Check($"Native profile, return gain and intensity/{environment}/{reference.Rate}/{reference.Amount}", valid);
}

foreach (string name in OutdoorTestData.Environments)
{
    var environment = Enum.Parse<SpatialEnvironment>(name);
    Check($"Case-insensitive request parsing/{name}", Enum.TryParse(name.ToLowerInvariant(), true, out SpatialEnvironment parsed) && parsed == environment);
    foreach (int rate in OutdoorTestData.Rates)
    {
        float[] source = new float[rate * 2];
        OutdoorTestData.Signal(rate, rate / 5).CopyTo(source, 0);
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
        float[] silence = new float[rate];
        new SpatialEffectsEngine(rate).ApplyEnvironment(silence, environment, 1);
        Check($"Fresh silence remains silent/{environment}/{rate}", silence.All(x => x == 0));
        float[] direct = new float[Math.Max(1, rate / 100)];
        direct[0] = 2;
        new SpatialEffectsEngine(rate).ApplyEnvironment(direct, environment, 1);
        Check($"Direct speech retains floating-point headroom/{environment}/{rate}", direct[0] == 2);
        foreach (SpatialEnvironment other in new[] { SpatialEnvironment.Forest })
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
        float[] input = OutdoorTestData.Signal(rate, rate), expected = (float[])input.Clone();
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
var acoustic = new Dictionary<string, OutdoorImpulseMetrics>();
foreach (SpatialEnvironment environment in new[] { SpatialEnvironment.Forest, SpatialEnvironment.Canyon })
{
    const int rate = 48000;
    float[] response = new float[rate * OutdoorTestData.ReferenceSeconds];
    response[0] = .5f;
    new SpatialEffectsEngine(rate).ApplyEnvironment(response, environment, 1);
    response[0] -= .5f;
    acoustic[environment.ToString()] = OutdoorTestData.Measure(response, rate);
}
Check("Mountains is absent from the environment endpoint", !advertised.Contains("Mountains", StringComparer.Ordinal));
Check("Canyon retains the original 223 ms reflection delay", Math.Abs(acoustic["Canyon"].FirstReflectionMilliseconds - 223) < .1);
if (output != null)
{
    Directory.CreateDirectory(output);
    var options = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(Path.Combine(output, "interior-checks.json"), JsonSerializer.Serialize(checks, options));
    File.WriteAllText(Path.Combine(output, "interior-timings.json"), JsonSerializer.Serialize(timings, options));
    File.WriteAllText(Path.Combine(output, "acoustic-features.json"), JsonSerializer.Serialize(acoustic, options));
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
