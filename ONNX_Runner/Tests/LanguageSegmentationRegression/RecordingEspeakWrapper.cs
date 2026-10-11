namespace ONNX_Runner.Services;

// Test-only boundary spy. The real text pipeline calls this in place of the native
// eSpeak bridge; returning the core unchanged exposes punctuation and spacing decisions.
// No pronunciation, native library, or waveform behavior is simulated or asserted.
public sealed class EspeakWrapper
{
    public List<(string Text, string Voice)> Calls { get; } = [];

    /// <summary>Records ordinary text and voice selection, returning the text as a test phoneme payload.</summary>
    public bool TryGetIpaPhonemes(string text, string voice, out string phonemes)
    {
        Calls.Add((text, voice));
        phonemes = text;
        return true;
    }
    public List<(char Character, string Voice)> CharacterCalls { get; } = [];
    public Dictionary<char, string> CharacterResults { get; } = [];
    public List<(string Emoji, string Voice)> EmojiCalls { get; } = [];
    public Dictionary<string, string> EmojiResults { get; } = new(StringComparer.Ordinal);

    /// <summary>Records character-name requests and returns configured or default test phonemes.</summary>
    public bool TryGetCharacterPhonemes(char character, string voice, out string phonemes)
    {
        CharacterCalls.Add((character, voice));
        phonemes = CharacterResults.TryGetValue(character, out string? result) ? result : "s";
        return true;
    }


    /// <summary>Records emoji-name requests and returns configured or default test phonemes.</summary>
    public bool TryGetEmojiPhonemes(string emoji, string voice, out string phonemes)
    {
        EmojiCalls.Add((emoji, voice));
        phonemes = EmojiResults.TryGetValue(emoji, out string? result) ? result : "e";
        return true;
    }
}
