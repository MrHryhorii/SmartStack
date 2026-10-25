using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Dsp;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class FmCompletionRegressionProgram
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type Pipeline = typeof(AudioEffectsEngine).Assembly.GetType(
        "ONNX_Runner.Services.Synthesis.AudioGenerationPipeline")!;
    private static readonly MethodInfo DrainEffect = Pipeline.GetMethod("DrainEffectTail", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo DrainSpatial = Pipeline.GetMethod("DrainSpatialTail", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo FlushLimiter = Pipeline.GetMethod("FlushOutputVolume", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly List<object> Evidence = [];
    private static int _checks, _failures;

    private delegate int SpanOperation(Span<float> samples);
    private delegate void OutputWrite(Span<float> samples, AudioStreamManager manager, BiQuadFilter? filter, VolumeShifter volume);
    private sealed record PcmInput(string Name, int Rate, string Group, string Sha256);
    private sealed record EffectOutput(float[] Samples, int TailFrames);
    private sealed record ChainOutput(byte[] Bytes, int TailFrames);
    private sealed record Digest(string Name, int Rate, float Amount, string Sha256, double Energy);

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--record")
        {
            Record(args[1], args[2]);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--corpus")
        {
            Corpus(args[1], args[2]);
            return 0;
        }
        bool characterOnly = args.Contains("--character-only");
        FmMixingChecks.Run(Check);
        FmVoicePathChecks.Run(Check);
        PeakLimiterChecks.Run(Check);
        foreach (int rate in Rates)
        {
            foreach (float amount in new[] { .01f, .25f, .5f, .75f, 1f })
            foreach (int length in new[] { 1, 17, rate / 5 })
            foreach (int pause in new[] { 0, rate / 200, 3 * rate / 10 })
                Check($"FM completion retains the full response at {rate} Hz, strength {amount}, input {length}, pause {pause}",
                    () => VerifyCompletion(rate, amount, length, pause));

            Check($"An empty completion preserves pending radio state at {rate} Hz", () =>
            {
                var engine = new AudioEffectsEngine(new(), rate);
                engine.ApplyEffect(new[] { .1f }, VoiceEffectType.FmRadio, .5f);
                Func<bool> pending = Pending(engine);
                SpanOperation complete = Completer(engine);
                Require(pending() && complete(Span<float>.Empty) == 0 && pending(), "An empty destination changed retained audio.");
                engine.Reset(123456);
                Require(!pending() && complete(new float[17]) == 0, "Reset retained a previous request tail.");
            });
            Check($"FM completion allocates no memory while draining at {rate} Hz", () =>
            {
                var engine = new AudioEffectsEngine(new(), rate);
                var complete = Completer(engine);
                engine.ApplyEffect(new[] { .1f }, VoiceEffectType.FmRadio, 1f);
                Span<float> scratch = stackalloc float[17];
                complete(scratch);
                long before = GC.GetAllocatedBytesForCurrentThread();
                while (complete(scratch) > 0) { }
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "Completion allocated while processing.");
            });
        }
        Check("Other character effects do not generate new request tails", () =>
        {
            foreach (VoiceEffectType type in Enum.GetValues<VoiceEffectType>().Where(x => x != VoiceEffectType.FmRadio))
            {
                var engine = new AudioEffectsEngine(new(), 22050);
                engine.Reset(123456);
                engine.ApplyEffect(LateBurst(22050, 441), type, 1f);
                Require(!Pending(engine)() && Completer(engine)(new float[441]) == 0,
                    $"Completion unexpectedly advanced {type}.");
            }
            var inactive = new AudioEffectsEngine(new(), 22050);
            inactive.ApplyEffect(new[] { .1f }, VoiceEffectType.FmRadio, 0);
            Require(!Pending(inactive)() && Completer(inactive)(new float[1]) == 0, "Bypassed radio retained audio.");
        });
        foreach (int rate in new[] { 8000, 22050, 48000 })
        foreach (float amount in new[] { .25f, 1f })
        foreach (SpatialEnvironment environment in characterOnly ? new[] { SpatialEnvironment.None } : new[] { SpatialEnvironment.None, SpatialEnvironment.Forest, SpatialEnvironment.Cave })
        foreach (bool profiles in new[] { false, true })
            Check($"The pipeline drains FM before {environment}, profiles {profiles}, {rate} Hz, strength {amount}",
                () => VerifyChain(rate, amount, environment, profiles, rate));

        foreach (int targetRate in new[] { 8000, 16000, 48000 })
            Check($"FM completion shares output resampling to {targetRate} Hz",
                () => VerifyChain(22050, .5f, characterOnly ? SpatialEnvironment.None : SpatialEnvironment.Forest, true, targetRate));

        Check("Effect draining checks cancellation before touching retained output", () =>
        {
            var engine = new AudioEffectsEngine(new(), 22050);
            engine.ApplyEffect(new[] { .2f }, VoiceEffectType.FmRadio, 1f);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var stream = new MemoryStream();
            using var manager = new AudioStreamManager(AudioFormat.Pcm, 22050, stream);
            bool canceled = false;
            try { Invoke(DrainEffect, [engine, null, SpatialEnvironment.None, 0f, 22050, null, new VolumeShifter(22050, 1), manager, null, null, cancellation.Token]); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled && manager.SamplesWritten == 0 && Pending(engine)(), "Cancellation consumed or wrote audio.");
        });
        Check("Effect draining checks cancellation between output blocks", () =>
        {
            var engine = new AudioEffectsEngine(new(), 22050);
            engine.ApplyEffect(new[] { .2f }, VoiceEffectType.FmRadio, 1f);
            using var cancellation = new CancellationTokenSource();
            using var stream = new CancelingStream(cancellation);
            using var manager = new AudioStreamManager(AudioFormat.Pcm, 22050, stream);
            bool canceled = false;
            try { Invoke(DrainEffect, [engine, null, SpatialEnvironment.None, 0f, 22050, null, new VolumeShifter(22050, 1), manager, null, null, cancellation.Token]); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled && manager.SamplesWritten == 441 - new VolumeShifter(22050, 1).LatencySamples, "Draining continued after cancellation.");
        });
        Check("Concurrent radio requests retain independent completion and limiter state", () =>
        {
            byte[] expected = Chain(22050, .5f, characterOnly ? SpatialEnvironment.None : SpatialEnvironment.Forest, true, 16000, false).Bytes;
            bool[] results = new bool[8];
            Parallel.For(0, results.Length, i => results[i] =
                Chain(22050, .5f, characterOnly ? SpatialEnvironment.None : SpatialEnvironment.Forest, true, 16000, false).Bytes.AsSpan().SequenceEqual(expected));
            Require(results.All(x => x), "Independent requests shared DSP state.");
        });
        Console.WriteLine($"Result: {_checks - _failures}/{_checks} FM completion and output policy checks passed");
        int report = Array.IndexOf(args, "--report");
        if (report >= 0 && report + 1 < args.Length)
            Save(args[report + 1], new { Passed = _checks - _failures, Failed = _failures, CharacterOnly = characterOnly, Evidence });
        return _failures == 0 ? 0 : 1;
    }

    private static void VerifyCompletion(int rate, float amount, int length, int pause)
    {
        float[] source = LateBurst(rate, length).Concat(new float[pause]).ToArray();
        EffectOutput whole = Render(source, rate, amount, source.Length, rate / 50);
        EffectOutput split = Render(source, rate, amount, 37, 1);
        Require(Same(whole.Samples, split.Samples), "Chunk or completion partitions changed PCM.");
        Require(whole.TailFrames == split.TailFrames && whole.TailFrames < rate / 4, "Invalid completion duration.");
        float[] reference = source.Concat(new float[rate / 4]).ToArray();
        new AudioEffectsEngine(new(), rate).ApplyEffect(reference, VoiceEffectType.FmRadio, amount);
        Require(Same(whole.Samples, reference.AsSpan(0, whole.Samples.Length)), "Completed audio differs from a zero-padded stream.");
        float omitted = Peak(reference.AsSpan(whole.Samples.Length));
        Require(omitted < 1.001e-7f, $"Completion omitted output above its floor: {omitted:R}.");
        if (pause == 0)
            Require(whole.TailFrames > 0 && Peak(whole.Samples.AsSpan(source.Length)) > 1e-5f, "The late input response is still missing.");
        if (pause == 3 * rate / 10)
            Require(whole.TailFrames == 0, "A fully drained sentence pause gained another tail.");
        Evidence.Add(new { Rate = rate, Amount = amount, InputFrames = length, PauseFrames = pause, whole.TailFrames, OmittedPeak = omitted });
    }

    private static EffectOutput Render(float[] source, int rate, float amount, int block, int drain)
    {
        var engine = new AudioEffectsEngine(new(), rate);
        var complete = Completer(engine);
        var pending = Pending(engine);
        float[] prefix = (float[])source.Clone();
        for (int offset = 0; offset < prefix.Length; offset += block)
            engine.ApplyEffect(prefix.AsSpan(offset, Math.Min(block, prefix.Length - offset)), VoiceEffectType.FmRadio, amount);
        var output = new List<float>(prefix);
        float[] scratch = new float[Math.Max(1, drain)];
        int count;
        while ((count = complete(scratch)) > 0)
        {
            for (int i = 0; i < count; i++) output.Add(scratch[i]);
            Require(output.Count - prefix.Length < rate / 4, "Completion did not converge.");
        }
        Require(!pending() && complete(scratch) == 0, "Repeated completion was not empty.");
        return new(output.ToArray(), output.Count - prefix.Length);
    }

    private static void VerifyChain(int rate, float amount, SpatialEnvironment environment, bool profiles, int targetRate)
    {
        ChainOutput actual = Chain(rate, amount, environment, profiles, targetRate, false);
        ChainOutput padded = Chain(rate, amount, environment, profiles, targetRate, true);
        Require(actual.TailFrames > 0 && actual.TailFrames == padded.TailFrames, "The pipeline failed to drain retained radio input.");
        Require(actual.Bytes.AsSpan().SequenceEqual(padded.Bytes), "Pipeline completion order changed retained PCM or spatial reflections.");
    }

    private static ChainOutput Chain(int rate, float amount, SpatialEnvironment environment, bool profiles, int targetRate, bool padded)
    {
        float[] source = LateBurst(rate, rate / 5);
        int tail = Render(source, rate, amount, source.Length, rate / 50).TailFrames;
        float[] input = padded ? source.Concat(new float[tail]).ToArray() : source;
        var engine = new AudioEffectsEngine(new(), rate);
        var spatial = environment == SpatialEnvironment.None ? null : new SpatialEffectsEngine(rate);
        AudioEqualizer? equalizer = profiles ? AudioEqualizer.Create(["telephony-class-application"], rate) : null;
        BiQuadFilter? filter = profiles ? BiQuadFilter.LowPassFilter(rate, Math.Min(1800, rate * .45f), .707f) : null;
        const float volume = 2.5118864f;
        var outputVolume = new VolumeShifter(targetRate, volume);
        using var output = new MemoryStream();
        using var manager = new AudioStreamManager(AudioFormat.Pcm, targetRate, output);
        object? resampler = null;
        OutputWrite? writeResampled = null;
        if (targetRate != rate)
        {
            Type type = Pipeline.Assembly.GetType("ONNX_Runner.Services.Synthesis.AudioOutputResampler")!;
            resampler = Activator.CreateInstance(type, Hidden, null, [rate, targetRate], null);
            writeResampled = type.GetMethod("Write", Hidden)!.CreateDelegate<OutputWrite>(resampler);
        }
        try
        {
            for (int offset = 0; offset < input.Length; offset += 137)
            {
                var samples = input.AsSpan(offset, Math.Min(137, input.Length - offset));
                engine.ApplyEffect(samples, VoiceEffectType.FmRadio, amount);
                spatial?.ApplyEnvironment(samples, environment, .25f);
                equalizer?.Apply(samples);
                if (writeResampled != null) writeResampled(samples, manager, filter, outputVolume);
                else
                {
                    if (filter != null)
                        for (int i = 0; i < samples.Length; i++) samples[i] = filter.Transform(samples[i]);
                    int written = outputVolume.Process(samples);
                    manager.WriteChunk(samples[..written]);
                }
            }
            if (!padded)
                Invoke(DrainEffect, [engine, spatial, environment, .25f, rate, equalizer, outputVolume, manager, resampler, filter, CancellationToken.None]);
            if (spatial != null)
                Invoke(DrainSpatial, [spatial, environment, .25f, rate, .005f, equalizer, outputVolume, volume, manager, resampler, filter, CancellationToken.None]);
            resampler?.GetType().GetMethod("Complete", Hidden)!.Invoke(resampler, [manager, outputVolume, CancellationToken.None]);
            Invoke(FlushLimiter, [outputVolume, manager, CancellationToken.None]);
            return new(output.ToArray(), tail);
        }
        finally { (resampler as IDisposable)?.Dispose(); }
    }

    private static void Record(string inputDirectory, string report)
    {
        var digests = new List<Digest>();
        foreach (var input in Inputs(inputDirectory))
        foreach (VoiceEffectType type in new[] { VoiceEffectType.FmRadio, VoiceEffectType.Telephone, VoiceEffectType.Bitcrusher })
        foreach (float amount in new[] { 0f, .01f, .25f, .5f, .75f, 1f })
        {
            float[] source = ReadInput(inputDirectory, input);
            var engine = new AudioEffectsEngine(new(), input.Rate);
            engine.Reset(123456);
            engine.ApplyEffect(source, type, amount);
            digests.Add(new(input.Name + ":" + type, input.Rate, amount,
                Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(source.AsSpan()))), Energy(source)));
        }
        Save(report, digests);
        Console.WriteLine($"Recorded {digests.Count} unchanged-prefix PCM digests.");
    }

    private static void Corpus(string inputDirectory, string report)
    {
        var rows = new List<object>();
        foreach (var input in Inputs(inputDirectory))
        foreach (float amount in new[] { .01f, .25f, .5f, .75f, 1f })
        {
            float[] source = ReadInput(inputDirectory, input);
            EffectOutput result = Render(source, input.Rate, amount, 509, 137);
            float[] reference = source.Concat(new float[input.Rate / 4]).ToArray();
            new AudioEffectsEngine(new(), input.Rate).ApplyEffect(reference, VoiceEffectType.FmRadio, amount);
            Require(Same(result.Samples, reference.AsSpan(0, result.Samples.Length)), "Corpus completion differs from zero-padded reference.");
            float omitted = Peak(reference.AsSpan(result.Samples.Length));
            Require(omitted < 1.001e-7f, "Corpus completion stopped too early.");
            rows.Add(new { input.Name, input.Rate, Amount = amount, result.TailFrames, OmittedPeak = omitted });
        }
        Save(report, new { Passed = rows.Count, Rows = rows, FreshNeuralSynthesis = false });
        Console.WriteLine($"Verified {rows.Count} captured-audio FM completion cases.");
    }

    private static PcmInput[] Inputs(string directory) =>
        JsonSerializer.Deserialize<PcmInput[]>(File.ReadAllText(Path.Combine(directory, "inputs.json")))!;

    private static float[] ReadInput(string directory, PcmInput input)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(directory, input.Name + ".f32"));
        Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == input.Sha256, "Captured input hash changed.");
        float[] samples = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        if (input.Group == "reference")
            for (int i = 0; i < samples.Length; i++) samples[i] *= .39810717f;
        return samples;
    }

    private static SpanOperation Completer(AudioEffectsEngine engine) =>
        typeof(AudioEffectsEngine).GetMethod("Complete", Hidden)!.CreateDelegate<SpanOperation>(engine);
    private static Func<bool> Pending(AudioEffectsEngine engine) =>
        typeof(AudioEffectsEngine).GetProperty("HasPendingOutput", Hidden)!.GetMethod!.CreateDelegate<Func<bool>>(engine);

    private static void Invoke(MethodInfo method, object?[] args)
    {
        try { method.Invoke(null, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static float[] LateBurst(int rate, int count)
    {
        var result = new float[count];
        int start = Math.Max(0, count - Math.Max(1, rate / 200));
        for (int i = start; i < count; i++)
            result[i] = .1f * (float)Math.Cos(2 * Math.PI * 1000 * (i - start) / rate);
        return result;
    }
    private static bool Same(ReadOnlySpan<float> a, ReadOnlySpan<float> b) =>
        MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));
    private static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (float sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }
    private static double Energy(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float sample in samples) sum += (double)sample * sample;
        return sum;
    }
    private static void Check(string name, Action action)
    {
        _checks++;
        try { action(); }
        catch (Exception error) { _failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Save(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private sealed class CancelingStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        { base.Write(buffer, offset, count); cancellation.Cancel(); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { base.Write(buffer); cancellation.Cancel(); }
    }
}
