namespace ONNX_Runner.Models;
/// <summary>
/// Controls how a synthesized response is framed on the wire.
/// This is independent from <see cref="SynthesisRequest.Stream"/>, which controls
/// whether output is delivered incrementally while generation is still running.
/// </summary>
public enum SpeechStreamFormat : byte
{
    Audio,
    Sse
}
/// <summary>
/// Allocation-free parser shared by external request adapters.
/// Missing or empty values preserve the existing audio response behavior.
/// </summary>
internal static class SpeechStreamFormatParser
{
    public static bool TryParse(string? value, out SpeechStreamFormat format)
    {
        format = SpeechStreamFormat.Audio;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        ReadOnlySpan<char> candidate = value.AsSpan().Trim();
        if (candidate.Equals("audio".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (candidate.Equals("sse".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            format = SpeechStreamFormat.Sse;
            return true;
        }

        return false;
    }
}
