#if USE_WEBGPU
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace ONNX_Runner.Services.Inference;

/// <summary>Registers the plugin and optionally serializes inference for provider memory comparisons.</summary>
internal static class WebGpuExecutionProvider
{
    private static readonly Lazy<OrtEpDevice> Device = new(Register);

#if WEBGPU_MEMORY_COMPARISON
    private static readonly object InferenceGate = new();

    // The caller holds this lease until native outputs have been copied and disposed.
    // One gate covers Piper, tone extraction, and every color-converter session.
    internal static InferenceLease EnterInference() => new(InferenceGate);

    internal readonly struct InferenceLease : IDisposable
    {
        private readonly object _gate;

        internal InferenceLease(object gate)
        {
            Monitor.Enter(gate);
            _gate = gate;
        }

        public void Dispose() => Monitor.Exit(_gate);
    }
#endif

    public static void AppendTo(SessionOptions options, int adapterIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(adapterIndex);

#if WEBGPU_MEMORY_COMPARISON_LEGACY
        // WebGPU 0.3 does not expose adapterIndex. The Linux comparison selects
        // NVIDIA through VK_LOADER_DRIVERS_SELECT in both profile launch commands.
        if (adapterIndex != 0)
            throw new NotSupportedException("The legacy comparison requires GPU index 0 and OS-level selection of the target adapter.");
#endif

        // Graph execution stays sequential within each session. Different pooled
        // sessions can run concurrently on the WebGPU 0.5 / ORT 1.31 backend.
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        var providerOptions = new Dictionary<string, string>
        {
#if !WEBGPU_MEMORY_COMPARISON_LEGACY
            // OrtEpDevice is only a provider handle. Dawn selects the physical
            // adapter; deviceId is a context identifier, not an adapter index.
            ["adapterIndex"] = adapterIndex.ToString(CultureInfo.InvariantCulture)
#endif
        };
#if WEBGPU_MEMORY_COMPARISON
        // Both plugin versions read these canonical session-config entries.
        // Each dispatch is submitted immediately; storage buffers keep the same cache policy.
        options.AddSessionConfigEntry("ep.webgpuexecutionprovider.maxNumPendingDispatches", "1");
        options.AddSessionConfigEntry("ep.webgpuexecutionprovider.storageBufferCacheMode", "bucket");
#else
        // Submit each dispatch so the default buffer cache can reuse released buffers sooner.
        providerOptions["maxNumPendingDispatches"] = "1";
#endif
        options.AppendExecutionProvider(OrtEnv.Instance(), [Device.Value], providerOptions);
    }

    internal static void ValidateConcurrency(string runtimeVersion, string? forceLegacy)
    {
        string versionNumber = runtimeVersion.Split('-', '+')[0];
#if WEBGPU_MEMORY_COMPARISON_LEGACY
        if (!Version.TryParse(versionNumber, out var version) || version < new Version(1, 29))
            throw new NotSupportedException("Serialized WebGPU comparison requires ONNX Runtime 1.29 or later.");
#else
        if (!Version.TryParse(versionNumber, out var version) || version < new Version(1, 31))
            throw new NotSupportedException("Concurrent WebGPU sessions require ONNX Runtime 1.31 or later in this build.");
#endif

        // Keep the native implementation selected by its package version.
        // The environment override would change that implementation independently.
        if (forceLegacy == "1")
            throw new NotSupportedException("ORT_WEBGPU_EP_FORCE_LEGACY=1 disables concurrent WebGPU execution. Remove it and restart to use WebGPU pools.");
    }

    private static OrtEpDevice Register()
    {
        var env = OrtEnv.Instance();
        ValidateConcurrency(env.GetVersionString(), Environment.GetEnvironmentVariable("ORT_WEBGPU_EP_FORCE_LEGACY"));

#if WEBGPU_MEMORY_COMPARISON
#if WEBGPU_MEMORY_COMPARISON_LEGACY
        const string providerVersion = "0.3.0";
#else
        const string providerVersion = "0.5.0";
#endif
        Console.WriteLine($"[DIAGNOSTIC] WebGPU memory comparison | ORT={env.GetVersionString()} | EP={providerVersion} | GPU Run calls=serialized | maxNumPendingDispatches=1 | storageBufferCacheMode=bucket | model CPU fallback=disabled");
#endif

        string providerName = WebGpuEp.GetEpName();
        OrtEpDevice? device = FindDevice(env, providerName);
        if (device is not null) return device;

        env.RegisterExecutionProviderLibrary("webgpu_ep", WebGpuEp.GetLibraryPath());
        return FindDevice(env, providerName)
            ?? throw new InvalidOperationException("No hardware GPU is available to the WebGPU execution provider.");
    }

    private static OrtEpDevice? FindDevice(OrtEnv env, string providerName)
    {
        foreach (var device in env.GetEpDevices())
        {
            if (device.EpName == providerName && device.HardwareDevice.Type == OrtHardwareDeviceType.GPU)
                return device;
        }
        return null;
    }
}
#endif
