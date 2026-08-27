namespace ONNX_Runner.Models;

/// <summary>
/// Configuration for hardware resource management.
/// Determines the maximum number of parallel audio generation tasks 
/// to prevent Out-Of-Memory (OOM) crashes and CPU bottlenecking.
/// </summary>
public class HardwareSettings
{
    /// <summary>
    /// Limits parallel requests for the GPU. 
    /// IMPORTANT FOR DirectML: This exact number dictates how many isolated InferenceSession 
    /// objects are simultaneously kept alive in the Object Pool within the video memory (VRAM) 
    /// to serve concurrent requests. Higher numbers require significantly more VRAM.
    /// </summary>
    public int MaxConcurrentGpuRequests { get; set; } = 3;

    /// <summary>
    /// Limits parallel requests for the CPU (0 = auto-calculate based on physical cores).
    /// CPU execution uses a single shared session, so this limits thread contention, not memory.
    /// </summary>
    public int MaxConcurrentCpuRequests { get; set; } = 2;

    /// <summary>
    /// Specifies the preferred GPU device ID (0, 1, 2, etc.) for the Piper model.
    /// The engine will start hardware initialization from this ID.
    /// </summary>
    public int PiperGpuDeviceId { get; set; } = 0;

    /// <summary>
    /// Specifies the preferred GPU device ID (0, 1, 2, etc.) for the OpenVoice models.
    /// The engine will start hardware initialization from this ID.
    /// </summary>
    public int OpenVoiceGpuDeviceId { get; set; } = 0;
}