using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Dsp;
using NewEngine = ONNX_Runner.Services.AudioEffectsEngine;
using NewEffect = ONNX_Runner.Models.VoiceEffectType;
using NewEnvironment = ONNX_Runner.Models.SpatialEnvironment;
using Volume = ONNX_Runner.Services.VolumeShifter;
using NewManager = ONNX_Runner.Services.AudioStreamManager;
using NewResampler = ONNX_Runner.Services.AudioResampler;

internal static class OutputVolumeRegressionProgram
{
    private const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags HiddenInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type NewPipeline = typeof(NewEngine).Assembly.GetType("ONNX_Runner.Services.Synthesis.AudioGenerationPipeline")!;
    private static readonly Type NewResamplerType = typeof(NewEngine).Assembly.GetType("ONNX_Runner.Services.Synthesis.AudioOutputResampler")!;
    private static readonly NewWrite NewWriter = BindNewWriter();
    private static readonly Converter ConvertPcm = typeof(NewManager).GetMethod("ConvertFloatToPcm16", HiddenStatic)!.CreateDelegate<Converter>();
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly int[] Split = [1, 17, 509, 8192];
    private static int _checks;
    private static string? _report;
    private delegate int Operation(Span<float> samples);
    private delegate void NewWrite(Span<float> samples, NewManager manager, object? resampler, BiQuadFilter? filter, Volume volume);
    private delegate void Converter(ReadOnlySpan<float> samples, Span<short> destination);
    private delegate void Block(Span<float> samples);
    private sealed record Encoded(byte[] Bytes, long Frames);

    private static void Main(string[] args)
    {
        int report = Array.IndexOf(args, "--report");
        if (report >= 0 && report + 1 < args.Length) _report = args[report + 1];
        Basic("output-volume.json");
    }

