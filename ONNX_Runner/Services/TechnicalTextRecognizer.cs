using System.Globalization;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Recognizes technical syntax independently of statistical language detection.
/// Classification uses spans and the shared immutable punctuation catalog.
/// </summary>
public static class TechnicalTextRecognizer
{
    public static bool TryGetSpanLength(ReadOnlySpan<char> text, int start,
        TextChunkerRules rules, out int length)
    {
        length = 0;
        if ((uint)start >= (uint)text.Length || IsBoundary(text[start], rules) ||
            char.GetUnicodeCategory(text[start]) is UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation)
            return false;

        // Do not reclassify a suffix after the tokenizer consumed a Markdown wrapper.
        // A protected span starts at a lexical boundary, not in the middle of a token.
        if (start > 0)
        {
            char previous = text[start - 1];
            if (!IsBoundary(previous, rules) && !rules.ClausePunctuation.Contains(previous) &&
                !rules.SentenceTerminators.Contains(previous) && !rules.OpeningPunctuation.Contains(previous) &&
                !rules.ClosingPunctuation.Contains(previous)) return false;
        }

        int end = start;
        while (end < text.Length)
        {
            bool lexicalQuote = end > start && end + 1 < text.Length &&
                rules.LexicalApostrophes.Contains(text[end]) &&
                char.IsLetterOrDigit(text[end - 1]) && char.IsLetterOrDigit(text[end + 1]);
            if (IsBoundary(text[end], rules) && !lexicalQuote) break;
            end++;
        }
        ReadOnlySpan<char> candidate = text[start..end];
        int protectedLength = candidate.Length;

        // Leave sentence/clause punctuation outside the protected token for prosody.
        while (protectedLength > 0 &&
            (rules.TechnicalSuffixMarks.Contains(candidate[protectedLength - 1]) ||
             rules.PeriodLikeMarks.Contains(candidate[protectedLength - 1])))
            protectedLength--;

        // An enclosing prose bracket is not syntax belonging to the token.
        int bracketBalance = 0;
        foreach (char value in candidate[..protectedLength])
        {
            UnicodeCategory category = char.GetUnicodeCategory(value);
            if (category == UnicodeCategory.OpenPunctuation) bracketBalance++;
            if (category == UnicodeCategory.ClosePunctuation) bracketBalance--;
        }
        while (protectedLength > 0 && bracketBalance < 0 &&
            char.GetUnicodeCategory(candidate[protectedLength - 1]) == UnicodeCategory.ClosePunctuation)
        {
            protectedLength--;
            bracketBalance++;
        }

        if (protectedLength == 0 || !IsTechnical(candidate[..protectedLength], rules)) return false;
        length = protectedLength;
        return true;
    }

    public static bool IsBoundary(char value, TextChunkerRules rules) =>
        char.IsWhiteSpace(value) || rules.VisualQuotes.Contains(value) ||
        (rules.ReportingDashes.Contains(value) && !rules.LexicalHyphens.Contains(value));

    public static bool ShouldSpeak(ReadOnlySpan<char> text, TextChunkerRules rules)
    {
        if (IsNumericVersion(text) || IsNumericTime(text) || IsIpv4Endpoint(text)) return false;
        foreach (char value in text)
            if (rules.IsTechnicalSpeechSymbol(value)) return true;
        return false;
    }

