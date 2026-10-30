using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using NAudio.Wave;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

int passed = 0, failed = 0, comparisons = 0, lengthDifferences = 0;
double maximumError = 0;
double? minimumSnr = null;
string? baselinePath = Option("--baseline");
Assembly? baseline = baselinePath == null ? null :
    new AssemblyLoadContext("resampler-baseline").LoadFromAssemblyPath(Path.GetFullPath(baselinePath));
Type? baselineType = baseline?.GetType("ONNX_Runner.Services.AudioResampler", throwOnError: true);
var benchmarks = new List<object>();
int[] rates = [8000, 11025, 12345, 16000, 22050, 24000, 44100, 48000];

foreach (int sourceRate in rates)
foreach (int targetRate in rates)
{
    Check($"Complete chunks, span boundaries, reset and duration {sourceRate}->{targetRate}", () =>
    {
        var resampler = new AudioResampler(sourceRate, targetRate);
        foreach (int length in new[] { 0, 1, 2, 7, 331, sourceRate / 3 + 1, sourceRate * 3 + 37 })
        {
            float[] source = Signal(length, sourceRate);
            float[] original = (float[])source.Clone();
            int capacity = resampler.GetOutputCapacity(source.Length);
            var destination = Enumerable.Repeat(9f, capacity + 2).ToArray();
            int written = resampler.Resample(source, destination.AsSpan(1, capacity));
            Require(destination[0] == 9 && destination[^1] == 9, "Destination boundaries were overwritten.");
            Require(source.AsSpan().SequenceEqual(original), "Input PCM was modified.");
            Require(Math.Abs(written - length * (double)targetRate / sourceRate) <= 1,
                "Complete conversion changed the intended duration by more than one sample.");
            Require(destination.AsSpan(1, written).ToArray().All(float.IsFinite), "Non-finite output.");
            var pooled = resampler.Resample(source, source.Length);
            try
            {
                Require(pooled.Length == written && pooled.Buffer.AsSpan(0, written).SequenceEqual(destination.AsSpan(1, written)),
                    "Array and destination-span overloads disagree or chunk state leaked.");
                if (sourceRate == targetRate)
                    Require(source.AsSpan().SequenceEqual(pooled.Buffer.AsSpan(0, pooled.Length)), "Passthrough changed PCM.");
                ComparePrevious(source, sourceRate, targetRate, pooled.Buffer.AsSpan(0, pooled.Length));
            }
            finally { ArrayPool<float>.Shared.Return(pooled.Buffer); }
        }
    });

    Check($"Streaming partitions, valid output region and final drain {sourceRate}->{targetRate}", () =>
    {
        foreach (int length in new[] { 0, 1, 2, 7, 159, 220, 481, sourceRate + 37 })
        {
            float[] source = Signal(length, sourceRate);
            float[] whole = Stream(source, sourceRate, targetRate, [Math.Max(1, length)]);
            float[] split = Stream(source, sourceRate, targetRate, [1, 7, 97, 641]);
            Require(whole.AsSpan().SequenceEqual(split), "Input partitions changed streamed PCM.");
            Require(Math.Abs(split.Length - length * (double)targetRate / sourceRate) <= 1, "Streaming duration drifted.");
            if (baselineType != null)
            {
                float[] previous = Stream(source, sourceRate, targetRate, [1, 7, 97, 641], previous: true);
                Require(previous.AsSpan().SequenceEqual(split), "Streaming PCM differs from the previous implementation.");
            }
        }
    });
}

Check("Invalid arguments and undersized output are rejected before conversion", () =>
{
    Expect<ArgumentOutOfRangeException>(() => new AudioResampler(0, 16000));
    Expect<ArgumentOutOfRangeException>(() => new AudioResampler(22050, -1));
    var r = new AudioResampler(22050, 48000);
    Expect<ArgumentOutOfRangeException>(() => r.GetOutputCapacity(-1));
    Expect<OverflowException>(() => r.GetOutputCapacity(int.MaxValue));
    Expect<ArgumentNullException>(() => r.Resample(null!, 1));
    Expect<ArgumentOutOfRangeException>(() => r.Resample(new float[2], 3));
    float[] input = Signal(1000, 22050);
    Expect<ArgumentException>(() => r.Resample(input, new float[r.GetOutputCapacity(input.Length) - 1]));
    float[] alias = new float[5000];
    Expect<ArgumentException>(() => r.Resample(alias.AsSpan(0, 100), alias.AsSpan(0, 1000)));
    var expected = new AudioResampler(22050, 48000).Resample(input);
    var actual = r.Resample(input);
    try { Require(expected.Length == actual.Length && expected.Buffer.AsSpan(0, expected.Length).SequenceEqual(actual.Buffer.AsSpan(0, actual.Length)),
        "Failed validation changed DSP state."); }
    finally { ArrayPool<float>.Shared.Return(expected.Buffer); ArrayPool<float>.Shared.Return(actual.Buffer); }
});

