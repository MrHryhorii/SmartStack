using System.Globalization;
using System.Text;

namespace ONNX_Runner.Services.Text;

public partial class TextChunker
{

    /// <summary>
    /// Splits an oversized sentence using the complete pause-mark set. Intermediate chunks
    /// are continuations; only the final chunk inherits the real sentence boundary.
    /// </summary>
    private void SplitLongSentence(
        string originalText,
        ReadOnlySpan<char> sentenceSpan,
        bool isSentenceFinished,
        List<TextChunk> result)
    {
        int currentIndex = 0;
        originalText.AsSpan().Overlaps(sentenceSpan, out int sourceOffset);
        ReadOnlySpan<char> boundaryContext = originalText.AsSpan(0, sourceOffset + sentenceSpan.Length);

        while (currentIndex < sentenceSpan.Length)
        {
            int remainingLength = sentenceSpan.Length - currentIndex;
            if (remainingLength <= _maxLength)
            {
                ReadOnlySpan<char> finalSpan = TrimBoundaryWhitespace(sentenceSpan[currentIndex..]);
                if (!finalSpan.IsEmpty)
                {
                    result.Add(new TextChunk(MaterializeChunk(originalText, finalSpan), isSentenceFinished));
                }
                break;
            }

            int windowEnd = currentIndex + _maxLength;
            int splitIndex = FindLastOccurrence(sentenceSpan, currentIndex, windowEnd, Rules.PauseMarks);

            // A pause mark inside a URL, email, or dotted technical token is not a safe
            // emergency boundary. Look for an earlier pause rather than splitting "https://".
            while (splitIndex >= currentIndex &&
                   IsProtectedEmergencyBoundary(sentenceSpan, currentIndex, splitIndex + 1))
            {
                // Skip the whole technical token in one pass, even if it contains
                // many dots, slashes, or query separators.
                int tokenStart = splitIndex;
                while (tokenStart > currentIndex && !char.IsWhiteSpace(sentenceSpan[tokenStart - 1]))
                {
                    tokenStart--;
                }

                splitIndex = FindLastOccurrence(sentenceSpan, currentIndex, tokenStart, Rules.PauseMarks);
            }

            if (splitIndex >= currentIndex)
            {
                splitIndex++;
            }
            else
            {
                splitIndex = -1;
                for (int i = windowEnd - 1; i >= currentIndex; i--)
                {
                    if (!char.IsWhiteSpace(sentenceSpan[i]))
                    {
                        continue;
                    }

                    splitIndex = i + 1;
                    break;
                }

                if (splitIndex == -1)
                {
                    splitIndex = windowEnd;
                }
            }

            // Pause and whitespace candidates can also be followed by combining marks.
            // Validate every emergency cut, not only the separator-free hard fallback.
            splitIndex = FindSafeTextElementBoundary(sentenceSpan, currentIndex, splitIndex);

            // When no earlier separator exists, keep the technical token whole even if
            // that single token exceeds MaxChunkLength.
            int originalSplit = splitIndex;
            splitIndex = MoveEmergencyBoundaryOutsideTechnicalToken(sentenceSpan, currentIndex, splitIndex);
            // Moving back to a token start can land inside a whitespace/combining element.
            if (splitIndex < originalSplit)
            {
                splitIndex = FindSafeTextElementBoundary(sentenceSpan, currentIndex, splitIndex);
            }
            bool completedTechnicalToken = splitIndex > originalSplit;

            splitIndex = ProtectSinglePhonemeBoundary(originalText.AsSpan(), sourceOffset + currentIndex,
                sourceOffset + splitIndex, sourceOffset + sentenceSpan.Length, out bool phonemeBoundary) - sourceOffset;

            // The semantic sentence already owns its closing suffix. Emergency cuts must
            // retain that ownership and leave the final completion flag on speakable text.
            bool protectedTokenEnd = completedTechnicalToken || phonemeBoundary ||
                EndsWithProtectedEmergencyToken(sentenceSpan, currentIndex, splitIndex);
            splitIndex = ProtectEmergencyPunctuationSuffix(boundaryContext, sourceOffset + currentIndex,
                sourceOffset + splitIndex, sourceOffset + windowEnd, protectedTokenEnd) - sourceOffset;

            ReadOnlySpan<char> chunkSpan = TrimBoundaryWhitespace(sentenceSpan[currentIndex..splitIndex]);

            if (!chunkSpan.IsEmpty)
            {
                char lastChar = chunkSpan[^1];

                // Never append emergency glue after a complete technical token. A cut on
                // the whitespace immediately following a path must be treated like a
                // boundary moved beyond the token, without affecting ordinary hard splits.
                bool needsEmergencyGlue = !char.IsPunctuation(lastChar) &&
                    splitIndex < sentenceSpan.Length &&
                    !completedTechnicalToken &&
                    !phonemeBoundary &&
                    !EndsWithProtectedEmergencyToken(sentenceSpan, currentIndex, splitIndex);
                string finalChunk = needsEmergencyGlue
                    ? string.Concat(chunkSpan, EmergencyGlue)
                    : MaterializeChunk(originalText, chunkSpan);

                result.Add(new TextChunk(finalChunk, splitIndex == sentenceSpan.Length && isSentenceFinished));
            }

            currentIndex = splitIndex;
        }
    }

