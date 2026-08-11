namespace ONNX_Runner.Models;

/// <summary>
/// Configuration for hardware resource management.
/// Determines how many parallel audio generation tasks can run concurrently 
/// based on available GPU VRAM or CPU cores to prevent Out-Of-Memory (OOM) crashes.
/// </summary>
public class HardwareSettings
{
    public double TotalVramGb { get; set; } = 8.0;
    public double VramPerRequestGb { get; set; } = 0.6;

    // CPU (0 = use all cores)
    public int MaxConcurrentCpuRequests { get; set; } = 0;
}