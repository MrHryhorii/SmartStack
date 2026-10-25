using System.Runtime.InteropServices;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

int checks = 0, failures = 0;
TelephoneMixingChecks.Run(Check);
TelephoneCurveChecks.Run(Check);
PeakLimiterChecks.Run(Check);

Check("Signal interpolation preserves endpoints and does not normalize samples", () =>
{
    float[] dry = [2f, -.5f, .125f], wet = [-1f, .75f, .5f];
    float[] actual = (float[])wet.Clone();
    SignalInterpolator.Mix(dry, actual, 0);
    Require(Same(dry, actual), "The dry endpoint changed.");
    actual = (float[])wet.Clone();
    SignalInterpolator.Mix(dry, actual, 1);
    Require(Same(wet, actual), "The wet endpoint changed.");
    SignalInterpolator.Mix(dry, actual, .25f);
    Require(actual.SequenceEqual(new[] { 1.25f, -.1875f, .21875f }), "Interpolation applied an unrelated gain or peak policy.");
});
Check("Power restoration works without a mixer or dry branch", () =>
{
    float[] source = [1f, -1f, .5f, -.5f];
    double original = PowerMatcher.Measure(source);
    float[] attenuated = source.Select(x => x * .125f).ToArray();
    Require(PowerMatcher.Match(attenuated, original).Matched, "A nonzero signal could not be restored.");
    Require(Same(source, attenuated), "Power restoration required signal mixing or altered the waveform.");
});
Check("The same power service restores Ring output without mixing clean samples", () =>
{
    const int rate = 22050;
    float[] dry = Enumerable.Range(0, rate).Select(i => .1f * (float)Math.Sin(2 * Math.PI * 193 * i / rate)).ToArray();
    float[] ring = (float[])dry.Clone();
    var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456);
    engine.ApplyEffect(ring, VoiceEffectType.RingModulator, .5f);
    float[] before = (float[])ring.Clone();
    double reference = PowerMatcher.Measure(dry);
    var result = PowerMatcher.Match(ring, reference);
    Require(result.Matched && Math.Abs(10 * Math.Log10(PowerMatcher.Measure(ring) / reference)) < 1e-6,
        "Power restoration did not reach the independent dry reference.");
    for (int i = 0; i < ring.Length; i++)
        Require(ring[i] == (float)(before[i] * result.Gain), "Power restoration mixed a clean signal into Ring output.");
});
Check("Power matching handles silence and reports complete cancellation", () =>
{
    float[] silence = new float[17];
    Require(PowerMatcher.Match(silence, 0).Matched && silence.All(x => x == 0), "Silence was changed.");
    Require(!PowerMatcher.Match(silence, 1).Matched && silence.All(x => x == 0), "Energy was fabricated from silence.");
    float[] dry = [1f, -.5f], inverse = [-1f, .5f];
    Require(!PowerMatchedMixer.Mix(dry, inverse, .5f).Matched && inverse.All(x => x == 0),
        "Exact antiphase cancellation was not reported.");
    float[] wet = [1f, -.5f];
    Require(PowerMatchedMixer.Mix(new float[2], wet, .25f).Matched, "A silent endpoint broke matching.");
    Require(Math.Abs(PowerMatcher.Measure(wet) - .15625) < 1e-9, "Silent-endpoint power is incorrect.");
});
Check("Signal stages reject invalid positions, powers and partial overlaps", () =>
{
    MustThrow(() => SignalInterpolator.Mix(new float[2], new float[1], .5f));
    MustThrow(() => PowerMatchedMixer.Mix(new float[1], new float[1], float.NaN));
    MustThrow(() => PowerMatchedMixer.Mix(new float[1], new float[1], 1.1f));
    MustThrow(() => PowerMatcher.Match(new float[1], -1));
    MustThrow(() => PowerMatcher.Match(new float[1], double.PositiveInfinity));
    MustThrow(() => PowerMatcher.Target(-1, 1, .5f));
    MustThrow(() => LinearGain.Apply(new float[1], float.NaN));
    float[] overlap = [1f, 2f, 3f];
    MustThrow(() => PowerMatchedMixer.Mix(overlap.AsSpan(0, 2), overlap.AsSpan(1, 2), .5f));
});
Check("Linear gain remains exact and permits internal floating-point headroom", () =>
{
    float[] samples = [-.75f, .125f, .5f];
    LinearGain.Apply(samples, 4);
    Require(samples.SequenceEqual(new[] { -3f, .5f, 2f }), "Linear gain compressed or clipped samples.");
});
Check("Output adapter equals independently composed gain and limiting", () =>
{
    foreach (int rate in new[] { 8000, 22050, 48000 })
    foreach (int length in new[] { 0, 1, 17, rate / 3 })
    {
        float[] dry = Enumerable.Range(0, length).Select(i => i % 37 == 0 ? 8f : .2f * MathF.Sin(i * .37f)).ToArray();
        float[] expected = Render(dry, rate, false), actual = Render(dry, rate, true);
        Require(Same(expected, actual), "The output adapter coupled gain and peak algorithms.");
    }
});
Check("Equalizer output remains protected with continuous chunk state", () =>
{
    const int rate = 22050;
    float[] source = Enumerable.Range(0, rate).Select(i => .8f * (float)Math.Sin(2 * Math.PI * 2500 * i / rate)).ToArray();
    float[] expected = Equalized(source.Length), actual = Equalized(257);
    Require(Same(expected, actual) && actual.All(x => float.IsFinite(x) && Math.Abs(x) <= .950002f),
        "Equalizer or output protection lost state or allowed peaks through.");

    float[] Equalized(int segment)
    {
        var equalizer = AudioEqualizer.Create(["wearable-class-device", "wearable-class-device"], rate)!;
        var volume = new VolumeShifter(rate, 2.5118864f);
        float[] output = new float[source.Length]; int written = 0;
        for (int position = 0; position < source.Length; position += segment)
        {
            float[] block = source.AsSpan(position, Math.Min(segment, source.Length - position)).ToArray();
            equalizer.Apply(block);
            int count = volume.Process(block);
            block.AsSpan(0, count).CopyTo(output.AsSpan(written)); written += count;
        }
        Span<float> tail = stackalloc float[256]; int ready;
        while ((ready = volume.Flush(tail)) > 0) { tail[..ready].CopyTo(output.AsSpan(written)); written += ready; }
        Require(written == source.Length, "Equalized output duration changed.");
        return output;
    }
});
Check("The warmed Telephone and output chain allocates no managed bytes", () =>
{
    const int rate = 22050;
    var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456);
    var volume = new VolumeShifter(rate, 2.5118864f);
    float[] source = Enumerable.Range(0, 4096).Select(i => .2f * MathF.Sin(i * .19f)).ToArray();
    float[] work = new float[source.Length];
    for (int i = 0; i < 16; i++) Process();
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 32; i++) Process();
    Require(GC.GetAllocatedBytesForCurrentThread() == before, "The warmed combined chain allocated.");

    void Process()
    {
        source.CopyTo(work, 0);
        engine.ApplyEffect(work, VoiceEffectType.Telephone, .5f);
        volume.Process(work);
    }
});
Check("An output adapter cannot mutate input after completion", () =>
{
    var volume = new VolumeShifter(22050, 4);
    volume.Flush(new float[1]);
    float[] source = [.25f], before = (float[])source.Clone();
    bool rejected = false;
    try { volume.Process(source); }
    catch (InvalidOperationException) { rejected = true; }
    Require(rejected && Same(source, before), "Completed processing accepted or modified input.");
});
Check("Independent requests retain separate effect and limiter state", () =>
{
    float[] source = Enumerable.Range(0, 11025).Select(i => .2f * MathF.Sin(i * .17f)).ToArray();
    float[] expected = Process(source);
    bool[] same = new bool[12];
    Parallel.For(0, same.Length, i => same[i] = Same(expected, Process(source)));
    Require(same.All(x => x), "Request state leaked across concurrent processing.");

    static float[] Process(float[] source)
    {
        float[] output = (float[])source.Clone();
        var engine = new AudioEffectsEngine(new(), 22050); engine.Reset(123456);
        engine.ApplyEffect(output, VoiceEffectType.Telephone, .5f);
        VolumeShifter.ApplyVolume(output, 2.5118864f, 22050);
        return output;
    }
});