    /// <summary>Keeps a recognized IPA override atomic, as with an indivisible technical token.</summary>
    private static int ProtectSinglePhonemeBoundary(ReadOnlySpan<char> text, int start, int split, int limit, out bool phonemeBoundary)
    {
        phonemeBoundary = false;
        int search = start;
        int searchLimit = Math.Min(split + 1, limit);
        while (SingleBracketPhonemeRecognizer.TryFindNext(text, search, searchLimit, out int opening, out int end))
        {
            if (split <= end)
            {
                phonemeBoundary = true;
                if (split == end) return split;
                return opening > start && HasContent(text[start..opening]) ? opening : end;
            }
            search = end;
        }
        return split;
    }

    /// <summary>Moves a cut around an attached suffix while respecting the ordinary size cap.</summary>
    private int ProtectEmergencyPunctuationSuffix(ReadOnlySpan<char> text, int start, int split, int windowEnd, bool protectedTokenEnd)
    {
        if (split >= text.Length) return split;
        if (!IsAttachedSuffixMark(text, split))
        {
            bool quote = false;
            if (ConsumeClosingSuffix(text, split, ref quote, out _) == split) return split;
        }

        int suffixStart = split;
        while (suffixStart > start)
        {
            int previous = PreviousTextElementStart(text, start, suffixStart);
            char value = text[previous];
            bool spacing = !IsLineBoundary(value) && (char.IsWhiteSpace(value) || IsBoundaryFormatControl(value));
            if (!spacing && !IsAttachedSuffixMark(text, previous)) break;
            suffixStart = previous;
        }

        int end = suffixStart;
        bool sawQuote = false;
        while (end < text.Length)
        {
            int before = end;
            while (end < text.Length && !IsLineBoundary(text[end]) &&
                (Rules.SentenceTerminators.Contains(text[end]) || Rules.PauseMarks.Contains(text[end])))
            {
                end += StringInfo.GetNextTextElementLength(text[end..]);
            }
            end = ConsumeClosingSuffix(text, end, ref sawQuote, out _);
            if (end == before) break;
        }

        if (end <= split || !HasContent(text[start..suffixStart])) return split;
        if (end <= windowEnd || protectedTokenEnd) return end;

        // Move one content element with the suffix rather than emitting punctuation alone.
        int contentStart = suffixStart;
        while (contentStart > start)
        {
            contentStart = PreviousTextElementStart(text, start, contentStart);
            if (HasContent(text[contentStart..suffixStart])) break;
        }

        if (contentStart > start && HasContent(text[start..contentStart])) return contentStart;

        // An indivisible content element plus its suffix can exceed a tiny configured cap.
        // Keeping this minimal atom intact guarantees progress without a quote-only chunk.
        return end;
    }

    private bool IsAttachedSuffixMark(ReadOnlySpan<char> text, int index) =>
        !IsLineBoundary(text[index]) && (Rules.SentenceTerminators.Contains(text[index]) ||
            Rules.PauseMarks.Contains(text[index]) || IsClosingPunctuationAt(text, index));

