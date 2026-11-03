namespace ONNX_Runner.Models.Requests;

/// <summary>Preserves finite request ranges; non-finite values retain the applicable default.</summary>
internal static class SpeechRequestNumber
{
    internal static float Clamp(float value, float minimum, float maximum)
        => float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : 1;

    internal static float? Clamp(float? value, float minimum, float maximum)
        => value.HasValue && float.IsFinite(value.Value) ? Math.Clamp(value.Value, minimum, maximum) : null;
}
