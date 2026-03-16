namespace ONNX_Runner.Models;

public class ChunkerSettings
{
    public int MinChunkLength { get; set; } = 350;
    public int MaxChunkLength { get; set; } = 450;
}