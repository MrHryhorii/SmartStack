using System.Buffers;
using System.Globalization;
using System.Text;
using ONNX_Runner.Models.Text;

namespace ONNX_Runner.Services.Text.Phonemization;

/// <summary>Stateless Unicode and lexical-boundary checks using the caller's shared text rules.</summary>
internal static class PhonemizerTextBoundaries
{
    // Returns true when the rune is a Unicode letter.
    internal static bool IsLetterRune(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter;
    }

    // Returns true when the rune can participate in the core of a word token.
    internal static bool IsWordCoreRune(Rune rune)
    {
        int value = rune.Value;

        // ASCII dominates many API payloads. Keep the common path branch-only.
        if ((value >= 'A' && value <= 'Z') ||
            (value >= 'a' && value <= 'z') ||
            (value >= '0' && value <= '9'))
        {
            return true;
        }

        if (value <= 0x7F)
        {
            return false;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    // Decodes the first Unicode scalar and returns its UTF-16 width.
    internal static int DecodeRune(ReadOnlySpan<char> value, out Rune rune)
    {
        OperationStatus status = Rune.DecodeFromUtf16(
            value,
            out rune,
            out int consumed);

        if (status == OperationStatus.Done)
        {
            return consumed;
        }

        // Invalid UTF-16 must never stall the tokenizer. Preserve one code unit as garbage
        // and continue; valid text remains entirely on the normal Rune path.
        rune = Rune.ReplacementChar;
        return 1;
    }

    // Uses the shared catalog for hyphens that may participate in a word token.
    internal static bool IsLexicalHyphen(char value, TextChunkerRules rules)
    {
        return rules.LexicalHyphens.Contains(value);
    }

    // Returns true for period-like forms that may terminate a written abbreviation.
    internal static bool IsPeriodLike(char value, TextChunkerRules rules)
    {
        return rules.PeriodLikeMarks.Contains(value);
    }

    // Quotes are analysis boundaries but never output punctuation.
    internal static bool IsVisualQuote(Rune rune, TextChunkerRules rules)
    {
        return rune.Value <= char.MaxValue &&
               rules.VisualQuotes.Contains((char)rune.Value);
    }

    // Checks whether a character is an apostrophe, period, or lexical hyphen allowed inside a word.
    internal static bool IsWordConnector(char value, TextChunkerRules rules)
    {
        return rules.LexicalApostrophes.Contains(value) ||
            rules.PeriodLikeMarks.Contains(value) || IsLexicalHyphen(value, rules);
    }

    // Returns true for punctuation that may form a soft language-detection boundary.
    internal static bool IsBoundaryPunctuation(Rune rune, TextChunkerRules rules)
    {
        // Quotes are handled separately as silent hard analysis boundaries. All remaining
        // punctuation stays audible/structural and becomes a soft language-detection boundary.
        if (IsVisualQuote(rune, rules))
        {
            return false;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    // Preserves a separator when removing a visual quote would join two word cores.
    internal static bool NeedsQuoteSeparator(ReadOnlySpan<char> fullText, int quoteIndex, int quoteLength)
    {
        int nextIndex = quoteIndex + quoteLength;
        if (quoteIndex <= 0 || nextIndex >= fullText.Length) return false;

        OperationStatus leftStatus = Rune.DecodeLastFromUtf16(
            fullText[..quoteIndex], out Rune leftRune, out _);
        int rightLength = DecodeRune(fullText[nextIndex..], out Rune rightRune);
        _ = rightLength;

        return leftStatus == OperationStatus.Done && IsWordCoreRune(leftRune) && IsWordCoreRune(rightRune);
    }
}
