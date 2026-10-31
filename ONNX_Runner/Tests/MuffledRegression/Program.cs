using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;
using ONNX_Runner.Services.Spatial;

var checks = new List<object>();
var measurements = new List<object>();
int failures = 0;
void Check(string name, bool pass)
{
    checks.Add(new { Name = name, Passed = pass });
    if (pass) return;
    failures++; Console.Error.WriteLine($"FAIL: {name}");
}
int[] rates = [8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000, 192000];
object allocationControl = Allocate(); GC.KeepAlive(allocationControl);
long allocationBefore = GC.GetAllocatedBytesForCurrentThread(); allocationControl = Allocate();
long allocationAfter = GC.GetAllocatedBytesForCurrentThread(); GC.KeepAlive(allocationControl);
Check("Allocation host observes a positive control with background GC disabled",
    allocationAfter > allocationBefore && GC.GetConfigurationVariables().TryGetValue("ConcurrentGC", out object? gc) && gc is false);
foreach (int rate in rates)
{
    float[] source = new float[rate]; Signal(rate, rate / 3).CopyTo(source, 0);
    foreach (float amount in new[] { 0, .0011f, .01f, .25f, .5f, .75f, 1 })
    {
        float[] whole = (float[])source.Clone(), split = (float[])source.Clone();
        var engine = new SpatialEffectsEngine(rate);
        engine.ApplyEnvironment(whole, SpatialEnvironment.Muffled, amount);
        var streaming = new SpatialEffectsEngine(rate);
        int[] sizes = [0, 1, 13, 137, 256, 4096, 777];
        for (int offset = 0, part = 0; offset < split.Length; part++)
        {
            int count = Math.Min(sizes[part % sizes.Length], split.Length - offset);
            streaming.ApplyEnvironment(split.AsSpan(offset, count), SpatialEnvironment.Muffled, amount);
            offset += count;
        }
        Check($"Chunk continuity/{rate}/{amount}", Bits(whole, split));
        Check($"Finite output including near bypass/{rate}/{amount}", whole.All(float.IsFinite));
        engine.Reset(); float[] reset = (float[])source.Clone();
        engine.ApplyEnvironment(reset, SpatialEnvironment.Muffled, amount);
        Check($"Reset clears both filter histories/{rate}/{amount}", Bits(whole, reset));
        if (amount == 0) Check($"Exact zero bypass/{rate}", Bits(source, whole));
        else
        {
            float[] scaled = source.Select(x => x * 8).ToArray();
            new SpatialEffectsEngine(rate).ApplyEnvironment(scaled, SpatialEnvironment.Muffled, amount);
            // Input denormal cleanup and subnormal tails need not scale bit-for-bit.
            double error = scaled.Select((x, i) => Math.Abs(x - whole[i] * 8d)).Max();
            Check($"No local saturation or clipping/{rate}/{amount}", error < 1e-6);
        }
    }
    foreach (float amount in new[] { .25f, .5f, .75f, 1 })
    {
        float[] dc = Enumerable.Repeat(.1f, rate).ToArray();
        new SpatialEffectsEngine(rate).ApplyEnvironment(dc, SpatialEnvironment.Muffled, amount);
        double measuredDb = 20 * Math.Log10(dc.AsSpan(rate / 2).ToArray().Average() / .1);
        double expectedDb = amount * amount * amount;
        Check($"Mild late gain/{rate}/{amount}", Math.Abs(measuredDb - expectedDb) < .02);
        measurements.Add(new { Rate = rate, Amount = amount, ExpectedDcGainDb = expectedDb, MeasuredDcGainDb = measuredDb });
    }
    foreach (double frequency in new[] { 225d, 450, 900, 1800 })
    {
        float[] tone = Enumerable.Range(0, rate * 2).Select(i => (float)(.1 * Math.Sin(2 * Math.PI * frequency * i / rate))).ToArray();
        new SpatialEffectsEngine(rate).ApplyEnvironment(tone, SpatialEnvironment.Muffled, 1);
        double power = 0; for (int i = rate; i < tone.Length; i++) power += tone[i] * (double)tone[i];
        double db = 10 * Math.Log10(power / rate / .005);
        // Analytic magnitude of two cascaded Butterworth sections under the bilinear transform.
        double ratio = Math.Tan(Math.PI * frequency / rate) / Math.Tan(Math.PI * 450 / rate);
        double expectedDb = 1 - 20 * Math.Log10(1 + Math.Pow(ratio, 4));
        Check($"450 Hz endpoint and high-frequency attenuation/{rate}/{frequency}", Math.Abs(db - expectedDb) < .04);
    }
    float[] silence = new float[rate];
    new SpatialEffectsEngine(rate).ApplyEnvironment(silence, SpatialEnvironment.Muffled, 1);
    Check($"Silence adds neither noise nor reflections/{rate}", silence.All(x => x == 0));
    var switching = new SpatialEffectsEngine(rate);
    switching.ApplyEnvironment((float[])source.Clone(), SpatialEnvironment.Muffled, 1);
    float[] other = (float[])source.Clone(), isolated = (float[])source.Clone();
    switching.ApplyEnvironment(other, SpatialEnvironment.Underwater, .5f);
    new SpatialEffectsEngine(rate).ApplyEnvironment(isolated, SpatialEnvironment.Underwater, .5f);
    Check($"Muffled state does not leak into reverb/{rate}", Bits(other, isolated));
    float[] returned = (float[])source.Clone(), fresh = (float[])source.Clone();
    switching.ApplyEnvironment(returned, SpatialEnvironment.Muffled, .5f);
    new SpatialEffectsEngine(rate).ApplyEnvironment(fresh, SpatialEnvironment.Muffled, .5f);
    Check($"Returning to Muffled starts fresh/{rate}", Bits(returned, fresh));
    float[] first = (float[])source.Clone();
    new SpatialEffectsEngine(rate).ApplyEnvironment(first, SpatialEnvironment.Muffled, .0011f);
    double errorPower = 0, inputPower = 0;
    for (int i = 0; i < source.Length; i++)
    {
        errorPower += Math.Pow(first[i] - source[i], 2); inputPower += source[i] * (double)source[i];
    }
    // At 8 kHz this brighter signal has 1.013% RMS waveform error from the expected filter phase.
    // Bound onset error at 1.1%; a low-pass need not preserve phase as it leaves bypass.
    Check($"First active intensity approaches clean audio/{rate}", errorPower < inputPower * .000121);
    var a = new SpatialEffectsEngine(rate); var b = new SpatialEffectsEngine(rate);
    bool continuous = true, bypass = true;
    foreach (float amount in new[] { .5f, 0f, .001f, .0011f, .25f, .4999f, .5f, .5001f, .75f, 1f, .25f })
    {
        float[] whole = (float[])source.Clone(), split = (float[])source.Clone();
        a.ApplyEnvironment(whole, SpatialEnvironment.Muffled, amount);
        for (int offset = 0; offset < split.Length; offset += 137)
            b.ApplyEnvironment(split.AsSpan(offset, Math.Min(137, split.Length - offset)), SpatialEnvironment.Muffled, amount);
        continuous &= Bits(whole, split);
        if (amount <= .001f) bypass &= Bits(whole, source);
    }
    Check($"Changing intensity across bypass preserves partition independence/{rate}", continuous);
    Check($"Warm zero and threshold bypass preserve samples/{rate}", bypass);
    var warm = new SpatialEffectsEngine(rate); float[] small = Signal(rate, 257);
    for (int i = 0; i < 128; i++) warm.ApplyEnvironment(small, SpatialEnvironment.Muffled, .01f + i % 99 / 100f);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 128; i++) warm.ApplyEnvironment(small, SpatialEnvironment.Muffled, .01f + i % 99 / 100f);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check($"Coefficient changes allocate zero bytes at every supported test rate/{rate}", allocated == 0);
}

