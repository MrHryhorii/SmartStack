#if USE_WEBGPU
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace ONNX_Runner.Services.Inference;

/// <summary>Registers the process-wide plugin once; inference remains independent per session.</summary>
internal static class WebGpuExecutionProvider
{
    private static readonly Lazy<OrtEpDevice> Device = new(Register);

    public static void AppendTo(SessionOptions options, int adapterIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(adapterIndex);

        // Graph execution stays sequential within each session. Different pooled
        // sessions can run concurrently on the WebGPU 0.5 / ORT 1.31 backend.
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        options.AppendExecutionProvider(OrtEnv.Instance(), [Device.Value],
            new Dictionary<string, string>
            {
                // OrtEpDevice is only a provider handle. Dawn selects the physical
                // adapter; deviceId is a context identifier, not an adapter index.
                ["adapterIndex"] = adapterIndex.ToString(CultureInfo.InvariantCulture)
            });
    }

    internal static void ValidateConcurrency(string runtimeVersion, string? forceLegacy)
    {
        string versionNumber = runtimeVersion.Split('-', '+')[0];
        if (!Version.TryParse(versionNumber, out var version) || version < new Version(1, 31))
            throw new NotSupportedException("Concurrent WebGPU sessions require ONNX Runtime 1.31 or later in this build.");

        // A one-session pool is insufficient in legacy mode: Piper and OpenVoice
        // would still overlap on the shared device. Let the runners fall back to CPU.
        if (forceLegacy == "1")
            throw new NotSupportedException("ORT_WEBGPU_EP_FORCE_LEGACY=1 disables concurrent WebGPU execution. Remove it and restart to use WebGPU pools.");
    }

    private static OrtEpDevice Register()
    {
        var env = OrtEnv.Instance();
        ValidateConcurrency(env.GetVersionString(), Environment.GetEnvironmentVariable("ORT_WEBGPU_EP_FORCE_LEGACY"));

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