    private static bool HasContent(ReadOnlySpan<char> text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (IsContentRune(rune)) return true;
        }
        return false;
    }

    private static bool IsContentRune(Rune rune) => Rune.IsLetterOrDigit(rune) ||
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol;

    private static int PreviousTextElementStart(ReadOnlySpan<char> text, int start, int end)
    {
        int previous = start;
        for (int cursor = start; cursor < end; cursor += StringInfo.GetNextTextElementLength(text[cursor..])) previous = cursor;
        return previous;
    }

    /// <summary>Checks whether an emergency cut would divide a protected technical token.</summary>
    private bool IsProtectedEmergencyBoundary(ReadOnlySpan<char> text, int startIndex, int splitIndex)
    {
        if (splitIndex <= startIndex || splitIndex >= text.Length ||
            char.IsWhiteSpace(text[splitIndex - 1]) || char.IsWhiteSpace(text[splitIndex]))
        {
            return false;
        }

        int tokenStart = splitIndex - 1;
        while (tokenStart > startIndex && !char.IsWhiteSpace(text[tokenStart - 1]))
        {
            tokenStart--;
        }

        int tokenEnd = splitIndex;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]))
        {
            tokenEnd++;
        }

        ReadOnlySpan<char> token = text[tokenStart..tokenEnd];
        // Sentence/clause suffixes are not technical evidence by themselves. Otherwise
        // an ordinary long word ending in '.' or '?' would incorrectly bypass the size cap.
        while (!token.IsEmpty)
        {
            int suffix = token.Length - 1;
            if (char.GetUnicodeCategory(token[suffix]) is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                suffix = PreviousTextElementStart(token, 0, token.Length);
            }

            char mark = token[suffix];
            if (!Rules.SentenceTerminators.Contains(mark) && !Rules.TechnicalSuffixMarks.Contains(mark) &&
                !Rules.ClosingPunctuation.Contains(mark) && !IsBoundaryFormatControl(mark)) break;

            token = token[..suffix];
        }

        // Technical boundaries use visible syntax, not specific domains or languages.
        // Preserve URL schemes, paths, query strings, emails, and dotted identifiers.
        return token.IndexOfAny(Rules.TechnicalMarkers) >= 0;
    }

    /// <summary>Checks whether a proposed whitespace cut follows a complete protected token.</summary>
    private bool EndsWithProtectedEmergencyToken(
        ReadOnlySpan<char> text,
        int currentIndex,
        int splitIndex)
    {
        // A whitespace cut may already be immediately after a complete technical token.
        while (splitIndex > currentIndex && char.IsWhiteSpace(text[splitIndex - 1]))
        {
            splitIndex--;
        }

        return splitIndex - currentIndex >= 2 &&
            IsProtectedEmergencyBoundary(text, currentIndex, splitIndex - 1);
    }

    /// <summary>Moves an emergency cut to a technical token edge, allowing the token to exceed the length cap.</summary>
    private int MoveEmergencyBoundaryOutsideTechnicalToken(
        ReadOnlySpan<char> text,
        int currentIndex,
        int splitIndex)
    {
        if (!IsProtectedEmergencyBoundary(text, currentIndex, splitIndex))
        {
            return splitIndex;
        }

        int tokenStart = splitIndex - 1;
        while (tokenStart > currentIndex && !char.IsWhiteSpace(text[tokenStart - 1]))
        {
            tokenStart--;
        }

        if (tokenStart > currentIndex)
        {
            return tokenStart;
        }

        int tokenEnd = splitIndex;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]))
        {
            tokenEnd++;
        }

        return tokenEnd;
    }

    /// <summary>
    /// Finds the furthest extended-grapheme boundary that does not exceed preferredEnd.
    /// Used only by emergency splitting, so normal sentence/word splitting keeps
    /// the existing SearchValues/Span fast path with no additional per-character overhead.
    /// </summary>
    private int FindSafeTextElementBoundary(ReadOnlySpan<char> text, int startIndex, int preferredEnd)
    {
        int cursor = startIndex;
        int lastBoundary = startIndex;

        while (cursor < preferredEnd)
        {
            int elementLength = StringInfo.GetNextTextElementLength(text[cursor..]);
            if (elementLength <= 0 || cursor + elementLength > preferredEnd)
            {
                break;
            }

            cursor += elementLength;
            lastBoundary = cursor;
        }

        if (lastBoundary > startIndex)
        {
            return lastBoundary;
        }

        // A single grapheme can itself be longer than MaxChunkLength (for example a very large
        // emoji ZWJ/combining sequence). Preserving valid Unicode is more important than forcing
        // an impossible size cap, so allow that one element through intact to guarantee progress.
        int firstElementLength = StringInfo.GetNextTextElementLength(text[startIndex..]);
        return Math.Min(text.Length, startIndex + Math.Max(firstElementLength, 1));
    }

    // Finds the last requested boundary character inside the supplied range.
    private int FindLastOccurrence(ReadOnlySpan<char> text, int startIndex, int endIndex, System.Buffers.SearchValues<char> charsToFind)
    {
        ReadOnlySpan<char> window = text[startIndex..endIndex];
        int relativeIndex = window.LastIndexOfAny(charsToFind);

        return relativeIndex == -1 ? -1 : startIndex + relativeIndex;
    }
}
