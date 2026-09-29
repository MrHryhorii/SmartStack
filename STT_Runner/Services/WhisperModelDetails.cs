using System.Buffers.Binary;

namespace STT_Runner.Services;

/// <summary>
/// Describes the GGML architecture, not the provenance or exact weights of a checkpoint.
/// </summary>
public sealed record WhisperModelDetails(string Family, bool Multilingual, int AudioLayers)
{
    public static WhisperModelDetails? TryRead(string path)
    {
        // The legacy whisper.cpp GGML header starts with magic and eleven 32-bit parameters.
        Span<byte> header = stackalloc byte[48];
        try
        {
            using var file = File.OpenRead(path);
            file.ReadExactly(header);
        }
        catch (IOException)
        {
            return null;
        }

        int magic = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (magic != 0x67676d6c) return null;

        int vocabulary = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int audioLayers = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        int melBins = BinaryPrimitives.ReadInt32LittleEndian(header[40..]);
        if (vocabulary is < 50_000 or > 60_000 || melBins is not (80 or 128)) return null;

        string? family = audioLayers switch
        {
            4 => "tiny",
            6 => "base",
            12 => "small",
            24 => "medium",
            32 => "large",
            _ => null
        };
        if (family is null) return null;

        return new WhisperModelDetails(family, vocabulary >= 51_865, audioLayers);
    }
}
