using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Services.Cloning;
using ONNX_Runner.Services.Inference;
using ONNX_Runner.Services.Text.Phonemization;

// Native CPU fixtures exercise the production Run paths without loading Dawn or a GPU.
const string piperModel = "CAg6nwEKIAoFaW5wdXQSBm91dHB1dCIEQ2FzdCoJCgJ0bxgBoAECEhNjb25jdXJyZW5jeV9maXh0dXJlWhgKBWlucHV0Eg8KDQgHEgkKAggBCgMSAU5aGwoNaW5wdXRfbGVuZ3RocxIKCggIBxIECgIIAVoUCgZzY2FsZXMSCgoICAESBAoCCANiGQoGb3V0cHV0Eg8KDQgBEgkKAggBCgMSAU5CAhAN";
const string colorModel = "CAg67wEKIgoFYXVkaW8SD2NvbnZlcnRlZF9hdWRpbyIISWRlbnRpdHkSE2NvbmN1cnJlbmN5X2ZpeHR1cmVaHAoFYXVkaW8SEwoRCAESDQoCCAEKAggDCgMSAU5aGgoMYXVkaW9fbGVuZ3RoEgoKCAgHEgQKAggBWh4KCHNyY190b25lEhIKEAgBEgwKAggBCgIIAgoCCAFaHwoJZGVzdF90b25lEhIKEAgBEgwKAggBCgIIAgoCCAFaEQoDdGF1EgoKCAgBEgQKAggBYiYKD2NvbnZlcnRlZF9hdWRpbxITChEIARINCgIIAQoCCAMKAxIBTkICEA0=";
const string extractModel = "CAg6dwohCgVpbnB1dBIOdG9uZV9lbWJlZGRpbmciCElkZW50aXR5Eg9leHRyYWN0X2ZpeHR1cmVaGwoFaW5wdXQSEgoQCAESDAoCCAEKAggBCgIIAmIkCg50b25lX2VtYmVkZGluZxISChAIARIMCgIIAQoCCAEKAggCQgIQDQ==";
string temporary = Path.Combine(Path.GetTempPath(), "Tsubaki-WebGpu-Comparison-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
int checks = 0, failures = 0;
try
{
    Type provider = typeof(PiperRunner).Assembly.GetType("ONNX_Runner.Services.Inference.WebGpuExecutionProvider")!;
    MethodInfo enter = provider.GetMethod("EnterInference", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The engine was not built with a WebGPU comparison profile.");
    MethodInfo validate = provider.GetMethod("ValidateConcurrency", BindingFlags.NonPublic | BindingFlags.Static)!;

    bool legacyProfile = OrtEnv.Instance().GetVersionString().StartsWith("1.29.", StringComparison.Ordinal);
    foreach (string version in new[] { "1.29.0", "1.29.1", "1.30.0" })
    {
        if (legacyProfile)
            Check("Legacy comparison accepts core " + version, () => validate.Invoke(null, [version, null]));
        else
            Check("Current comparison keeps its core requirement for " + version, () => Reject(() => validate.Invoke(null, [version, null])));
    }
    foreach (string version in new[] { "1.31.1", "1.32.0-dev" })
        Check("Serialized comparison accepts core " + version, () => validate.Invoke(null, [version, null]));
    foreach (string version in new[] { "1.24.4", "1.28.0", "invalid" })
        Check("Serialized comparison rejects core " + version, () => Reject(() => validate.Invoke(null, [version, null])));
    Check("Forced legacy implementation cannot change the comparison", () => Reject(() => validate.Invoke(null, ["1.31.1", "1"])));

    string piperPath = Save("piper.onnx", piperModel);
    string colorPath = Save("color.onnx", colorModel);
    string extractPath = Save("extract.onnx", extractModel);

    foreach (var (hardware, cloning) in new[]
    {
        (new HardwareSettings { ForcePiperToCpu = true }, true),
        (new HardwareSettings { ForcePiperToCpu = false, PiperGpuDeviceId = 0, OpenVoiceGpuDeviceId = 1 }, true),
        (new HardwareSettings { ForcePiperToCpu = false }, false)
    })
    {
        Check("Incompatible startup routing fails before a CPU fallback", () =>
            Throws<InvalidOperationException>(() =>
            {
                using var rejected = new PiperRunner(piperPath, new PiperConfig(), new FixturePhonemizer(),
                    new OnnxSettings(), hardware, new ModelSettings(), NullLogger<PiperRunner>.Instance, cloning);
            }));
    }

    foreach (int size in new[] { 2, 3 })
    {
        using var piper = (PiperRunner)RuntimeHelpers.GetUninitializedObject(typeof(PiperRunner));
        var piperPool = Pool(piperPath, size);
        var piperGate = new SemaphoreSlim(size, size);
        Set(piper, "_phonemizer", new FixturePhonemizer());
        Set(piper, "_config", new PiperConfig());
        Set(piper, "_isolatedSessionPool", piperPool);
        Set(piper, "_sessionGate", piperGate);
        Set(piper, "_isUsingPool", true);

        using var color = (OpenVoiceRunner)RuntimeHelpers.GetUninitializedObject(typeof(OpenVoiceRunner));
        var colorPool = Pool(colorPath, size);
        var colorGate = new SemaphoreSlim(size, size);
        Set(color, "_config", new ToneConfig { Model = { GinChannels = 2 } });
        Set(color, "_logger", NullLogger<OpenVoiceRunner>.Instance);
        Set(color, "_extractSession", Session(extractPath));
        Set(color, "_colorSessionPool", colorPool);
        Set(color, "_colorGate", colorGate);
        Set(color, "_isUsingColorPool", true);

        Check($"{size}+{size}: Piper waits for the shared inference gate", () => WaitForGate(enter, RunPiper));
        Check($"{size}+{size}: tone extraction waits for the same gate", () => WaitForGate(enter, RunExtract));
        Check($"{size}+{size}: tone conversion waits for the same gate", () => WaitForGate(enter, RunColor));

        Check($"{size}+{size}: Piper native errors release both gates", () =>
        {
            Set(piper, "_speakerId", (long?)1);
            try { Throws<OnnxRuntimeException>(RunPiper); }
            finally { Set(piper, "_speakerId", null); }
            RunOnAnotherThread(RunPiper);
        });
        Check($"{size}+{size}: extractor native errors release the shared gate", () =>
        {
            Throws<OnnxRuntimeException>(() => color.ExtractToneColor(new float[2, 2]));
            RunOnAnotherThread(RunExtract);
        });
        Check($"{size}+{size}: converter native errors release both gates", () =>
        {
            Throws<OnnxRuntimeException>(() => color.ApplyToneColor(new float[8], 4, 2, new float[2], new float[2]));
            RunOnAnotherThread(RunColor);
        });
        Check($"{size}+{size}: resident pools keep their full capacity", () =>
            Require(piperPool.Count == size && colorPool.Count == size && piperGate.CurrentCount == size && colorGate.CurrentCount == size,
                "A session or semaphore permit was lost."));

        if (failures != 0) break;

        Check($"{size}+{size}: concurrent callers safely share serialized inference", () =>
            RunOnAnotherThread(() => Parallel.For(0, 100, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                if (i % 3 == 0) RunExtract();
                else if (i % 3 == 1) RunPiper();
                else RunColor();
            })));
        Check($"{size}+{size}: stress preserves every resident session", () =>
            Require(piperPool.Count == size && colorPool.Count == size && piperGate.CurrentCount == size && colorGate.CurrentCount == size,
                "Pool capacity changed after concurrent callers."));

        void RunPiper()
        {
            var result = piper.SynthesizeAudioRaw("7");
            try { Require(result.Length == 3 && result.Buffer.AsSpan(0, 3).SequenceEqual(new float[] { 7, 8, 9 }), "Piper output changed."); }
            finally { ArrayPool<float>.Shared.Return(result.Buffer); }
        }
        void RunExtract() => Require(color.ExtractToneColor(new float[,] { { 5, 6 } }).AsSpan().SequenceEqual(new float[] { 5, 6 }), "Extractor output changed.");
        void RunColor()
        {
            float[] input = Enumerable.Range(1, 12).Select(static i => (float)i).ToArray();
            var result = color.ApplyToneColor(input, 4, 3, new float[2], new float[2]);
            try { Require(result.Length == 12 && result.Buffer.AsSpan(0, 12).SequenceEqual(input), "Converter output changed."); }
            finally { ArrayPool<float>.Shared.Return(result.Buffer); }
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(new { Checks = checks, Failures = failures,
        Runtime = OrtEnv.Instance().GetVersionString(), PoolSizes = new[] { 2, 3 }, NativeGpuTested = false }));
    return failures == 0 ? 0 : 1;

    string Save(string name, string encoded)
    {
        string path = Path.Combine(temporary, name);
        File.WriteAllBytes(path, Convert.FromBase64String(encoded));
        return path;
    }
}
finally { Directory.Delete(temporary, true); }

void Check(string name, Action action)
{
    checks++;
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
static void WaitForGate(MethodInfo enter, Action action)
{
    Task? worker = null;
    using var started = new ManualResetEventSlim();
    using (var lease = (IDisposable)enter.Invoke(null, null)!)
    {
        worker = Task.Run(() => { started.Set(); action(); });
        Require(started.Wait(TimeSpan.FromSeconds(5)), "The worker did not start.");
        Require(!worker.Wait(TimeSpan.FromMilliseconds(150)), "Inference bypassed the shared gate.");
    }
    Require(worker.Wait(TimeSpan.FromSeconds(10)), "Inference did not resume after releasing the gate.");
}
static void RunOnAnotherThread(Action action) =>
    Require(Task.Run(action).Wait(TimeSpan.FromSeconds(15)), "Inference stayed blocked after an earlier call.");
static InferenceSession Session(string path)
{
    using var options = new SessionOptions { IntraOpNumThreads = 1 };
    return new InferenceSession(path, options);
}
static ConcurrentQueue<InferenceSession> Pool(string path, int size)
{
    var pool = new ConcurrentQueue<InferenceSession>();
    try
    {
        for (int i = 0; i < size; i++) pool.Enqueue(Session(path));
        return pool;
    }
    catch
    {
        while (pool.TryDequeue(out var session)) session.Dispose();
        throw;
    }
}
static void Set(object target, string name, object? value) =>
    target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static void Reject(Action action)
{
    try { action(); }
    catch (TargetInvocationException error) when (error.InnerException is NotSupportedException) { return; }
    throw new InvalidOperationException("An unsupported runtime configuration was accepted.");
}
sealed class FixturePhonemizer : IPhonemizer
{
    public long[] PhonemesToIds(string phonemes, bool isContinuation = false, bool isFinished = true)
    {
        long value = long.Parse(phonemes, CultureInfo.InvariantCulture);
        return [value, value + 1, value + 2];
    }
}