var emptySwitch = new SpatialEffectsEngine(22050);
emptySwitch.ApplyEnvironment(Span<float>.Empty, SpatialEnvironment.Muffled, .5f);
emptySwitch.ApplyEnvironment(Span<float>.Empty, SpatialEnvironment.LivingRoom, .25f);
float[] switchedSignal = Signal(22050, 22050), isolatedSignal = (float[])switchedSignal.Clone();
emptySwitch.ApplyEnvironment(switchedSignal, SpatialEnvironment.Muffled, .5f);
new SpatialEffectsEngine(22050).ApplyEnvironment(isolatedSignal, SpatialEnvironment.Muffled, .5f);
Check("Empty environment switches invalidate prepared cutoff and gain", Bits(switchedSignal, isolatedSignal));

foreach (int rate in new[] { 22050, 48000 })
{
    float[] source = Signal(rate, rate), expected = (float[])source.Clone();
    new SpatialEffectsEngine(rate).ApplyEnvironment(expected, SpatialEnvironment.Muffled, 1);
    float[][] concurrent = new float[8][];
    Parallel.For(0, concurrent.Length, i =>
    {
        concurrent[i] = (float[])source.Clone();
        new SpatialEffectsEngine(rate).ApplyEnvironment(concurrent[i], SpatialEnvironment.Muffled, 1);
    });
    Check($"Concurrent request isolation/{rate}", concurrent.All(x => Bits(expected, x)));
    VolumeShifter.ApplyVolume(expected, 16, rate);
    Check($"Shared final peak protection/{rate}", expected.All(float.IsFinite) && expected.Max(x => Math.Abs(x)) <= VolumeShifter.Ceiling + 1e-6);
    var engine = new SpatialEffectsEngine(rate); var old = new LegacySpatialEffectsEngine(rate);
    float[] buffer = new float[rate];
    for (int i = 0; i < 6; i++)
    {
        source.CopyTo(buffer, 0); engine.ApplyEnvironment(buffer, SpatialEnvironment.Muffled, 1);
        source.CopyTo(buffer, 0); old.ApplyEnvironment(buffer, SpatialEnvironment.Muffled, 1);
    }
    long startAllocation = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 10; i++)
    {
        source.CopyTo(buffer, 0);
        engine.ApplyEnvironment(buffer, SpatialEnvironment.Muffled, i % 2 == 0 ? .5f : 1);
    }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - startAllocation;
    Check($"No allocation, including cutoff changes/{rate}", allocated == 0);
    double[] currentMs = new double[21], oldMs = new double[21];
    for (int i = 0; i < currentMs.Length; i++)
    {
        source.CopyTo(buffer, 0); long time = Stopwatch.GetTimestamp();
        engine.ApplyEnvironment(buffer, SpatialEnvironment.Muffled, 1);
        currentMs[i] = Stopwatch.GetElapsedTime(time).TotalMilliseconds;
        source.CopyTo(buffer, 0); time = Stopwatch.GetTimestamp();
        old.ApplyEnvironment(buffer, SpatialEnvironment.Muffled, 1);
        oldMs[i] = Stopwatch.GetElapsedTime(time).TotalMilliseconds;
    }
    Array.Sort(currentMs); Array.Sort(oldMs);
    measurements.Add(new { Rate = rate, UpdatedMsPerAudioSecond = currentMs[10], PreviousMsPerAudioSecond = oldMs[10] });
}

