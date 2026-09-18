using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace ONNX_Runner.Services;

/// <summary>
/// Centralized startup diagnostics for ONNX Runtime execution devices.
/// This class only reports the device selected by an already-successful
/// session initialization path; it does not participate in provider routing.
/// </summary>
internal static partial class OnnxHardwareDiagnostics
{
    public static void LogCpuDevice(ILogger logger, string component)
    {
        LogCpuLoaded(
            logger,
            component,
            RuntimeInformation.ProcessArchitecture,
            Environment.ProcessorCount);
    }

    public static void LogDirectMlDevice(ILogger logger, string component, int adapterIndex)
    {
        if (TryGetDxgiAdapter(adapterIndex, out var hardwareDevice))
        {
            LogDirectMlLoaded(
                logger,
                component,
                adapterIndex,
                hardwareDevice.Vendor,
                hardwareDevice.DeviceId);
            return;
        }

        // Session creation already succeeded with this DirectML adapter index.
        // Hardware metadata is diagnostic only, so never fail startup if ORT
        // cannot expose the matching DXGI device details.
        LogDirectMlLoadedWithoutMetadata(logger, component, adapterIndex);
    }

    public static void LogCudaDevice(ILogger logger, string component, int deviceId)
    {
        if (TryGetCudaDevice(deviceId, out var epDevice))
        {
            LogCudaLoaded(
                logger,
                component,
                deviceId,
                epDevice.HardwareDevice.Vendor,
                epDevice.HardwareDevice.DeviceId);
            return;
        }

        // CUDA's deviceId is the actual CUDA ordinal passed to the provider.
        // Some ONNX Runtime builds do not expose a matching OrtEpDevice entry,
        // so keep the ordinal instead of guessing physical-device metadata.
        LogCudaLoadedWithoutMetadata(logger, component, deviceId);
    }

    public static void LogWebGpuDevice(
        ILogger logger,
        string component,
        int adapterIndex,
        OrtEpDevice epDevice)
    {
        LogWebGpuLoaded(
            logger,
            component,
            adapterIndex,
            epDevice.HardwareDevice.Vendor,
            epDevice.HardwareDevice.DeviceId);
    }

    private static bool TryGetDxgiAdapter(int adapterIndex, out OrtHardwareDevice hardwareDevice)
    {
        // Use GetEpDevices() instead of GetHardwareDevices(). GetEpDevices() is
        // available in the ONNX Runtime version used by Tsubaki and still exposes
        // the associated OrtHardwareDevice, including DXGI metadata on Windows.
        foreach (var epDevice in OrtEnv.Instance().GetEpDevices())
        {
            OrtHardwareDevice device = epDevice.HardwareDevice;

            if (device.Type != OrtHardwareDeviceType.GPU)
            {
                continue;
            }

            if (!device.Metadata.Entries.TryGetValue("DxgiAdapterNumber", out string? adapterNumberText))
            {
                continue;
            }

            if (!int.TryParse(
                adapterNumberText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int dxgiAdapterIndex))
            {
                continue;
            }

            if (dxgiAdapterIndex != adapterIndex)
            {
                continue;
            }

            hardwareDevice = device;
            return true;
        }

        hardwareDevice = null!;
        return false;
    }

    private static bool TryGetCudaDevice(int deviceId, out OrtEpDevice epDevice)
    {
        OrtEpDevice? onlyCudaDevice = null;
        int cudaDeviceCount = 0;

        foreach (var device in OrtEnv.Instance().GetEpDevices())
        {
            if (!string.Equals(
                device.EpName,
                "CUDAExecutionProvider",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cudaDeviceCount++;
            onlyCudaDevice = device;

            if (!TryGetCudaOrdinal(device, out int cudaOrdinal))
            {
                continue;
            }

            if (cudaOrdinal != deviceId)
            {
                continue;
            }

            epDevice = device;
            return true;
        }

        // If ORT exposes exactly one CUDA device but omits the explicit ordinal,
        // CUDA device 0 is unambiguous.
        if (deviceId == 0 && cudaDeviceCount == 1 && onlyCudaDevice != null)
        {
            epDevice = onlyCudaDevice;
            return true;
        }

        epDevice = null!;
        return false;
    }

    private static bool TryGetCudaOrdinal(OrtEpDevice device, out int deviceId)
    {
        IReadOnlyDictionary<string, string> entries = device.EpOptions.Entries;

        if (!entries.TryGetValue("device_id", out string? deviceIdText) &&
            !entries.TryGetValue("deviceId", out deviceIdText))
        {
            deviceId = default;
            return false;
        }

        return int.TryParse(
            deviceIdText,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out deviceId);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[HARDWARE] {Component} loaded successfully on CPU (Architecture: {Architecture}, Logical Processors: {LogicalProcessors})")]
    private static partial void LogCpuLoaded(
        ILogger logger,
        string component,
        Architecture architecture,
        int logicalProcessors);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[HARDWARE] {Component} loaded successfully on GPU (DirectML, Adapter: {AdapterIndex}, Vendor: {Vendor}, HardwareDeviceId: {HardwareDeviceId})")]
    private static partial void LogDirectMlLoaded(
        ILogger logger,
        string component,
        int adapterIndex,
        string vendor,
        uint hardwareDeviceId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[HARDWARE] {Component} loaded successfully on GPU (DirectML, Adapter: {AdapterIndex})")]
    private static partial void LogDirectMlLoadedWithoutMetadata(
        ILogger logger,
        string component,
        int adapterIndex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[HARDWARE] {Component} loaded successfully on GPU (CUDA, Device ID: {DeviceId}, Vendor: {Vendor}, HardwareDeviceId: {HardwareDeviceId})")]
    private static partial void LogCudaLoaded(
        ILogger logger,
        string component,
        int deviceId,
        string vendor,
        uint hardwareDeviceId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[HARDWARE] {Component} loaded successfully on GPU (CUDA, Device ID: {DeviceId})")]
    private static partial void LogCudaLoadedWithoutMetadata(
        ILogger logger,
        string component,
        int deviceId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[HARDWARE] {Component} loaded successfully on GPU (WebGPU, Adapter: {AdapterIndex}, Vendor: {Vendor}, HardwareDeviceId: {HardwareDeviceId})")]
    private static partial void LogWebGpuLoaded(
        ILogger logger,
        string component,
        int adapterIndex,
        string vendor,
        uint hardwareDeviceId);
}
