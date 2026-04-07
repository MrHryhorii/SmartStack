using Microsoft.ML.OnnxRuntime;

namespace ONNX_Runner.Models;

public class OnnxSettings
{
    public bool EnableGraphOptimization { get; set; } = true;

    // "Sequential" (послідовне виконання вузлів) або "Parallel" (паралельне)
    public string ExecutionMode { get; set; } = "Sequential";

    // Кількість потоків (0 = автоматично)
    public int IntraOpNumThreads { get; set; } = 0;

    // Зручний метод, щоб швидко застосувати ці налаштування до будь-якої сесії
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