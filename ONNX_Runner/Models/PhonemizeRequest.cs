namespace ONNX_Runner.Models;

/// <summary>
/// Data Transfer Object (DTO) for isolated text-to-phoneme conversion requests.
/// </summary>
public class PhonemizeRequest
{
    public required string Input { get; set; }
}