namespace ONNX_Runner.Models;

/// <summary>
/// Execution profile containing thread and memory settings for a specific hardware target (CPU or GPU).
/// This class is intentionally role-agnostic: it never carries its own "recommended" defaults for
/// IntraOpNumThreads, since the right value differs entirely depending on whether the profile ends up
/// playing the CPU or GPU role. See <see cref="OnnxSettings.Cpu"/> and <see cref="OnnxSettings.Gpu"/>
/// below for the actual defaults and the reasoning behind each.
/// </summary>
public class OnnxProfile
{
    /// <summary>
    /// Execution mode: "Sequential" (nodes executed one by one) or "Parallel" (simultaneous node execution).
    /// Sequential is generally safer and faster for most TTS models unless the graph is explicitly designed for parallel execution.
    /// </summary>
    public string ExecutionMode { get; set; } = "Sequential";

    /// <summary>
    /// The number of threads used to parallelize the execution within a single node.
    /// <c>0</c> is a sentinel meaning "leave this untouched" — <see cref="ApplyTo"/> then never
    /// sets <c>SessionOptions.IntraOpNumThreads</c> at all, letting ONNX Runtime's own internal
    /// default (which already behaves as auto-detection) take over. It is deliberately NOT meant
    /// to be a sensible standalone default: the right number depends entirely on whether this
    /// profile is playing the CPU or GPU role, which is why <see cref="OnnxSettings"/> always
    /// overrides it for both the <see cref="OnnxSettings.Cpu"/> and <see cref="OnnxSettings.Gpu"/> profiles.
    /// </summary>
    public int IntraOpNumThreads { get; set; }

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
        // else: leave SessionOptions.IntraOpNumThreads untouched — see the IntraOpNumThreads
        // property doc above for why 0 means "skip the assignment" rather than "explicitly request auto".

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
///
/// SAFE-BY-DEFAULT: Cpu and Gpu below are pre-populated via their own property initializers.
/// .NET's configuration binder reuses these existing instances when binding appsettings.json
/// rather than constructing fresh ones, so a missing "Cpu"/"Gpu" key — or a missing individual
/// field inside either one — safely falls back to the values declared here instead of resetting
/// to 0/false. The one case that needs an explicit fallback is the whole "OnnxSettings" section
/// being absent from config entirely: Get&lt;T&gt;() returns null (not a default instance) when a
/// section doesn't exist at all, which is why Program.cs guards the read with "?? new OnnxSettings()".
/// </summary>
public class OnnxSettings
{
    /// <summary>
    /// Enables ONNX Runtime graph optimizations (constant folding, node fusion, etc.).
    /// </summary>
    public bool EnableGraphOptimization { get; set; } = true;

    /// <summary>
    /// Settings specifically applied when the engine is running on the CPU.
    /// IntraOpNumThreads defaults to a fixed, moderate value rather than 0/auto — on CPU, "auto"
    /// tends to mean "every physical core", which starves any other concurrent request under load.
    /// </summary>
    public OnnxProfile Cpu { get; set; } = new OnnxProfile 
    { 
        IntraOpNumThreads = 4,
        EnableCpuMemArena = true 
    };

    /// <summary>
    /// Settings specifically applied when the engine is running on the GPU (DirectML/CUDA).
    /// IntraOpNumThreads defaults to 1, since the CPU here is only preparing data for the GPU —
    /// the heavy lifting happens on the accelerator itself, so extra CPU-side threads mostly just
    /// add synchronization overhead.
    /// </summary>
    public OnnxProfile Gpu { get; set; } = new OnnxProfile 
    { 
        IntraOpNumThreads = 1,
        EnableCpuMemArena = false // Often disabled for GPU to avoid memory conflicts
    };
}