namespace ONNX_Runner.Models;

/// <summary>
/// Configuration for locating the primary Piper TTS model.
/// </summary>
public class ModelSettings
{
    /// <summary>
    /// The relative or absolute path to the directory containing the .onnx and .json model files.
    /// </summary>
    public string ModelDirectory { get; set; } = "Model";

    /// <summary>
    /// Optional: The exact file path to the .onnx model file. 
    /// If provided, this will be used instead of searching the ModelDirectory.
    /// </summary>
    public string ExactModelFilePath { get; set; } = string.Empty;
    /// <summary>
    /// Optional: The exact file path to the .json config file. 
    /// If provided, this will be used instead of searching the ModelDirectory.
    /// </summary>
    public string ExactConfigFilePath { get; set; } = string.Empty;
}