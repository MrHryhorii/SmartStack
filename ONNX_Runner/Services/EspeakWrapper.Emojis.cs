using System.Text;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public partial class EspeakWrapper
{
    /// <summary>
    /// Returns dictionary emoji descriptions as IPA in the requested model voice.
    /// Unsupported emoji stay silent; the character API's Unicode-code fallback is never used.
    /// </summary>
    public bool TryGetEmojiPhonemes(string emoji, string voice, out string phonemes)
    {
        lock (_espeakLock)
        {
            phonemes = string.Empty;
            if (!TrySelectVoiceLocked(voice)) return false;

            // Presentation selectors are visual. Separate skin-tone annotations from the base
            // ZWJ sequence to avoid eSpeak truncating adjacent multiword dictionary descriptions.
            StringBuilder? baseText = null;
            foreach (Rune rune in emoji.EnumerateRunes())
            {
                if (rune.Value is 0xFE0E or 0xFE0F || UnicodeEmojiData.IsSkinToneModifier(rune.Value))
                {
                    baseText ??= new StringBuilder(emoji.Length);
                    break;
                }
            }

            Span<char> buffer = stackalloc char[2];
            if (baseText != null)
                foreach (Rune rune in emoji.EnumerateRunes())
                    if (rune.Value is not (0xFE0E or 0xFE0F) && !UnicodeEmojiData.IsSkinToneModifier(rune.Value))
                        baseText.Append(buffer[..rune.EncodeToUtf16(buffer)]);

            string text = baseText?.ToString() ?? emoji;
            if (text.Length == 0) return true;
            phonemes = RemoveCharacterLanguageLabels(GetIpaPhonemes(text));
            if (text.Contains('\u200D') && IsComponentNameFallback(text, phonemes))
            {
                phonemes = string.Empty;
                return true;
            }
            if (phonemes.Length == 0 || baseText == null) return true;

            var output = new StringBuilder(phonemes);
            foreach (Rune rune in emoji.EnumerateRunes())
            {
                if (!UnicodeEmojiData.IsSkinToneModifier(rune.Value)) continue;
                string annotation = RemoveCharacterLanguageLabels(GetIpaPhonemes(rune.ToString()));
                EspeakPhonemePartJoiner.Append(output, annotation);
            }
            phonemes = output.ToString();
            return true;
        }
    }

    // Older dictionaries can spell an unknown ZWJ sequence as concatenated first words of
    // component names. That is an incomplete description, not support for the whole emoji.
    private bool IsComponentNameFallback(string emoji, string phonemes)
    {
        if (phonemes.Length == 0) return false;
        var components = new StringBuilder(phonemes.Length);
        foreach (Rune rune in emoji.EnumerateRunes())
        {
            if (!UnicodeEmojiData.Contains(rune.Value)) continue;
            string name = RemoveCharacterLanguageLabels(GetIpaPhonemes(rune.ToString()));
            int end = 0;
            while (end < name.Length && !char.IsWhiteSpace(name[end])) end++;
            AppendSpokenContent(components, name.AsSpan(0, end));
        }
        var spoken = new StringBuilder(phonemes.Length);
        AppendSpokenContent(spoken, phonemes);
        return components.Length > 0 && components.Equals(spoken);
    }

    private static void AppendSpokenContent(StringBuilder output, ReadOnlySpan<char> text)
    {
        foreach (char character in text)
            if (!char.IsWhiteSpace(character) && character is not ('\u02C8' or '\u02CC')) output.Append(character);
    }
}
