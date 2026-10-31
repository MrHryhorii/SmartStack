using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using NAudio.Dsp;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class PipelineTailChecks
{
    private static readonly Type Pipeline = typeof(SpatialEffectsEngine).Assembly.GetType("ONNX_Runner.Services.Synthesis.AudioGenerationPipeline")!;
    private static readonly Type Resampler = Pipeline.Assembly.GetType("ONNX_Runner.Services.Synthesis.AudioOutputResampler")!;
    private static readonly MethodInfo Drain = Pipeline.GetMethod("DrainSpatialTail", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo Flush = Pipeline.GetMethod("FlushOutputVolume", BindingFlags.NonPublic | BindingFlags.Static)!;
    private delegate void WriteOutput(Span<float> samples, AudioStreamManager manager, object? resampler, BiQuadFilter? filter, VolumeShifter volume);
    private static readonly WriteOutput Write = BindWriter();

    internal static int Run(string? output)
    {
        int failures = 0; var rows = new List<object>();
        foreach (int rate in new[] { 8000, 16000, 22050, 48000 })
        foreach (float amount in new[] { .25f, 1f })
        foreach (float volume in new[] { .5f, 2.5118864f })
        foreach (int prefixBlock in new[] { 17, 4096 })
        {
            float[] input = LivingRoomChecks.Signal(rate, .1), initial = (float[])input.Clone();
            var engine = new SpatialEffectsEngine(rate); engine.ApplyEnvironment(initial, SpatialEnvironment.LivingRoom, amount);
            using var destination = new MemoryStream(); using var manager = new AudioStreamManager(AudioFormat.Pcm, rate, destination);
            var level = new VolumeShifter(rate, volume);
            for (int offset = 0; offset < initial.Length; offset += prefixBlock)
                Write(initial.AsSpan(offset, Math.Min(prefixBlock, initial.Length - offset)), manager, null, null, level);
            Drain.Invoke(null, [engine, SpatialEnvironment.LivingRoom, amount, rate, .005f, null, level, volume, manager, null, null, CancellationToken.None]);
            Flush.Invoke(null, [level, manager, CancellationToken.None]);
            int retained = checked((int)manager.SamplesWritten);
            float[] reference = new float[input.Length + rate * 4]; input.CopyTo(reference, 0);
            new SpatialEffectsEngine(rate).ApplyEnvironment(reference, SpatialEnvironment.LivingRoom, amount);
            float omitted = reference.AsSpan(retained).ToArray().Select(x => Math.Abs(x) * volume).DefaultIfEmpty().Max();
            float[] expected = reference.AsSpan(0, retained).ToArray(); VolumeShifter.ApplyVolume(expected, volume, rate);
            using var encoded = new MemoryStream(); using var expectedManager = new AudioStreamManager(AudioFormat.Pcm, rate, encoded);
            expectedManager.WriteChunk(expected);
            bool exact = encoded.ToArray().AsSpan().SequenceEqual(destination.ToArray());
            bool pass = exact && omitted < .00501f && retained - input.Length < rate * 4;
            if (!pass) { failures++; Console.WriteLine($"FAIL: actual pipeline tail/{rate}/{amount}/{volume}/{prefixBlock}"); }
            rows.Add(new { Rate = rate, Intensity = amount, Volume = volume, PrefixBlock = prefixBlock, Passed = pass,
                ExactPcmAfterSharedLimiterFlush = exact, OmittedPeak = omitted, TailSamples = retained - input.Length });
        }
        if (output != null) File.WriteAllText(Path.Combine(output, "actual-pipeline-tail.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { ActualPipelineTailChecks = rows.Count, Failures = failures }));
        return failures == 0 ? 0 : 1;
    }
    private static WriteOutput BindWriter()
    {
        var method = new DynamicMethod("WriteLivingRoomTestOutput", typeof(void), [typeof(Span<float>), typeof(AudioStreamManager), typeof(object), typeof(BiQuadFilter), typeof(VolumeShifter)], typeof(PipelineTailChecks).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Castclass, Resampler);
        il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Call, Pipeline.GetMethod("WriteOutput", BindingFlags.NonPublic | BindingFlags.Static)!); il.Emit(OpCodes.Ret);
        return method.CreateDelegate<WriteOutput>();
    }
}
