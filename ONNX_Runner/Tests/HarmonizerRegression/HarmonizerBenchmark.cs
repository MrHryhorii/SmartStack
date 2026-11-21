using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ONNX_Runner.Services.Audio.Effects;

internal static class HarmonizerBenchmark
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Block = 1024, Rounds = 15;
    private delegate int Processing(Span<float> samples, float amount);
    private delegate int Completion(Span<float> samples);
    private delegate int Rendering(ReadOnlySpan<float> input, Span<float> output);

    internal static int Run(string original, string previous, string output, string? inputWave = null)
    {
        using var before = new Version("Original", original);
        using var first = new Version("Previous", previous);
        using var final = new Version("Current", typeof(AudioEffectsEngine).Assembly.Location);
        Version[] versions = [before, first, final];
        var results = new List<object>();
        var quality = new List<object>();
        float[]? speech = null;
        int speechRate = 0;
        if (inputWave != null)
        {
            using var reader = new WaveFileReader(inputWave);
            if (reader.WaveFormat.Channels != 1) throw new ArgumentException("Provide a mono speech WAV.", nameof(inputWave));
            speechRate = reader.WaveFormat.SampleRate;
            speech = new float[checked((int)(reader.Length / reader.WaveFormat.BlockAlign))];
            var provider = reader.ToSampleProvider();
            int read = 0, count;
            while ((count = provider.Read(speech.AsSpan(read))) > 0) read += count;
            if (read != speech.Length || read == 0) throw new InvalidDataException("The speech WAV has no complete PCM payload.");
        }

        foreach (string scope in new[] { "CompleteProcessor", "MixAndCorrectionOnly" })
        foreach (int rate in new[] { 22050, 48000 })
        foreach (float amount in new[] { .25f, .5f, 1f })
        {
            float[] source = Source(rate, rate * 16, speech, speechRate);
            if (scope == "CompleteProcessor")
            {
                foreach (Version version in versions)
                {
                    float[] pcm = Capture(version, source, rate, amount);
                    quality.Add(new { Version = version.Name, Rate = rate, Amount = amount, Samples = pcm.Length,
                        Sha256 = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pcm.AsSpan()))),
                        Levels = LevelErrors(source, pcm, rate) });
                }
            }
            var samples = versions.Select(_ => new List<double>()).ToArray();
            var allocations = versions.Select(_ => new List<long>()).ToArray();
            for (int round = -2; round < Rounds; round++)
            for (int index = 0; index < versions.Length; index++)
            {
                int version = (round + 2 + index) % versions.Length;
                var timing = Trial(versions[version], source, rate, amount, scope);
                if (round < 0) continue;
                samples[version].Add(timing.Milliseconds);
                allocations[version].Add(timing.AllocatedBytes);
            }
            double baseline = Median(samples[0]);
            var measurements = versions.Select((version, i) => new
            {
                Version = version.Name,
                MedianMs = Median(samples[i]),
                P25Ms = Quantile(samples[i], .25),
                P75Ms = Quantile(samples[i], .75),
                TimeReductionPercent = 100 * (1 - Median(samples[i]) / baseline),
                MaximumManagedBytes = allocations[i].Max(),
                SamplesMs = samples[i]
            }).ToArray();
            results.Add(new { Scope = scope, Rate = rate, Amount = amount,
                AudioSeconds = scope == "CompleteProcessor" ? 16 : 128, Measurements = measurements });
            Console.WriteLine($"{scope}, {rate} Hz, {amount}: " + string.Join(", ",
                measurements.Select(x => $"{x.Version} {x.MedianMs:F3} ms ({x.TimeReductionPercent:+0.0;-0.0;0.0}%)")));
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Rounds, WarmupRounds = 2, BlockSamples = Block,
            Input = inputWave == null ? "Deterministic voiced signal" : "Repeated mono speech WAV",
            InputSha256 = inputWave == null ? null : Hash(inputWave),
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "Runtime default",
            OriginalSha256 = Hash(original), PreviousSha256 = Hash(previous),
            CurrentSha256 = Hash(typeof(AudioEffectsEngine).Assembly.Location),
            Method = "Isolated copies of the actual processors, rotated order, warmed code, construction excluded. CompleteProcessor includes native pitch shifting and completion. MixAndCorrectionOnly invokes the actual renderer with prepared harmony buffers; its inputs are synthetic. Timing and allocation samples exclude input preparation.",
            Results = results, SpeechPcm = quality
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static float[] Capture(Version version, float[] source, int rate, float amount)
    {
        using var driver = version.Create(rate);
        float[] input = (float[])source.Clone(), output = new float[source.Length], scratch = new float[Block];
        int written = 0;
        for (int offset = 0; offset < input.Length; offset += Block)
        {
            Span<float> block = input.AsSpan(offset, Math.Min(Block, input.Length - offset));
            int ready = driver.Process(block, amount);
            block[..ready].CopyTo(output.AsSpan(written));
            written += ready;
        }
        int tail;
        while ((tail = driver.Complete(scratch)) > 0)
        {
            scratch.AsSpan(0, tail).CopyTo(output.AsSpan(written));
            written += tail;
        }
        if (written != source.Length || output.Any(x => !float.IsFinite(x)))
            throw new InvalidOperationException("A benchmark version changed duration or produced non-finite speech PCM.");
        return output;
    }

    private static object LevelErrors(float[] source, float[] output, int rate)
    {
        int window = Math.Max(1, rate / 50);
        var powers = new List<(double Reference, double Output)>();
        for (int offset = 0; offset + window <= source.Length; offset += window)
        {
            double referencePower = 0, outputPower = 0;
            for (int i = offset; i < offset + window; i++)
            {
                referencePower += source[i] * (double)source[i];
                outputPower += output[i] * (double)output[i];
            }
            powers.Add((referencePower / window, outputPower / window));
        }
        double floor = Math.Max(1e-8, powers.Max(x => x.Reference) * .01);
        List<double> errors = powers.Where(x => x.Reference > floor && x.Output > 0)
            .Select(x => 10 * Math.Log10(x.Output / x.Reference)).ToList();
        if (errors.Count == 0) throw new InvalidOperationException("The benchmark source has no active speech windows.");
        return new { WindowMs = 20, ReferenceFloorDbBelowPeakWindow = 20, ActiveWindows = errors.Count,
            MeanAbsoluteDb = errors.Average(Math.Abs), P10Db = Quantile(errors, .1), P90Db = Quantile(errors, .9),
            MinimumDb = errors.Min(), MaximumDb = errors.Max() };
    }

    private static (double Milliseconds, long AllocatedBytes) Trial(Version version, float[] source, int rate, float amount, string scope)
    {
        using var driver = version.Create(rate);
        driver.Process(Span<float>.Empty, amount);
        float[] copy = (float[])source.Clone();
        float[] scratch = new float[Block];
        float[] input = Source(rate, Block, null, 0);
        if (scope == "MixAndCorrectionOnly")
        {
            driver.PrepareVoices(rate);
            for (int i = 0; i < 64; i++) driver.Render(input, scratch);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        if (scope == "CompleteProcessor")
        {
            int ready = 0;
            for (int offset = 0; offset < copy.Length; offset += Block)
                ready += driver.Process(copy.AsSpan(offset, Math.Min(Block, copy.Length - offset)), amount);
            int tail;
            while ((tail = driver.Complete(scratch)) > 0) ready += tail;
            if (ready != source.Length) throw new InvalidOperationException("A benchmark version changed PCM duration.");
        }
        else
        {
            for (int offset = 0; offset < rate * 128; offset += Block)
                driver.Render(input.AsSpan(0, Math.Min(Block, rate * 128 - offset)), scratch);
        }
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return (elapsed, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    private static float[] Source(int rate, int length, float[]? speech, int speechRate)
    {
        float[] source = new float[length];
        for (int i = 0; i < length; i++)
        {
            if (speech != null)
            {
                double position = i * (double)speechRate / rate % speech.Length;
                int index = (int)position;
                source[i] = (float)(speech[index] + (speech[(index + 1) % speech.Length] - speech[index]) * (position - index));
                continue;
            }
            double t = i / (double)rate;
            source[i] = (float)((.15 * Math.Sin(2 * Math.PI * 180 * t) + .05 * Math.Sin(2 * Math.PI * 830 * t)) *
                (.6 + .4 * Math.Sin(2 * Math.PI * 3 * t)));
        }
        return source;
    }

    private static double Median(List<double> samples) => Quantile(samples, .5);
    private static double Quantile(List<double> samples, double position)
    {
        double[] sorted = samples.Order().ToArray();
        return sorted[(int)Math.Round((sorted.Length - 1) * position)];
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class Version : IDisposable
    {
        private readonly AssemblyLoadContext _context;
        private readonly Type _type;
        internal string Name { get; }

        internal Version(string name, string path)
        {
            Name = name;
            _context = new AssemblyLoadContext($"HarmonizerBenchmark-{name}", isCollectible: true);
            _type = _context.LoadFromAssemblyPath(Path.GetFullPath(path))
                .GetType("ONNX_Runner.Services.Audio.Effects.HarmonizerProcessor", throwOnError: true)!;
        }

        internal Driver Create(int rate) => new(Activator.CreateInstance(_type, Hidden, null, [rate], null)!);
        public void Dispose() => _context.Unload();
    }

    private sealed class Driver : IDisposable
    {
        private readonly object _processor;
        internal Processing Process { get; }
        internal Completion Complete { get; }
        internal Rendering Render { get; }

        internal Driver(object processor)
        {
            _processor = processor;
            Type type = processor.GetType();
            Process = type.GetMethod("Process", Hidden)!.CreateDelegate<Processing>(processor);
            Complete = type.GetMethod("Complete", Hidden)!.CreateDelegate<Completion>(processor);
            Render = type.GetMethod("Render", Hidden)!.CreateDelegate<Rendering>(processor);
        }

        internal void PrepareVoices(int rate)
        {
            foreach (var (field, semitones) in new[] { ("_thirdOutput", 4), ("_fifthOutput", 7) })
            {
                float[] samples = (float[])_processor.GetType().GetField(field, Hidden)!.GetValue(_processor)!;
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (float)(.15 * Math.Sin(2 * Math.PI * 180 * Math.Pow(2, semitones / 12d) * i / rate));
            }
        }

        public void Dispose() => ((IDisposable)_processor).Dispose();
    }
}
