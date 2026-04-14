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
}