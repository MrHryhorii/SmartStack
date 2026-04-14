using Microsoft.ML.OnnxRuntime;

namespace ONNX_Runner.Models;

/// <summary>
/// Configuration for the ONNX Runtime execution engine.
/// Allows fine-tuning performance based on specific CPU/GPU architectures.
/// </summary>
public class OnnxSettings
{
    public bool EnableGraphOptimization { get; set; } = true;

    /// <summary>
    /// Execution mode: "Sequential" (nodes executed one by one) or "Parallel" (simultaneous node execution).
    /// Sequential is generally safer and faster for most TTS models unless the graph is explicitly designed for parallel execution.
    /// </summary>
    public string ExecutionMode { get; set; } = "Sequential";

    /// <summary>
    /// The number of threads used to parallelize the execution within nodes. 
    /// 0 means the ONNX runtime will automatically select the optimal thread count based on hardware.
    /// </summary>
    public int IntraOpNumThreads { get; set; } = 0;

    /// <summary>
    /// A convenient helper method to quickly apply these settings to any ONNX SessionOptions instance.
    /// </summary>
    public void ApplyTo(Microsoft.ML.OnnxRuntime.SessionOptions options)
    {
        options.GraphOptimizationLevel = EnableGraphOptimization
            ? GraphOptimizationLevel.ORT_ENABLE_ALL
            : GraphOptimizationLevel.ORT_DISABLE_ALL;

        options.ExecutionMode = ExecutionMode.Equals("Parallel", StringComparison.OrdinalIgnoreCase)
            ? Microsoft.ML.OnnxRuntime.ExecutionMode.ORT_PARALLEL
            : Microsoft.ML.OnnxRuntime.ExecutionMode.ORT_SEQUENTIAL;

        if (IntraOpNumThreads > 0)
        {
            options.IntraOpNumThreads = IntraOpNumThreads;
        }
    }
}