Check("Passthrough spans support overlap without allocating or changing samples", () =>
{
    var r = new AudioResampler(22050, 22050);
    float[] data = [1, 2, 3, 4, 5];
    Require(r.Resample(data.AsSpan(0, 4), data.AsSpan(1, 4)) == 4 && data.AsSpan().SequenceEqual([1, 1, 2, 3, 4]),
        "Overlapping passthrough did not use memmove semantics.");
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 1000; i++) r.Resample(data, data.AsSpan());
    Require(GC.GetAllocatedBytesForCurrentThread() == before, "Passthrough span conversion allocated.");
});

Check("Streaming mode rejects size changes and complete-chunk calls", () =>
{
    var r = new AudioResampler(22050, 16000);
    float[] output = new float[160];
    r.Process([.1f], output.AsSpan());
    Expect<ArgumentException>(() => r.Process([.1f], new float[161].AsSpan()));
    Expect<InvalidOperationException>(() => r.Resample([.1f]));
    r.Complete(output.AsSpan());
    Expect<InvalidOperationException>(() => r.Process([.1f], output.AsSpan()));
    Expect<ArgumentOutOfRangeException>(() => new AudioResampler(22050, 16000).Complete(Span<float>.Empty));
});

foreach (var (from, to) in new[] { (22050, 16000), (22050, 48000), (48000, 22050) })
{
    Check($"Warm streaming remains allocation-free {from}->{to}", () =>
    {
        var r = new AudioResampler(from, to);
        float[] input = Signal(1024, from), output = new float[Math.Max(1, to / 100)];
        void Feed()
        {
            int offset = 0;
            while (offset < input.Length) offset += r.Process(input.AsSpan(offset), output.AsSpan()).Consumed;
        }
        for (int i = 0; i < 100; i++) Feed();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Feed();
        Require(GC.GetAllocatedBytesForCurrentThread() == before, "Warm streaming allocated managed memory.");
    });
    Check($"Complete conversion has bounded internal allocation {from}->{to}", () =>
    {
        var r = new AudioResampler(from, to);
        foreach (int seconds in new[] { 3, 30 })
        {
            float[] input = Signal(from * seconds + 37, from);
            float[] output = new float[r.GetOutputCapacity(input.Length)];
            r.Resample(input, output);
            long before = GC.GetAllocatedBytesForCurrentThread();
            r.Resample(input, output);
            Require(GC.GetAllocatedBytesForCurrentThread() - before < 65536,
                "WDL allocated a duration-sized temporary input buffer.");
        }
    });
}

foreach (var (rate, channels, floating, length) in new[]
{
    (22050, 1, false, 22137), (22050, 2, false, 22137),
    (48000, 1, true, 48137), (48000, 2, true, 48137),
    (8000, 1, false, 262144), (8000, 1, false, 262181), (22050, 1, false, 0)
})
{
    Check($"Direct WAV decoding, downmix and growth {rate}/{channels}/{floating}/{length}", () =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"tsubaki-resampler-{Guid.NewGuid():N}.wav");
        float[] source = Signal(length, rate);
        try
        {
            var format = floating ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels) : new WaveFormat(rate, 16, channels);
            using (var writer = new WaveFileWriter(path, format))
                foreach (float sample in source)
                    for (int ch = 0; ch < channels; ch++) writer.WriteSample(sample);
            var processor = new AudioProcessor(new ToneConfig { Data = new ToneDataConfig { FilterLength = 1024, HopLength = 256 } });
            foreach (int target in new[] { rate, 16000 })
            {
                var loaded = processor.LoadAndNormalizeWav(path, target);
                var expected = new AudioResampler(rate, target).Resample(source);
                try
                {
                    Require(loaded.Length == expected.Length, "WAV conversion changed duration.");
                    for (int i = 0; i < loaded.Length; i++)
                        Require(Math.Abs(loaded.Buffer[i] - expected.Buffer[i]) < 2f / 32768, "Decoded WAV content changed beyond source quantization.");
                }
                finally { ArrayPool<float>.Shared.Return(loaded.Buffer); ArrayPool<float>.Shared.Return(expected.Buffer); }
            }
        }
        finally { File.Delete(path); }
    });
}

foreach (string voice in Options("--voice"))
{
    Check($"Complete reference PCM comparison: {Path.GetFileName(voice)}", () =>
    {
        using var reader = new AudioFileReader(voice);
        Require(reader.WaveFormat.Channels == 1, "Comparison reference must be mono.");
        float[] source = new float[checked((int)(reader.Length / sizeof(float)))];
        int count = 0, read;
        while (count < source.Length && (read = reader.Read(source.AsSpan(count))) > 0) count += read;
        foreach (int target in new[] { 8000, 16000, 22050, 24000, 44100, 48000 })
        {
            var output = new AudioResampler(reader.WaveFormat.SampleRate, target).Resample(source.AsSpan(0, count));
            try { ComparePrevious(source[..count], reader.WaveFormat.SampleRate, target, output.Buffer.AsSpan(0, output.Length)); }
            finally { ArrayPool<float>.Shared.Return(output.Buffer); }
        }
    });
}

