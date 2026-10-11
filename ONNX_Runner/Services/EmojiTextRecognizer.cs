using System.Buffers;
using System.Globalization;
using System.Text;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

internal static class EmojiTextRecognizer
{
    // A complete text element preserves flags, joiners, modifiers, and presentation selectors.
    internal static bool TryGetSpanLength(ReadOnlySpan<char> text, TextChunkerRules rules, out int length)
    {
        length = 0;
        if (text.IsEmpty) return false;
        char first = text[0];
        bool keycap = first is '#' or '*' or >= '0' and <= '9';
        if (first < '\u00A9' && !keycap) return false;
        if (keycap && (text.Length < 2 || text[1] is not ('\u20E3' or '\uFE0F'))) return false;
        if (Rune.DecodeFromUtf16(text, out Rune rune, out _) != OperationStatus.Done ||
            !UnicodeEmojiData.Contains(rune.Value)) return false;

        int elementLength = StringInfo.GetNextTextElementLength(text);
        ReadOnlySpan<char> element = text[..elementLength];
        if (keycap && !element.Contains('\u20E3')) return false;
        if (element.Contains('\uFE0E')) return false;

        // Plain punctuation, mathematical operators, and typographic marks retain their text role.
        // Their explicit emoji presentation is handled as an emoji instead.
        bool textSymbol = rune.Value is 0x00A9 or 0x00AE or 0x2122 || Rune.IsPunctuation(rune) ||
            Rune.GetUnicodeCategory(rune) == UnicodeCategory.MathSymbol || rules.SemanticKinds.ContainsKey(rune);
        if (textSymbol && !keycap && !element.Contains('\uFE0F')) return false;

        length = elementLength;
        return true;
    }

    /// <summary>Finds the next complete emoji span without splitting UTF-16 surrogate pairs; returns -1 when absent.</summary>
    internal static int FindNext(ReadOnlySpan<char> text, TextChunkerRules rules, out int length)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (TryGetSpanLength(text[index..], rules, out length)) return index;
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) index++;
        }
        length = 0;
        return -1;
    }
}