    private static bool IsTechnical(ReadOnlySpan<char> token, TextChunkerRules rules)
    {
        if (token.IndexOfAny(rules.TechnicalTokenMarkers) >= 0 ||
            token.StartsWith("www.".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            token.IndexOf("?.".AsSpan(), StringComparison.Ordinal) >= 0 ||
            token.IndexOf("!.".AsSpan(), StringComparison.Ordinal) >= 0 ||
            token.IndexOf("::".AsSpan(), StringComparison.Ordinal) >= 0 ||
            (token.Length > 1 && token[0] == '.' && char.IsLetter(token[1])) ||
            IsDottedIdentifier(token, rules)) return true;

        int opening = token.IndexOfAny(rules.OpeningPunctuation);
        if (opening > 0 && char.IsLetterOrDigit(token[opening - 1]))
        {
            foreach (char value in token[(opening + 1)..])
                if (char.GetUnicodeCategory(value) == UnicodeCategory.ClosePunctuation) return true;
        }

        // Preserve wrapped Markdown emphasis while recognizing pointers and file globs.
        if (token.IndexOf('*') >= 0 &&
            !(token.Length > 1 && token[0] == '*' && token[^1] == '*' && token.IndexOf('.') < 0)) return true;

        bool hasText = false;
        foreach (char value in token)
            if (char.IsLetterOrDigit(value)) { hasText = true; break; }
        foreach (char value in token)
            if (rules.IsTechnicalSymbolMarker(value, hasText)) return true;

        int colon = token.IndexOf(':');
        return colon > 0 && colon + 1 < token.Length &&
            char.IsLetterOrDigit(token[colon - 1]) && char.IsLetterOrDigit(token[colon + 1]);
    }

    private static bool IsDottedIdentifier(ReadOnlySpan<char> token, TextChunkerRules rules)
    {
        if (token.IndexOf('.') < 0 ||
            rules.KnownAbbreviations.GetAlternateLookup<ReadOnlySpan<char>>().Contains(token) ||
            IsDottedAbbreviation(token, rules)) return false;

        bool hasLetter = false;
        int segmentLength = 0;
        foreach (char value in token)
        {
            if (value == '.')
            {
                if (segmentLength == 0) return false;
                segmentLength = 0;
                continue;
            }
            if (char.IsLetter(value))
            {
                hasLetter = true;
                segmentLength++;
                continue;
            }
            if (char.IsDigit(value) || value is '_' or '-' || rules.LexicalApostrophes.Contains(value))
            {
                segmentLength++;
                continue;
            }
            return false;
        }
        return hasLetter && segmentLength > 0;
    }

    private static bool IsDottedAbbreviation(ReadOnlySpan<char> token, TextChunkerRules rules)
    {
        int separators = 0, segmentLength = 0, minLength = int.MaxValue, maxLength = 0;
        foreach (char value in token)
        {
            if (rules.PeriodLikeMarks.Contains(value))
            {
                if (segmentLength == 0) return false;
                separators++;
                minLength = Math.Min(minLength, segmentLength);
                maxLength = Math.Max(maxLength, segmentLength);
                segmentLength = 0;
                continue;
            }
            if (!char.IsLetter(value)) return false;
            segmentLength++;
        }
        if (separators == 0 || segmentLength == 0) return false;
        minLength = Math.Min(minLength, segmentLength);
        maxLength = Math.Max(maxLength, segmentLength);
        return maxLength == 1 || (separators >= 2 && minLength > 0 && maxLength <= 3);
    }

    private static bool IsNumericVersion(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        int index = (text[0] is 'v' or 'V') ? 1 : 0;
        bool sawDot = false, sawDigit = false;
        for (; index < text.Length; index++)
        {
            if (char.IsAsciiDigit(text[index])) { sawDigit = true; continue; }
            if (text[index] != '.' || !sawDigit) return false;
            sawDot = true;
            sawDigit = false;
        }
        return sawDot && sawDigit;
    }

    private static bool IsNumericTime(ReadOnlySpan<char> text)
    {
        bool sawColon = false, sawDigit = false;
        foreach (char value in text)
        {
            if (char.IsAsciiDigit(value)) { sawDigit = true; continue; }
            if (value != ':' || !sawDigit) return false;
            sawColon = true;
            sawDigit = false;
        }
        return sawColon && sawDigit;
    }

    private static bool IsIpv4Endpoint(ReadOnlySpan<char> text)
    {
        int index = 0;
        for (int group = 0; group < 4; group++)
        {
            int digits = 0, value = 0;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                value = value * 10 + text[index++] - '0';
                if (++digits > 3 || value > 255) return false;
            }
            if (digits == 0) return false;
            if (group == 3) break;
            if (index >= text.Length || text[index++] != '.') return false;
        }
        if (index == text.Length) return true;
        if (text[index++] != ':' || index == text.Length) return false;
        int port = 0;
        for (; index < text.Length; index++)
        {
            if (!char.IsAsciiDigit(text[index])) return false;
            port = port * 10 + text[index] - '0';
            if (port > 65535) return false;
        }
        return true;
    }
}