Check("Independent resamplers preserve request isolation under concurrency", () =>
{
    float[] source = Signal(22087, 22050);
    var expected = new AudioResampler(22050, 48000).Resample(source);
    try
    {
        Parallel.For(0, 16, _ =>
        {
            var actual = new AudioResampler(22050, 48000).Resample(source);
            try { Require(actual.Length == expected.Length && actual.Buffer.AsSpan(0, actual.Length).SequenceEqual(expected.Buffer.AsSpan(0, expected.Length)),
                "One request affected another request's resampler state."); }
            finally { ArrayPool<float>.Shared.Return(actual.Buffer); }
        });
    }
    finally { ArrayPool<float>.Shared.Return(expected.Buffer); }
});

if (args.Contains("--benchmark") && baselineType != null && failed == 0)
{
    foreach (var (from, to) in new[] { (22050, 16000), (22050, 48000), (48000, 22050) })
    {
        float[] input = Signal(from * 3 + 37, from);
        var current = new AudioResampler(from, to);
        var old = PreviousComplete(from, to);
        Benchmark($"complete-{from}-{to}", input.Length / (double)from,
            () => Return(old(input, input.Length)), () => Return(current.Resample(input)));
        var oldState = Activator.CreateInstance(baselineType, from, to)!;
        var oldProcess = baselineType.GetMethod("Process")!.CreateDelegate<ProcessArray>(oldState);
        var newState = new AudioResampler(from, to);
        var output = new float[Math.Max(1, to / 100)];
        void FeedPrevious()
        {
            int offset = 0;
            while (offset < input.Length) offset += oldProcess(input.AsSpan(offset), output, output.Length).Consumed;
        }
        void FeedCurrent()
        {
            int offset = 0;
            while (offset < input.Length) offset += newState.Process(input.AsSpan(offset), output.AsSpan()).Consumed;
        }
        Benchmark($"stream-{from}-{to}", input.Length / (double)from, FeedPrevious, FeedCurrent);
    }
}

Console.WriteLine($"Resampler: {passed}/{passed + failed} checks passed; {comparisons} previous-PCM comparisons; max error {maximumError:G8}; minimum SNR {minimumSnr:F2} dB; {lengthDifferences} one-sample endpoint differences.");
if (Option("--report") is string report)
    File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = passed, Failed = failed, PreviousComparisons = comparisons,
        MaximumAbsoluteError = maximumError, MinimumSnrDb = minimumSnr, OneSampleLengthDifferences = lengthDifferences,
        Benchmark = benchmarks }, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;

