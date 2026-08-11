namespace ONNX_Runner.Models;

/// <summary>
/// Execution profile containing thread and memory settings for a specific hardware target (CPU or GPU).
/// </summary>
public class OnnxProfile
{
    /// <summary>
    /// Execution mode: "Sequential" (nodes executed one by one) or "Parallel" (simultaneous node execution).
    /// Sequential is generally safer and faster for most TTS models unless the graph is explicitly designed for parallel execution.
    /// </summary>
    public string ExecutionMode { get; set; } = "Sequential";

    /// <summary>
    /// The number of threads used to parallelize the execution within nodes. 
    /// 0 means the ONNX runtime will automatically select the optimal thread count based on hardware.
    /// For CPU execution, setting this to 2 or the number of physical cores often yields the best performance.
    /// For GPU execution, it is highly recommended to set this to 1 to reduce synchronization overhead, 
    /// as the CPU is only preparing data for the GPU.
    /// </summary>
    public int IntraOpNumThreads { get; set; } = 0;

    /// <summary>
    /// The number of threads used to parallelize the execution of the graph across nodes.
    /// For TTS (sequential audio generation), it is highly recommended to keep this at 1 to prevent thread contention.
    /// </summary>
    public int InterOpNumThreads { get; set; } = 1;

    /// <summary>
    /// Allows ONNX to pre-allocate memory in blocks. Reduces Garbage Collector overhead.
    /// </summary>
    public bool EnableMemoryPattern { get; set; } = true;

    /// <summary>
    /// Enables the use of the CPU memory arena for ONNX execution.
    /// </summary>
    public bool EnableCpuMemArena { get; set; } = true;

    /// <summary>
    /// A convenient helper method to quickly apply these settings to any ONNX SessionOptions instance.
    /// Includes a fail-safe mechanism to prevent invalid or dangerous thread counts.
    /// </summary>
    public void ApplyTo(Microsoft.ML.OnnxRuntime.SessionOptions options)
    {
        options.ExecutionMode = ExecutionMode.Equals("Parallel", StringComparison.OrdinalIgnoreCase)
            ? Microsoft.ML.OnnxRuntime.ExecutionMode.ORT_PARALLEL
            : Microsoft.ML.OnnxRuntime.ExecutionMode.ORT_SEQUENTIAL;

        // --- FOOL-PROOF SYSTEM ---
        // Get the absolute maximum number of logical processors on the current machine
        int maxHardwareThreads = Environment.ProcessorCount;

        // Clamp the values to ensure they are strictly between 0 and maxHardwareThreads.
        // If a user inputs 100, it becomes maxHardwareThreads. If they input -5, it becomes 0.
        int safeIntraThreads = Math.Clamp(IntraOpNumThreads, 0, maxHardwareThreads);
        int safeInterThreads = Math.Clamp(InterOpNumThreads, 0, maxHardwareThreads);

        if (safeIntraThreads > 0)
        {
            options.IntraOpNumThreads = safeIntraThreads;
        }

        if (safeInterThreads > 0)
        {
            options.InterOpNumThreads = safeInterThreads;
        }

        options.EnableMemoryPattern = EnableMemoryPattern;
        options.EnableCpuMemArena = EnableCpuMemArena;
    }
}

/// <summary>
/// Configuration for the ONNX Runtime execution engine.
/// Allows fine-tuning performance based on specific CPU/GPU architectures.
/// </summary>
public class OnnxSettings
{
    /// <summary>
    /// Enables ONNX Runtime graph optimizations (constant folding, node fusion, etc.).
    /// </summary>
    public bool EnableGraphOptimization { get; set; } = true;

    /// <summary>
    /// Settings specifically applied when the engine is running on the CPU.
    /// </summary>
    public OnnxProfile Cpu { get; set; } = new OnnxProfile 
    { 
        IntraOpNumThreads = 2,
        EnableCpuMemArena = true 
    };

    /// <summary>
    /// Settings specifically applied when the engine is running on the GPU (DirectML/CUDA).
    /// </summary>
    public OnnxProfile Gpu { get; set; } = new OnnxProfile 
    { 
        IntraOpNumThreads = 1,
        EnableCpuMemArena = false // Often disabled for GPU to avoid memory conflicts
    };
}