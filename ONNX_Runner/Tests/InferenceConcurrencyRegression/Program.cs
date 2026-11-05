using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Services.Cloning;
using ONNX_Runner.Services.Inference;
using ONNX_Runner.Services.Text.Phonemization;

// Original ONNX fixtures: Cast(input -> output) and Identity(audio -> converted_audio).
// Native CPU sessions exercise production pooled Run/copy/finally paths without a GPU.
const string piperModel = "CAg6nwEKIAoFaW5wdXQSBm91dHB1dCIEQ2FzdCoJCgJ0bxgBoAECEhNjb25jdXJyZW5jeV9maXh0dXJlWhgKBWlucHV0Eg8KDQgHEgkKAggBCgMSAU5aGwoNaW5wdXRfbGVuZ3RocxIKCggIBxIECgIIAVoUCgZzY2FsZXMSCgoICAESBAoCCANiGQoGb3V0cHV0Eg8KDQgBEgkKAggBCgMSAU5CAhAN";
const string colorModel = "CAg67wEKIgoFYXVkaW8SD2NvbnZlcnRlZF9hdWRpbyIISWRlbnRpdHkSE2NvbmN1cnJlbmN5X2ZpeHR1cmVaHAoFYXVkaW8SEwoRCAESDQoCCAEKAggDCgMSAU5aGgoMYXVkaW9fbGVuZ3RoEgoKCAgHEgQKAggBWh4KCHNyY190b25lEhIKEAgBEgwKAggBCgIIAgoCCAFaHwoJZGVzdF90b25lEhIKEAgBEgwKAggBCgIIAgoCCAFaEQoDdGF1EgoKCAgBEgQKAggBYiYKD2NvbnZlcnRlZF9hdWRpbxITChEIARINCgIIAQoCCAMKAxIBTkICEA0=";
string temporary = Path.Combine(Path.GetTempPath(), "Tsubaki-Inference-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
int checks = 0, failures = 0;
try
{
    string piperPath = Path.Combine(temporary, "piper.onnx"), colorPath = Path.Combine(temporary, "color.onnx");
    File.WriteAllBytes(piperPath, Convert.FromBase64String(piperModel));
    File.WriteAllBytes(colorPath, Convert.FromBase64String(colorModel));
    using var piper = new PiperRunner(piperPath, new PiperConfig(), new FixturePhonemizer(),
        new OnnxSettings { Cpu = { IntraOpNumThreads = 1 } }, new HardwareSettings { ForcePiperToCpu = true },
        new ModelSettings(), NullLogger<PiperRunner>.Instance);
    Check("CPU override keeps a shared CPU session", () =>
        Require(!piper.IsUsingGPU && piper.ConcurrencyCapacity == int.MaxValue, "CPU override was ignored."));

    // Inject CPU sessions into the real pooled paths to isolate lease management from the driver.
    Get<InferenceSession>(piper, "_sharedSession").Dispose();
    Set(piper, "_sharedSession", null);
    var piperPool = CreatePool(piperPath);
    var piperGate = new SemaphoreSlim(3, 3);
    Set(piper, "_isolatedSessionPool", piperPool);
    Set(piper, "_sessionGate", piperGate);
    Set(piper, "_isUsingPool", true);

    using var color = (OpenVoiceRunner)RuntimeHelpers.GetUninitializedObject(typeof(OpenVoiceRunner));
    var colorPool = CreatePool(colorPath);
    var colorGate = new SemaphoreSlim(3, 3);
    Set(color, "_config", new ToneConfig { Model = { GinChannels = 2 } });
    Set(color, "_logger", NullLogger<OpenVoiceRunner>.Instance);
    Set(color, "_colorSessionPool", colorPool);
    Set(color, "_colorGate", colorGate);
    Set(color, "_isUsingColorPool", true);

    await CheckAsync("Piper waits for a free session", () => CheckWait(piperGate, () => RunPiper(7)));
    await CheckAsync("OpenVoice waits for a free session", () => CheckWait(colorGate, () => RunColor(7)));
    Check("Piper native errors return the session and permit", () =>
    {
        Set(piper, "_speakerId", (long?)1); // The fixture has no sid input.
        try { Throws<OnnxRuntimeException>(() => RunPiper(1)); }
        finally { Set(piper, "_speakerId", null); }
        Require(piperPool.Count == 3 && piperGate.CurrentCount == 3, "Piper leaked a lease.");
        RunPiper(2);
    });
    Check("OpenVoice native errors return the session and permit", () =>
    {
        // The fixed bin dimension is 3. This shape error must reach native Run.
        Throws<OnnxRuntimeException>(() => color.ApplyToneColor(new float[8], 4, 2, new float[2], new float[2]));
        Require(colorPool.Count == 3 && colorGate.CurrentCount == 3, "OpenVoice leaked a lease.");
        RunColor(2);
    });
    Check("200 concurrent Piper calls preserve request data", () =>
        Parallel.For(1, 201, new ParallelOptions { MaxDegreeOfParallelism = 12 }, RunPiper));
    Check("200 concurrent OpenVoice calls preserve request data", () =>
        Parallel.For(1, 201, new ParallelOptions { MaxDegreeOfParallelism = 12 }, RunColor));
    Check("Both pools retain their full capacity", () =>
        Require(piperPool.Count == 3 && colorPool.Count == 3 && piperGate.CurrentCount == 3 && colorGate.CurrentCount == 3,
            "Pool capacity changed after stress."));

    Type? provider = typeof(PiperRunner).Assembly.GetType("ONNX_Runner.Services.Inference.WebGpuExecutionProvider");
    Check("Explicit Piper CPU keeps the independent OpenVoice adapter without initializing WebGPU", () =>
    {
        var hardware = new HardwareSettings { ForcePiperToCpu = true, PiperGpuDeviceId = 0, OpenVoiceGpuDeviceId = 2 };
        string original = JsonSerializer.Serialize(hardware);
        var logger = new WarningLogger();
        object? device = provider?.GetField("Device", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (device != null)
            Require(!(bool)device.GetType().GetProperty("IsValueCreated")!.GetValue(device)!, "WebGPU was initialized before the CPU test.");
        using var forced = new PiperRunner(piperPath, new PiperConfig(), new FixturePhonemizer(),
            new OnnxSettings { Cpu = { IntraOpNumThreads = 1 } }, hardware, new ModelSettings(), logger, openVoiceEnabled: true);
        Require(!forced.IsUsingGPU && forced.ConcurrencyCapacity == int.MaxValue, "The CPU override was ignored.");
        Require(JsonSerializer.Serialize(hardware) == original, "Piper changed the OpenVoice GPU selection.");
        if (provider != null)
        {
            Require(device != null && !(bool)device.GetType().GetProperty("IsValueCreated")!.GetValue(device)!, "CPU Piper initialized the shared WebGPU adapter.");
            Require(logger.Warnings.Count == 1 && logger.Warnings[0].Contains("ForcePiperToCpu", StringComparison.Ordinal), "The explicit override lost warning priority.");
        }
        var result = forced.SynthesizeAudioRaw("17");
        try { Require(result.Length == 3 && result.Buffer.AsSpan(0, 3).SequenceEqual(new float[] { 17, 18, 19 }), "Forced CPU inference failed."); }
        finally { ArrayPool<float>.Shared.Return(result.Buffer); }
    });
    MethodInfo routing = typeof(PiperRunner).GetMethod("ShouldRunOnCpu", BindingFlags.NonPublic | BindingFlags.Static)!;
    foreach (var (piperId, voiceId, cloning, forceCpu) in new[]
    {
        (0, 0, true, false), (2, 2, true, false),
        (0, 1, true, false), (1, 0, true, false),
        (0, 1, false, false), (1, 0, false, false),
        (-1, 0, true, false), (0, -1, true, false), (-1, 2, true, false),
        (0, 0, true, true), (0, 1, true, true), (0, 1, false, true)
    })
    {
        Check($"Startup routing: Piper={piperId}, OpenVoice={voiceId}, cloning={cloning}, forced CPU={forceCpu}", () =>
        {
            using ConfigurationRoot configuration = (ConfigurationRoot)new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
                .AddCommandLine(["--HardwareSettings:ForcePiperToCpu", forceCpu.ToString(),
                    "--HardwareSettings:PiperGpuDeviceId", piperId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--HardwareSettings:OpenVoiceGpuDeviceId", voiceId.ToString(System.Globalization.CultureInfo.InvariantCulture)])
                .Build();
            var hardware = configuration.GetSection(nameof(HardwareSettings)).Get<HardwareSettings>()!;
            string original = JsonSerializer.Serialize(hardware);
            var logger = new WarningLogger();
            bool expected = forceCpu || provider is not null && cloning && Math.Max(0, piperId) != Math.Max(0, voiceId);
            bool actual = (bool)routing.Invoke(null, [hardware, cloning, logger])!;
            Require(actual == expected, "Startup routing selected the wrong backend.");
            Require(logger.Warnings.Count == (expected ? 1 : 0), "Expected exactly one warning for a CPU override.");
            Require(JsonSerializer.Serialize(hardware) == original, "Startup routing mutated shared hardware settings.");
            if (forceCpu) Require(logger.Warnings[0].Contains("ForcePiperToCpu", StringComparison.Ordinal), "Manual CPU override lost priority.");
        });
    }
    if (provider is not null)
    {
        Check("Mismatched WebGPU adapters initialize Piper directly on CPU", () =>
        {
            var hardware = new HardwareSettings { ForcePiperToCpu = false, PiperGpuDeviceId = 0, OpenVoiceGpuDeviceId = 1 };
            var logger = new WarningLogger();
            using var guarded = new PiperRunner(piperPath, new PiperConfig(), new FixturePhonemizer(),
                new OnnxSettings { Cpu = { IntraOpNumThreads = 1 } }, hardware, new ModelSettings(), logger,
                openVoiceEnabled: true);
            Require(!guarded.IsUsingGPU && guarded.ConcurrencyCapacity == int.MaxValue && !Get<bool>(guarded, "_isUsingPool"),
                "Piper did not use the normal shared CPU path.");
            Require(logger.Warnings.Count == 1 && logger.Warnings[0].Contains("GPU indexes differ", StringComparison.Ordinal),
                "GPU initialization was attempted or the mismatch warning is missing.");
            Require(!hardware.ForcePiperToCpu && hardware.PiperGpuDeviceId == 0 && hardware.OpenVoiceGpuDeviceId == 1,
                "The OpenVoice GPU selection or CPU setting was changed.");
            var result = guarded.SynthesizeAudioRaw("13");
            try { Require(result.Length == 3 && result.Buffer.AsSpan(0, 3).SequenceEqual(new float[] { 13, 14, 15 }), "Guarded CPU inference failed."); }
            finally { ArrayPool<float>.Shared.Return(result.Buffer); }
        });
        MethodInfo validate = provider.GetMethod("ValidateConcurrency", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (string version in new[] { "1.31.0", "1.31.1", "1.32.0-dev", "2.0.0" })
            Check($"WebGPU accepts core {version}", () => validate.Invoke(null, [version, null]));
        foreach (string version in new[] { "1.24.4", "1.29.0", "1.29.1", "1.30.0", "invalid" })
            Check($"WebGPU rejects core {version}", () => Reject(() => validate.Invoke(null, [version, null])));
        Check("Legacy override cannot overlap the two runners", () => Reject(() => validate.Invoke(null, ["1.31.1", "1"])));
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Checks = checks, Failures = failures,
        ConcurrentRequestsPerRunner = 200, PoolSize = 3, GpuPerformanceTested = false }));
    return failures == 0 ? 0 : 1;

    void RunPiper(int value)
    {
        var result = piper.SynthesizeAudioRaw(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try { Require(result.Length == 3 && result.Buffer.AsSpan(0, 3).SequenceEqual(new float[] { value, value + 1, value + 2 }), "Piper data crossed requests."); }
        finally { ArrayPool<float>.Shared.Return(result.Buffer); }
    }
    void RunColor(int value)
    {
        float[] input = Enumerable.Range(value, 12).Select(static x => (float)x).ToArray();
        var result = color.ApplyToneColor(input, 4, 3, new float[2], new float[2]);
        try { Require(result.Length == input.Length && result.Buffer.AsSpan(0, result.Length).SequenceEqual(input), "OpenVoice data crossed requests."); }
        finally { ArrayPool<float>.Shared.Return(result.Buffer); }
    }
}
finally { Directory.Delete(temporary, true); }

void Check(string name, Action action)
{
    checks++;
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
}
async Task CheckAsync(string name, Func<Task> action)
{
    checks++;
    try { await action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
}
static async Task CheckWait(SemaphoreSlim gate, Action run)
{
    for (int i = 0; i < 3; i++) gate.Wait();
    using var started = new ManualResetEventSlim();
    Task task = Task.Run(() => { started.Set(); run(); });
    started.Wait();
    bool waited = await Task.WhenAny(task, Task.Delay(100)) != task;
    gate.Release(3);
    await task.WaitAsync(TimeSpan.FromSeconds(10));
    Require(waited, "Call did not wait for an available session.");
}
static ConcurrentQueue<InferenceSession> CreatePool(string path)
{
    using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
    var pool = new ConcurrentQueue<InferenceSession>();
    try
    {
        for (int i = 0; i < 3; i++) pool.Enqueue(new InferenceSession(path, options));
        return pool;
    }
    catch
    {
        while (pool.TryDequeue(out var session)) session.Dispose();
        throw;
    }
}
static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
static void Require(bool valid, string message)
{
    if (!valid) throw new InvalidOperationException(message);
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
    catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException) { return; }
    throw new InvalidOperationException("Unsafe WebGPU execution was accepted.");
}
sealed class FixturePhonemizer : IPhonemizer
{
    public long[] PhonemesToIds(string phonemes, bool isContinuation = false, bool isFinished = true)
    {
        long value = long.Parse(phonemes, System.Globalization.CultureInfo.InvariantCulture);
        return [value, value + 1, value + 2];
    }
}

sealed class WarningLogger : ILogger<PiperRunner>
{
    public List<string> Warnings { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
    }
}
