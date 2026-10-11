namespace ONNX_Runner.Models;

/// <summary>
/// Defines the supported audio output formats for the TTS engine.
/// 
/// Used in AudioStreamManager.
/// </summary>
public enum AudioFormat
{
    Wav,
    Mp3,
    Opus,
    Aac,
    Flac,
    Pcm,
    B64Json
}

/// <summary>Parses named wire formats without accepting numeric or composite enum values.</summary>
internal static class AudioFormatParser
{
    public static bool TryParse(string? value, out AudioFormat format)
    {
        ReadOnlySpan<char> candidate = value is null ? "mp3" : value.AsSpan().Trim();
        if (candidate.Equals("b64_json".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            format = AudioFormat.B64Json;
            return true;
        }

        return Enum.TryParse(candidate, true, out format) && Enum.IsDefined(format) &&
            candidate.Equals(format.ToString().AsSpan(), StringComparison.OrdinalIgnoreCase);
    }
}