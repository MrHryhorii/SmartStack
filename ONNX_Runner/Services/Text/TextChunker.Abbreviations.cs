using System.Buffers;
using System.Globalization;
using System.Text;

namespace ONNX_Runner.Services.Text;

public partial class TextChunker
{

    // One initial may use a supplementary letter or decomposed accents.
    // Combining marks do not turn that single letter into an ordinary word.
    private bool IsSingleLetterInitial(ReadOnlySpan<char> token)
    {
        return TryGetInitialLetter(token, out _);
    }

    /// <summary>Checks the single-letter lowercase boundary rule, excluding Georgian letters without the relevant case distinction.</summary>
    private bool IsLowercaseCasedLetter(ReadOnlySpan<char> token)
    {
        return TryGetInitialLetter(token, out Rune letter) &&
            Rune.GetUnicodeCategory(letter) == UnicodeCategory.LowercaseLetter &&
            letter.Value is not (>= 0x10D0 and <= 0x10FF);
    }

    /// <summary>Accepts exactly one Unicode letter followed only by combining marks.</summary>
    private static bool TryGetInitialLetter(ReadOnlySpan<char> token, out Rune letter)
    {
        if (Rune.DecodeFromUtf16(token, out letter, out int consumed) != OperationStatus.Done ||
            !Rune.IsLetter(letter)) return false;

        int index = consumed;
        while (index < token.Length)
        {
            if (Rune.DecodeFromUtf16(token[index..], out Rune mark, out consumed) != OperationStatus.Done ||
                !IsCombiningMark(mark)) return false;

            index += consumed;
        }

        return true;
    }

    /// <summary>Checks a registered name prefix for an initial capital and otherwise lowercase cased letters.</summary>
    private bool IsCapitalizedPrefixAbbreviation(ReadOnlySpan<char> token)
    {
        if (!Rules.PrefixAbbreviations
            .GetAlternateLookup<ReadOnlySpan<char>>()
            .Contains(token))
        {
            return false;
        }

        foreach (Rune value in token.EnumerateRunes())
        {
            if (!Rune.IsLetter(value))
            {
                continue;
            }

            if (IsUppercaseOrTitlecase(value))
            {
                return true;
            }

            if (Rune.IsLower(value))
            {
                return false;
            }
        }

        // Scripts without upper/lower case cannot satisfy a capitalization test; the curated
        // prefix list itself is therefore the strongest available signal for them.
        return true;
    }

    /// <summary>Checks whether an abbreviation contains uppercase or titlecase letters.</summary>
    private bool ContainsUppercaseLetter(ReadOnlySpan<char> token)
    {
        foreach (Rune value in token.EnumerateRunes())
        {
            if (IsUppercaseOrTitlecase(value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Recognizes the al part of a preceding et al abbreviation.</summary>
    private bool IsEtAlAbbreviation(
        ReadOnlySpan<char> text,
        int periodIndex,
        ReadOnlySpan<char> token)
    {
        if (!token.Equals("al".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int previousWordEnd = periodIndex - token.Length;
        while (previousWordEnd > 0 && char.IsWhiteSpace(text[previousWordEnd - 1]))
        {
            previousWordEnd--;
        }

        return previousWordEnd >= 2 &&
               text[(previousWordEnd - 2)..previousWordEnd].Equals("et".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
               (previousWordEnd == 2 || !char.IsLetter(text[previousWordEnd - 3]));
    }

    /// <summary>Returns the whitespace-delimited token ending at the supplied exclusive position without allocating.</summary>
    private ReadOnlySpan<char> GetTokenBefore(ReadOnlySpan<char> text, int endExclusive)
    {
        int start = endExclusive - 1;
        while (start >= 0 && !char.IsWhiteSpace(text[start]))
        {
            start--;
        }

        return text[(start + 1)..endExclusive];
    }

    /// <summary>Removes leading token punctuation while preserving period-like abbreviation markers.</summary>
    private ReadOnlySpan<char> TrimLeadingTokenPunctuation(ReadOnlySpan<char> token)
    {
        int start = 0;
        while (start < token.Length &&
               char.IsPunctuation(token[start]) &&
               !Rules.PeriodLikeMarks.Contains(token[start]))
        {
            start++;
        }

        return token[start..];
    }

    /// <summary>Recognizes dotted initials and short multi-part letter abbreviations conservatively.</summary>
    private bool LooksLikeDottedAbbreviation(ReadOnlySpan<char> token)
    {
        int separatorCount = 0;
        int segmentLength = 0;
        int maxSegmentLength = 0;
        int minSegmentLength = int.MaxValue;

        foreach (Rune value in token.EnumerateRunes())
        {
            if (value.IsBmp && Rules.PeriodLikeMarks.Contains((char)value.Value))
            {
                if (segmentLength == 0)
                {
                    return false;
                }

                separatorCount++;
                maxSegmentLength = Math.Max(maxSegmentLength, segmentLength);
                minSegmentLength = Math.Min(minSegmentLength, segmentLength);
                segmentLength = 0;
                continue;
            }

            // Dotted acronyms/abbreviations are alphabetic. Numeric/mixed forms such as
            // 127.0.0.1 and v1.2.3 are technical tokens, not abbreviation evidence.
            if (Rune.IsLetter(value))
            {
                segmentLength++;
                continue;
            }

            if (segmentLength > 0 && IsCombiningMark(value)) continue;
            return false;
        }

        if (separatorCount == 0 || segmentLength == 0)
        {
            return false;
        }

        maxSegmentLength = Math.Max(maxSegmentLength, segmentLength);
        minSegmentLength = Math.Min(minSegmentLength, segmentLength);

        // Strong generic forms: U.S / i.e / p.m (single-letter segments), or longer chains
        // such as S.T.A.L.K.E.R where multiple short alphabetic segments are unmistakably
        // acronym-like. Avoid treating short domains such as x.ai or co.uk as abbreviations.
        if (maxSegmentLength == 1)
        {
            return true;
        }

        return separatorCount >= 2 &&
               minSegmentLength > 0 &&
               maxSegmentLength <= 3;
    }

    private static bool IsUppercaseOrTitlecase(Rune value) =>
        Rune.GetUnicodeCategory(value) is UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter;

    private static bool IsCombiningMark(Rune value) =>
        Rune.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
}