    private static void Basic(string report)
    {
        var evidence = new List<object>();
        Span<float> tail = stackalloc float[256];
        foreach (int rate in Rates)
        {
            foreach (int target in new[] { 8000, 22050, 48000 })
            foreach (bool cleanup in new[] { false, true })
            foreach (float gain in new[] { 1f, 2.5118864f })
            foreach (int length in new[] { 0, 1, 17, rate / 3 + 19 })
            {
                float[] source = Enumerable.Range(0, length).Select(i => i % 313 < 17 ? 6f * MathF.Cos(i * .7f) : .3f * MathF.Sin(i * .13f)).ToArray();
                if (source.Length > 0) source[^1] = 16;
                float[] reference = Reference(source, rate, target, gain, cleanup);
                Encoded whole = ActualNew(source, rate, target, gain, cleanup, [int.MaxValue]);
                Encoded split = ActualNew(source, rate, target, gain, cleanup, Split);
                Require(whole.Bytes.AsSpan().SequenceEqual(split.Bytes), "Output partitions changed the final PCM16.");
                Require(whole.Bytes.AsSpan().SequenceEqual(Pcm(reference)), "The actual writer differs from filter -> resample -> gain/protection.");
                Require(whole.Frames == reference.Length && whole.Frames == whole.Bytes.Length / 2, "Finalization lost or duplicated frames.");
                Require(reference.All(x => float.IsFinite(x) && Math.Abs(x) <= .950002f), "A converted sample escaped protection.");
                Require(Math.Abs(reference.Length - source.Length * (double)target / rate) <= 1, "Output conversion changed duration.");
                _checks++;
                evidence.Add(new { Rate = rate, Target = target, Cleanup = cleanup, Gain = gain, Length = length, OutputFrames = whole.Frames, Peak = Peak(reference) });
            }
            foreach (int target in new[] { 8000, 22050, 48000 })
            foreach (float amount in new[] { .25f, 1f })
            {
                float[] source = Enumerable.Range(0, rate / 5 + 7).Select(i => i > rate / 5 - rate / 200 ? .2f * MathF.Cos(i * .7f) : 0).ToArray();
                float[] completed = EffectNew(source, rate, NewEffect.FmRadio, amount, true, Split);
                Require(completed.Length > source.Length, "The finite radio response was not retained.");
                Encoded actual = ActualNew(source, rate, target, 2.5118864f, true, Split, NewEffect.FmRadio, amount);
                float[] reference = Reference(completed, rate, target, 2.5118864f, true);
                Require(actual.Bytes.AsSpan().SequenceEqual(Pcm(reference)), "FM completion did not precede conversion and final protection.");
                Require(actual.Frames == reference.Length, "FM completion or final protection lost frames.");
                _checks++;
            }
            var processor = new Volume(rate, 2.5118864f);
            float[] buffer = new float[1024];
            for (int i = 0; i < 8; i++) processor.Process(buffer);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) processor.Process(buffer);
            while (processor.Flush(tail) > 0) { }
            Require(GC.GetAllocatedBytesForCurrentThread() == allocated, "The common volume hot path allocated.");
            _checks++;
        }
        var residuals = new List<object>();
        foreach (int rate in new[] { 8000, 22050, 48000 })
        {
            float[] source = Enumerable.Range(0, rate * 2).Select(i => .7f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate)).ToArray();
            float[] old = (float[])source.Clone();
            Volume.ApplyVolume(old, 3);
            float[] next = Reference(source, rate, rate, 3, false);
            double a = Residual(old, rate), b = Residual(next, rate);
            Require(b < -65 && b < a - 30, "The common limiter retained substantial static waveform distortion.");
            residuals.Add(new { Rate = rate, OldResidualDb = a, ProposedResidualDb = b });
            _checks++;
        }
        foreach (int length in new[] { 0, 1, 17, 4096 })
        {
            float[] source = Enumerable.Range(0, length).Select(i => .01f * MathF.Sin(i * .19f)).ToArray();
            foreach (float gain in new[] { 0f, .25f, 1f, 2.5118864f })
            {
                float[] expected = source.Select(x => x * gain).ToArray();
                float[] actual = Reference(source, 22050, 22050, gain, false);
                Require(Same(expected, actual), "Quiet output did not preserve exact linear gain and duration.");
                _checks++;
            }
        }
        VerifyCancellation();
        byte[] concurrentExpected = ActualNew(Tone(), 22050, 16000, 2.5118864f, true, Split, NewEffect.FmRadio, 1).Bytes;
        bool[] matches = new bool[8];
        Parallel.For(0, matches.Length, i => matches[i] = ActualNew(Tone(), 22050, 16000, 2.5118864f, true, Split, NewEffect.FmRadio, 1).Bytes.AsSpan().SequenceEqual(concurrentExpected));
        Require(matches.All(x => x), "Concurrent requests shared output state.");
        _checks++;
        Save(report, new { Passed = _checks, HardwareAccelerated = Vector.IsHardwareAccelerated,
            Scope = "Actual character-only output writer, same output with fragmented input, final resampler drain before limiter flush, PCM16 reference equality, finite sample peaks, FM completion with matching analysis buffers, linear quiet gain, cancellation and request isolation.", Residuals = residuals, Evidence = evidence });
        Console.WriteLine($"Passed {_checks} character-output integration checks.");
    }

    private static float[] Reference(float[] source, int rate, int target, float gain, bool cleanup)
    {
        float[] input = (float[])source.Clone();
        BiQuadFilter? filter = Filter(rate, cleanup);
        if (filter != null) for (int i = 0; i < input.Length; i++) input[i] = filter.Transform(input[i]);
        if (rate != target) input = Converted(input, rate, target);
        var volume = new Volume(target, gain);
        int count = volume.Process(input);
        while (count < input.Length) count += volume.Flush(input.AsSpan(count));
        return input;
    }

    private static Encoded ActualNew(float[] source, int rate, int target, float gain, bool cleanup, int[] blocks,
        NewEffect type = NewEffect.None, float amount = 0)
    {
        using var stream = new MemoryStream();
        using var manager = new NewManager(ONNX_Runner.Models.AudioFormat.Pcm, target, stream);
        object? resampler = rate == target ? null : NewResampler(rate, target);
        var volume = new Volume(target, gain);
        BiQuadFilter? filter = Filter(rate, cleanup);
        var engine = type == NewEffect.None ? null : new NewEngine(new(), rate);
        engine?.Reset(123456);
        float[] input = (float[])source.Clone();
        try
        {
            EachBlock(input, blocks, samples =>
            {
                engine?.ApplyEffect(samples, type, amount);
                NewWriter(samples, manager, resampler, filter, volume);
            });
            if (engine != null) Invoke(NewPipeline.GetMethod("DrainEffectTail", HiddenStatic)!, null,
                [engine, null, NewEnvironment.None, 0f, rate, null, volume, manager, resampler, filter, CancellationToken.None]);
            if (resampler != null) Invoke(NewResamplerType.GetMethod("Complete", HiddenInstance)!, resampler,
                [manager, volume, CancellationToken.None]);
            Invoke(NewPipeline.GetMethod("FlushOutputVolume", HiddenStatic)!, null, [volume, manager, CancellationToken.None]);
            return new(stream.ToArray(), manager.SamplesWritten);
        }
        finally { (resampler as IDisposable)?.Dispose(); }
    }

    private static float[] Converted(float[] input, int rate, int target)
    {
        var processor = new NewResampler(rate, target);
        var output = new List<float>();
        var block = new float[Math.Max(1, target / 100)];
        int offset = 0;
        while (offset < input.Length)
        {
            var result = processor.Process(input.AsSpan(offset), block, block.Length);
            offset += result.Consumed;
            for (int i = 0; i < result.Written; i++) output.Add(block[i]);
        }
        int count;
        do
        {
            count = processor.Complete(block, block.Length);
            for (int i = 0; i < count; i++) output.Add(block[i]);
        } while (count == block.Length);
        return output.ToArray();
    }

    private static float[] EffectNew(float[] source, int rate, NewEffect type, float amount, bool complete, int[]? blocks = null)
    {
        float[] result = (float[])source.Clone();
        var engine = new NewEngine(new(), rate);
        engine.Reset(123456);
        if (blocks == null) engine.ApplyEffect(result, type, amount);
        else EachBlock(result, blocks, samples => engine.ApplyEffect(samples, type, amount));
        if (!complete) return result;
        var output = new List<float>(result);
        Operation drain = typeof(NewEngine).GetMethod("Complete", HiddenInstance)!.CreateDelegate<Operation>(engine);
        float[] tail = new float[Math.Max(1, rate / 50)];
        int count;
        while ((count = drain(tail)) > 0)
        {
            for (int i = 0; i < count; i++) output.Add(tail[i]);
            Require(output.Count - source.Length < rate / 4, "Character completion did not terminate.");
        }
        return output.ToArray();
    }

    private static void VerifyCancellation()
    {
        var volume = new Volume(22050, 1);
        volume.Process([.1f]);
        using var stream = new MemoryStream();
        using var manager = new NewManager(ONNX_Runner.Models.AudioFormat.Pcm, 22050, stream);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool rejected = false;
        try { Invoke(NewPipeline.GetMethod("FlushOutputVolume", HiddenStatic)!, null, [volume, manager, cancellation.Token]); }
        catch (OperationCanceledException) { rejected = true; }
        Require(rejected && manager.SamplesWritten == 0, "Canceled finalization consumed or wrote retained volume output.");
        Span<float> tail = stackalloc float[1];
        Require(volume.Flush(tail) == 1 && tail[0] == .1f, "Cancellation discarded the pending sample.");
        _checks++;
        using var resampler = (IDisposable)NewResampler(22050, 8000);
        rejected = false;
        try { Invoke(NewResamplerType.GetMethod("Complete", HiddenInstance)!, resampler, [manager, new Volume(8000, 1), cancellation.Token]); }
        catch (OperationCanceledException) { rejected = true; }
        Require(rejected, "Canceled resampler finalization continued processing.");
        _checks++;
    }

    private static void EachBlock(float[] input, int[] blocks, Block process)
    {
        int offset = 0, step = 0;
        while (offset < input.Length)
        {
            int length = Math.Min(blocks[step++ % blocks.Length], input.Length - offset);
            process(input.AsSpan(offset, length));
            offset += length;
        }
    }
    private static object NewResampler(int rate, int target) => Activator.CreateInstance(NewResamplerType, HiddenInstance, null, [rate, target], null)!;
    private static BiQuadFilter? Filter(int rate, bool enabled) => enabled ? BiQuadFilter.LowPassFilter(rate, Math.Min(7500, rate / 2f - 10), .707f) : null;
    private static float[] Tone() => Enumerable.Range(0, 22050 / 5 + 7).Select(i => .3f * MathF.Sin(i * .19f)).ToArray();
    private static bool Same(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));
    private static float Peak(ReadOnlySpan<float> data) { float p = 0; foreach (float value in data) p = Math.Max(p, Math.Abs(value)); return p; }
    private static byte[] Pcm(float[] samples)
    {
        short[] result = new short[samples.Length];
        ConvertPcm(samples, result);
        return MemoryMarshal.AsBytes(result.AsSpan()).ToArray();
    }
    private static double Residual(float[] source, int rate)
    {
        double c = 0, s = 0, error = 0, power = 0;
        for (int i = rate / 2; i < rate + rate / 2; i++) { double p = 2 * Math.PI * 1000 * i / rate; c += 2 * source[i] * Math.Cos(p) / rate; s += 2 * source[i] * Math.Sin(p) / rate; }
        for (int i = rate / 2; i < rate + rate / 2; i++) { double p = 2 * Math.PI * 1000 * i / rate, expected = c * Math.Cos(p) + s * Math.Sin(p); error += Math.Pow(source[i] - expected, 2); power += expected * expected; }
        return 10 * Math.Log10(Math.Max(1e-30, error / power));
    }
    private static NewWrite BindNewWriter()
    {
        var method = new DynamicMethod("CallNewWriter", typeof(void), [typeof(Span<float>), typeof(NewManager), typeof(object), typeof(BiQuadFilter), typeof(Volume)], typeof(OutputVolumeRegressionProgram).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Castclass, NewResamplerType); il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Call, NewPipeline.GetMethod("WriteOutput", HiddenStatic)!); il.Emit(OpCodes.Ret);
        return method.CreateDelegate<NewWrite>();
    }
    private static object? Invoke(MethodInfo method, object? target, object?[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Save(string name, object data)
    {
        if (_report != null) File.WriteAllText(_report, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}
