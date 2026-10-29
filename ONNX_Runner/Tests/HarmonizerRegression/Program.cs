using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Dsp;
using ONNX_Runner.Models;
using ONNX_Runner.Services;
using Signalsmith;

internal static class HarmonizerRegression
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type Pipeline = typeof(AudioEffectsEngine).Assembly.GetType(
        "ONNX_Runner.Services.Synthesis.AudioGenerationPipeline")!;
    private static readonly List<object> Evidence = [];
    private static int _checks, _failures;
    private delegate int Completion(Span<float> samples);
    private delegate void OutputWrite(Span<float> samples, AudioStreamManager manager, BiQuadFilter? filter, VolumeShifter volume);

    private static int Main(string[] args)
    {
        foreach (int rate in new[] { 8000, 16000, 22050, 24000, 44100, 48000, 96000, 192000 })
        foreach (int length in new[] { 1, 17, rate / 5 })
        foreach (float amount in new[] { .25f, 1f })
            Check($"Duration and chunk independence: {rate} Hz, {length} frames, {amount}", () =>
            {
                float[] source = Voice(rate, length);
                float[] whole = Render(source, rate, amount, source.Length, rate);
                float[] split = Render(source, rate, amount, 37, 1);
                Require(whole.Length == source.Length && Same(whole, split), "Duration or PCM changed with partitions.");
                Require(whole.All(float.IsFinite), "Non-finite output.");
            });

        foreach (int rate in new[] { 16000, 22050, 48000 })
            Check($"The selected B profile is preserved at {rate} Hz", () =>
            {
                float[] source = Voice(rate, rate + 17);
                float[] full = ReferenceB(source, rate);
                float[] actual = Render(source, rate, 1, 257, 17);
                Require(Same(full, actual), "The streaming processor changed selected profile B.");
                float[] half = (float[])full.Clone();
                new StreamingPowerMatchedMixer(rate).Mix(source, half, .5f);
                Require(Same(half, Render(source, rate, .5f, 113, 7)), "Intensity changed the wet character or dry alignment.");
            });

        Check("All bypasses preserve PCM and avoid native state", () =>
        {
            float[] source = Voice(22050, 1000);
            foreach (float amount in new[] { 0f, .001f })
            foreach (bool enabled in new[] { false, true })
            {
                using var engine = new AudioEffectsEngine(new() { EnableGlobalEffects = enabled }, 22050);
                float[] samples = (float[])source.Clone();
                Require(engine.ApplyEffect(samples, VoiceEffectType.Harmonizer, amount) == samples.Length && Same(samples, source), "Bypass changed PCM.");
                Require(typeof(AudioEffectsEngine).GetField("_harmonizer", Hidden)!.GetValue(engine) == null && !Pending(engine)(), "Bypass allocated or retained native state.");
            }
            using var empty = new AudioEffectsEngine(new(), 22050);
            Require(empty.ApplyEffect(Span<float>.Empty, VoiceEffectType.Harmonizer, 1) == 0 && !Pending(empty)(), "Empty input retained audio.");
        });

        Check("Empty completion does not finalize a partially primed request", () =>
        {
            using var engine = new AudioEffectsEngine(new(), 22050);
            var complete = Completer(engine);
            engine.ApplyEffect(new[] { .2f }, VoiceEffectType.Harmonizer, .5f);
            Require(Pending(engine)() && complete(Span<float>.Empty) == 0 && Pending(engine)(), "Empty completion consumed audio.");
            engine.ApplyEffect(new[] { .1f }, VoiceEffectType.Harmonizer, .5f);
            Require(Drain(engine, 1).Length == 2, "Completion lost a short request.");
        });

        Check("Reset and effect changes release native state without retaining old audio", () =>
        {
            using var engine = new AudioEffectsEngine(new(), 22050);
            float[] source = Voice(22050, 4096);
            float[] first = RenderWith(engine, source, .5f, 257, 11);
            engine.Reset(123456);
            Require(!Pending(engine)(), "Reset retained old output.");
            Require(Same(first, RenderWith(engine, source, .5f, 31, 1)), "Reset changed deterministic harmony.");
            engine.Reset();
            engine.ApplyEffect(new[] { .2f }, VoiceEffectType.Harmonizer, 1);
            engine.ApplyEffect(new[] { .1f }, VoiceEffectType.Telephone, 1);
            Require(typeof(AudioEffectsEngine).GetField("_harmonizer", Hidden)!.GetValue(engine) == null, "Changing effects retained native harmony state.");
        });

        Check("Native state is disposed idempotently, including incomplete requests", () =>
        {
            var engine = new AudioEffectsEngine(new(), 22050);
            engine.ApplyEffect(new[] { .2f }, VoiceEffectType.Harmonizer, 1);
            engine.Dispose(); engine.Dispose();
            Require(!Pending(engine)(), "Disposed request retained output.");
            Throws<ObjectDisposedException>(() => engine.ApplyEffect(new[] { .1f }, VoiceEffectType.Harmonizer, 1));
            Throws<ObjectDisposedException>(() => engine.Reset());
        });

        Check("Warm processing and completion allocate no managed memory", () =>
        {
            using var engine = new AudioEffectsEngine(new(), 22050);
            float[] block = Voice(22050, 1024);
            for (int i = 0; i < 20; i++) engine.ApplyEffect(block, VoiceEffectType.Harmonizer, .5f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) engine.ApplyEffect(block, VoiceEffectType.Harmonizer, .5f);
            Require(before == GC.GetAllocatedBytesForCurrentThread(), "Warm processing allocated.");
            var complete = Completer(engine);
            before = GC.GetAllocatedBytesForCurrentThread();
            while (complete(block) > 0) { }
            Require(before == GC.GetAllocatedBytesForCurrentThread(), "Completion allocated.");
        });

        foreach (float amount in new[] { .01f, .5f, 1f })
        foreach (int targetRate in new[] { 16000, 22050, 48000 })
        foreach (bool profiles in new[] { false, true })
            Check($"Output stages retain harmony: {amount}, {targetRate} Hz, profiles {profiles}", () =>
            {
                byte[] whole = Chain(amount, targetRate, profiles, 22050);
                byte[] split = Chain(amount, targetRate, profiles, 137);
                Require(whole.AsSpan().SequenceEqual(split), "Output stages changed with character partitions.");
                Require(whole.Length > 0, "Output chain is empty.");
                if (targetRate == 22050) Require(whole.Length == 22050 * 2, "The pipeline changed PCM duration.");
            });

        Check("Cancellation preserves pending audio until request disposal", () =>
        {
            using var engine = new AudioEffectsEngine(new(), 22050);
            engine.ApplyEffect(new[] { .2f }, VoiceEffectType.Harmonizer, .5f);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            using var stream = new MemoryStream();
            using var manager = new AudioStreamManager(AudioFormat.Pcm, 22050, stream);
            bool canceled = false;
            try { Invoke("DrainEffectTail", [engine, null, SpatialEnvironment.None, 0f, 22050, null, new VolumeShifter(22050, 1), manager, null, null, cancellation.Token]); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled && manager.SamplesWritten == 0 && Pending(engine)(), "Cancellation consumed or wrote harmony.");
        });

        Check("Concurrent requests have independent native, gain and completion state", () =>
        {
            float[] source = Voice(22050, 22050);
            float[] expected = Render(source, 22050, .5f, 257, 17);
            Parallel.For(0, 8, i => Require(Same(expected, Render(source, 22050, .5f, 31 + i, 11 + i)), "Request state leaked."));
        });

        Console.WriteLine($"Harmonizer: {_checks - _failures}/{_checks} checks passed.");
        if (args.Length > 0) File.WriteAllText(args[0], JsonSerializer.Serialize(new { Passed = _checks - _failures, Failed = _failures, Evidence }, new JsonSerializerOptions { WriteIndented = true }));
        return _failures == 0 ? 0 : 1;
    }

    internal static float[] Render(float[] input, int rate, float amount, int block, int drain)
    {
        using var engine = new AudioEffectsEngine(new(), rate);
        return RenderWith(engine, input, amount, block, drain);
    }

    private static float[] RenderWith(AudioEffectsEngine engine, float[] input, float amount, int block, int drain)
    {
        var output = new List<float>(input.Length);
        float[] copy = (float[])input.Clone();
        for (int offset = 0; offset < copy.Length; offset += Math.Max(1, block))
        {
            Span<float> samples = copy.AsSpan(offset, Math.Min(block, copy.Length - offset));
            int ready = engine.ApplyEffect(samples, VoiceEffectType.Harmonizer, amount);
            Require(ready >= 0 && ready <= samples.Length, "Invalid ready prefix.");
            output.AddRange(samples[..ready]);
        }
        output.AddRange(Drain(engine, drain));
        return output.ToArray();
    }

    private static float[] Drain(AudioEffectsEngine engine, int block)
    {
        var output = new List<float>();
        var complete = Completer(engine);
        float[] scratch = new float[Math.Max(1, block)];
        int ready;
        while ((ready = complete(scratch)) > 0) output.AddRange(scratch.AsSpan(0, ready));
        Require(!Pending(engine)(), "Completion did not finish.");
        return output.ToArray();
    }

    internal static float[] ReferenceB(float[] input, int rate)
    {
        float[] output = new float[input.Length];
        foreach (int semitones in new[] { 0, 4, 7 })
        {
            float[] voice = input;
            if (semitones != 0)
            {
                using var shift = new Stretch(42);
                shift.PresetDefault(1, rate, false);
                shift.SetTransposeSemitones(semitones, 1);
                shift.SetFormantSemitones(0, true);
                shift.SetFormantBase(0);
                int pre = shift.InputLatency(), post = shift.OutputLatency();
                float[] complete = new float[input.Length + pre + post];
                shift.Process(input, complete.AsSpan(0, input.Length));
                shift.Process(new float[pre], complete.AsSpan(input.Length, pre));
                shift.Flush(complete.AsSpan(input.Length + pre, post), 1);
                voice = complete.AsSpan(pre + post, input.Length).ToArray();
            }
            for (int i = 0; i < output.Length; i++) output[i] += (float)(voice[i] * (1 / Math.Sqrt(3)));
        }
        var level = new WindowedPowerMatcher(rate);
        for (int i = 0; i < output.Length; i++) output[i] = level.Process(input[i], output[i]);
        return output;
    }

    private static byte[] Chain(float amount, int targetRate, bool profiles, int block)
    {
        const int rate = 22050;
        float[] input = Voice(rate, rate);
        using var engine = new AudioEffectsEngine(new(), rate);
        AudioEqualizer? equalizer = profiles ? AudioEqualizer.Create(["telephony-class-application"], rate) : null;
        BiQuadFilter? filter = profiles ? BiQuadFilter.LowPassFilter(rate, 1800, .707f) : null;
        var volume = new VolumeShifter(targetRate, 2.5118864f);
        using var stream = new MemoryStream();
        using var manager = new AudioStreamManager(AudioFormat.Pcm, targetRate, stream);
        object? resampler = null;
        OutputWrite? write = null;
        if (targetRate != rate)
        {
            Type type = Pipeline.Assembly.GetType("ONNX_Runner.Services.Synthesis.AudioOutputResampler")!;
            resampler = Activator.CreateInstance(type, Hidden, null, [rate, targetRate], null);
            write = type.GetMethod("Write", Hidden)!.CreateDelegate<OutputWrite>(resampler);
        }
        try
        {
            for (int offset = 0; offset < input.Length; offset += block)
            {
                Span<float> samples = input.AsSpan(offset, Math.Min(block, input.Length - offset));
                int ready = engine.ApplyEffect(samples, VoiceEffectType.Harmonizer, amount);
                samples = samples[..ready];
                equalizer?.Apply(samples);
                if (write != null) write(samples, manager, filter, volume);
                else
                {
                    if (filter != null) for (int i = 0; i < samples.Length; i++) samples[i] = filter.Transform(samples[i]);
                    ready = volume.Process(samples);
                    manager.WriteChunk(samples[..ready]);
                }
            }
            Invoke("DrainEffectTail", [engine, null, SpatialEnvironment.None, 0f, rate, equalizer, volume, manager, resampler, filter, CancellationToken.None]);
            resampler?.GetType().GetMethod("Complete", Hidden)!.Invoke(resampler, [manager, volume, CancellationToken.None]);
            Invoke("FlushOutputVolume", [volume, manager, CancellationToken.None]);
            return stream.ToArray();
        }
        finally { (resampler as IDisposable)?.Dispose(); }
    }

    private static float[] Voice(int rate, int length)
    {
        float[] samples = Enumerable.Range(0, length).Select(i =>
            (float)(.15 * Math.Sin(2 * Math.PI * 180 * i / rate) + .05 * Math.Sin(2 * Math.PI * 830 * i / rate)) *
            (i < length / 8 ? 0f : 1f)).ToArray();
        if (length > 0) samples[^1] += .3f;
        return samples;
    }

    private static Completion Completer(AudioEffectsEngine engine) => typeof(AudioEffectsEngine).GetMethod("Complete", Hidden)!.CreateDelegate<Completion>(engine);
    private static Func<bool> Pending(AudioEffectsEngine engine) => typeof(AudioEffectsEngine).GetProperty("HasPendingOutput", Hidden)!.GetMethod!.CreateDelegate<Func<bool>>(engine);
    private static bool Same(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void Invoke(string name, object?[] arguments)
    {
        try { Pipeline.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); }
    }
    private static void Check(string name, Action test)
    {
        _checks++;
        try { test(); Evidence.Add(new { Name = name, Passed = true }); }
        catch (Exception exception) { _failures++; Console.WriteLine($"FAIL {name}: {exception.Message}"); Evidence.Add(new { Name = name, Passed = false, Error = exception.Message }); }
    }
}