string? Option(string name) { int i = Array.IndexOf(args, name); return i < 0 || i + 1 >= args.Length ? null : args[i + 1]; }
IEnumerable<string> Options(string name) { for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) yield return args[i + 1]; }
void Check(string name, Action action)
{
    try { action(); passed++; }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
Func<float[], int, (float[] Buffer, int Length)> PreviousComplete(int from, int to)
{
    object state = Activator.CreateInstance(baselineType!, from, to)!;
    return baselineType!.GetMethod("Resample")!.CreateDelegate<Func<float[], int, (float[] Buffer, int Length)>>(state);
}
void ComparePrevious(float[] source, int from, int to, ReadOnlySpan<float> actual)
{
    if (baselineType == null) return;
    var previous = PreviousComplete(from, to)(source, source.Length);
    try
    {
        comparisons++;
        Require(Math.Abs(previous.Length - actual.Length) <= 1, "Complete output duration differs by more than one sample.");
        if (previous.Length != actual.Length) lengthDifferences++;
        double error = 0, power = 0;
        for (int i = 0; i < Math.Min(previous.Length, actual.Length); i++)
        {
            double x = previous.Buffer[i], e = actual[i] - x;
            maximumError = Math.Max(maximumError, Math.Abs(e));
            error += e * e; power += x * x;
        }
        double? snr = error > 0 && power > 0 ? 10 * Math.Log10(power / error) : null;
        if (snr.HasValue) minimumSnr = Math.Min(minimumSnr ?? double.PositiveInfinity, snr.Value);
        Require(snr is null or > 100, $"Complete PCM error exceeded the fidelity budget: {snr:F2} dB SNR.");
    }
    finally { ArrayPool<float>.Shared.Return(previous.Buffer); }
}
float[] Stream(float[] input, int from, int to, int[] partitions, bool previous = false)
{
    var r = new AudioResampler(from, to);
    ProcessSpan process = r.Process;
    CompleteSpan complete = r.Complete;
    if (previous)
    {
        object state = Activator.CreateInstance(baselineType!, from, to)!;
        ProcessArray oldProcess = baselineType!.GetMethod("Process")!.CreateDelegate<ProcessArray>(state);
        Func<float[], int, int> oldComplete = baselineType.GetMethod("Complete")!.CreateDelegate<Func<float[], int, int>>(state);
        float[] oldOutput = new float[Math.Max(1, to / 100)];
        process = (source, output) => { var result = oldProcess(source, oldOutput, oldOutput.Length); oldOutput.AsSpan(0, result.Written).CopyTo(output); return result; };
        complete = output => { int count = oldComplete(oldOutput, oldOutput.Length); oldOutput.AsSpan(0, count).CopyTo(output); return count; };
    }
    var buffer = Enumerable.Repeat(7f, Math.Max(1, to / 100) + 2).ToArray();
    var result = new List<float>();
    int offset = 0, part = 0;
    while (offset < input.Length)
    {
        int remaining = Math.Min(partitions[part++ % partitions.Length], input.Length - offset);
        while (remaining > 0)
        {
            var block = process(input.AsSpan(offset, remaining), buffer.AsSpan(1, buffer.Length - 2));
            Require(block.Consumed > 0, "Streaming made no progress.");
            for (int i = 0; i < block.Written; i++) result.Add(buffer[i + 1]);
            remaining -= block.Consumed; offset += block.Consumed;
        }
    }
    int written, drains = 0;
    do
    {
        written = complete(buffer.AsSpan(1, buffer.Length - 2));
        for (int i = 0; i < written; i++) result.Add(buffer[i + 1]);
        Require(++drains < 100, "Completion did not drain.");
    } while (written == buffer.Length - 2);
    Require(complete(buffer.AsSpan(1, buffer.Length - 2)) == 0, "Final samples were repeated.");
    Require(buffer[0] == 7 && buffer[^1] == 7, "Streaming wrote beyond its output span.");
    return result.ToArray();
}
void Benchmark(string name, double audioSeconds, Action previous, Action current)
{
    for (int i = 0; i < 20; i++) { previous(); current(); }
    var rows = new List<(double OldMs, double NewMs, double OldBytes, double NewBytes)>();
    for (int round = 0; round < 21; round++)
    {
        var a = Measure(round % 2 == 0 ? previous : current);
        var b = Measure(round % 2 == 0 ? current : previous);
        rows.Add(round % 2 == 0 ? (a.Ms, b.Ms, a.Bytes, b.Bytes) : (b.Ms, a.Ms, b.Bytes, a.Bytes));
    }
    double Median(IEnumerable<double> values) => values.Order().ElementAt(rows.Count / 2);
    double oldMs = Median(rows.Select(x => x.OldMs)) / audioSeconds;
    double newMs = Median(rows.Select(x => x.NewMs)) / audioSeconds;
    double oldBytes = Median(rows.Select(x => x.OldBytes)), newBytes = Median(rows.Select(x => x.NewBytes));
    double pairedRatio = Median(rows.Select(x => x.NewMs / x.OldMs));
    benchmarks.Add(new { Name = name, PreviousMsPerAudioSecond = oldMs, CurrentMsPerAudioSecond = newMs,
        PreviousBytesPerClip = oldBytes, CurrentBytesPerClip = newBytes, PairedTimeRatio = pairedRatio,
        Rounds = rows.Select(x => new { x.OldMs, x.NewMs, x.OldBytes, x.NewBytes }).ToArray() });
    Console.WriteLine($"{name}: {oldMs:F6}->{newMs:F6} ms/audio-second; {oldBytes:F0}->{newBytes:F0} bytes/clip; paired {pairedRatio:F4}");
}
(double Ms, double Bytes) Measure(Action action)
{
    long startBytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
    int count = 0;
    do { action(); count++; } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 40);
    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    return (elapsed / count, (GC.GetAllocatedBytesForCurrentThread() - startBytes) / (double)count);
}
void Return((float[] Buffer, int Length) result) => ArrayPool<float>.Shared.Return(result.Buffer);
float[] Signal(int length, int rate)
{
    var result = new float[length];
    var random = new Random(12345);
    for (int i = 0; i < length; i++)
        result[i] = (float)(.25 * Math.Sin(2 * Math.PI * 177 * i / rate) + .15 * Math.Sin(2 * Math.PI * 2403 * i / rate) + .03 * (random.NextDouble() - .5));
    return result;
}

delegate (int Consumed, int Written) ProcessSpan(ReadOnlySpan<float> input, Span<float> output);
delegate int CompleteSpan(Span<float> output);
delegate (int Consumed, int Written) ProcessArray(ReadOnlySpan<float> input, float[] output, int length);