Console.WriteLine($"Signal pipeline: {checks - failures}/{checks} passed.");
return failures == 0 ? 0 : 1;

void Check(string name, Action run)
{
    checks++;
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void MustThrow(Action run)
{
    try { run(); }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException("Invalid input was accepted.");
}

static bool Same(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));

static float[] Render(float[] source, int rate, bool useAdapter)
{
    var volume = new VolumeShifter(rate, 2.5118864f);
    var limiter = new LookaheadPeakLimiter(rate);
    float[] output = new float[source.Length]; int written = 0;
    for (int position = 0; position < source.Length; position += 37)
    {
        float[] block = source.AsSpan(position, Math.Min(37, source.Length - position)).ToArray();
        int count;
        if (useAdapter) count = volume.Process(block);
        else { LinearGain.Apply(block, 2.5118864f); count = limiter.Process(block); }
        block.AsSpan(0, count).CopyTo(output.AsSpan(written)); written += count;
    }
    Span<float> tail = stackalloc float[256]; int ready;
    while ((ready = useAdapter ? volume.Flush(tail) : limiter.Flush(tail)) > 0)
    {
        tail[..ready].CopyTo(output.AsSpan(written)); written += ready;
    }
    Require(written == source.Length, "Output duration changed.");
    return output;
}