var filter = SpatialFilterKernel.FromSlope(48000, SpatialFilterType.LowPass, 450);
filter.Process(.4f);
try { filter.SetLowPass(48000, 24000); Check("Nyquist cutoff is rejected", false); }
catch (ArgumentOutOfRangeException) { Check("Nyquist cutoff is rejected", true); }
var control = SpatialFilterKernel.FromSlope(48000, SpatialFilterType.LowPass, 450); control.Process(.4f);
Check("Rejected update preserves filter coefficients and history", filter.Process(0) == control.Process(0));
filter.SetLowPass(48000, 1000);
Check("A valid update retains nonzero history", filter.Process(0) != 0);

if (args.Length > 0)
{
    Directory.CreateDirectory(args[0]);
    File.WriteAllText(Path.Combine(args[0], "muffled-checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText(Path.Combine(args[0], "muffled-measurements.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine(JsonSerializer.Serialize(new { Checks = checks.Count, Failures = failures, Benchmarks = measurements.Where(x => x.GetType().GetProperty("UpdatedMsPerAudioSecond") != null) }));
return failures == 0 ? 0 : 1;

static bool Bits(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
[MethodImpl(MethodImplOptions.NoInlining)] static object Allocate() => new byte[37];
static float[] Signal(int rate, int count) => Enumerable.Range(0, count)
    .Select(i => (float)(.2 * Math.Sin(2 * Math.PI * 180 * i / rate) + .08 * Math.Sin(2 * Math.PI * 2300 * i / rate))).ToArray();
