using System.Text;

namespace ONNX_Runner.Services.Text;

public partial class TextChunker
{

    /// <summary>
    /// Finds the next semantic sentence boundary. Characters in SentenceTerminators are candidates;
    /// ambiguous period-like marks, ellipses, and technical-token punctuation are validated in context.
    /// </summary>
    private int FindSentenceEnd(
        ReadOnlySpan<char> textSpan,
        int currentIndex,
        out bool isSentenceFinished)
    {
        int searchIndex = currentIndex;

        while (searchIndex < textSpan.Length)
        {
            int offset = textSpan[searchIndex..].IndexOfAny(Rules.SentenceBoundaryCandidates);
            if (offset < 0)
            {
                isSentenceFinished = false;
                return textSpan.Length;
            }

            int candidateIndex = searchIndex + offset;


            if (!IsRealSentenceBoundary(textSpan, candidateIndex))
            {
                searchIndex = candidateIndex + 1;
                continue;
            }

            isSentenceFinished = true;
            return ConsumeBoundarySuffix(textSpan, candidateIndex);
        }

        isSentenceFinished = false;
        return textSpan.Length;
    }

    /// <summary>
    /// Validates one candidate terminator without assuming a particular language.
    /// </summary>
    private bool IsRealSentenceBoundary(ReadOnlySpan<char> text, int index)
    {
        char terminator = text[index];

        // Greek questions are usually written with an ASCII semicolon, not with the
        // visually equivalent U+037E. Ordinary semicolons in other scripts remain clauses.
        if (terminator == ';')
        {
            return IsGreekQuestionSemicolon(text, index) &&
                   !IsQuotedReportingContinuation(text, index) &&
                   HasBoundarySeparationAfterTerminalCluster(text, index);
        }

        // Explicit text/paragraph separators are always semantic boundaries.
        if (IsLineBoundary(terminator))
        {
            return true;
        }

        // Lowercase reporting remains part of the semantic sentence even after ...?!.
        // EarlySplit may expose this boundary without marking the thought as finished.
        if (IsQuotedReportingContinuation(text, index))
        {
            return false;
        }

        if (Rules.PeriodLikeMarks.Contains(terminator))
        {
            return IsRealPeriodBoundary(text, index);
        }

        if (Rules.EllipsisMarks.Contains(terminator))
        {
            return IsRealEllipsisBoundary(text, index, index + 1);
        }

        // Ambiguous Western terminal marks are accepted only after the complete attached
        // terminal/closing cluster reaches a real boundary. This protects code-like constructs
        // such as foo?.Bar(), while still accepting What?! Next and "Stop!" Then continue.
        // Script-specific hard terminators intentionally do not require whitespace because many
        // writing systems do not separate sentences with spaces.
        if (RequiresBoundarySeparation(terminator) && !HasBoundarySeparationAfterTerminalCluster(text, index))
        {
            return false;
        }

        // ASCII ? and ! may legally occur inside URLs / URI queries / technical tokens.
        // This remains as a structural safeguard even though the boundary-separation rule above
        // already rejects the common no-whitespace forms.
        if (terminator is '?' or '!' && IsEmbeddedTechnicalTerminator(text, index))
        {
            return false;
        }

        return true;
    }

    /// <summary>Recognizes a separated one-to-three-digit ordered-list marker in its permitted line context.</summary>
    private bool IsOrderedListMarker(ReadOnlySpan<char> text, int periodIndex)
    {
        int numberStart = periodIndex;
        while (numberStart > 0 && char.IsAsciiDigit(text[numberStart - 1]))
        {
            numberStart--;
        }

        int numberLength = periodIndex - numberStart;
        if (numberLength is < 1 or > 3 ||
            periodIndex + 1 >= text.Length ||
            !char.IsWhiteSpace(text[periodIndex + 1]))
        {
            return false;
        }

        int nextIndex = SkipBoundarySpacing(text, periodIndex + 1, out bool crossedLineBoundary);
        if (crossedLineBoundary || nextIndex >= text.Length || !char.IsLetter(text[nextIndex]))
        {
            return false;
        }

        int previousIndex = numberStart - 1;
        while (previousIndex >= 0 && char.IsWhiteSpace(text[previousIndex]) &&
               !IsLineBoundary(text[previousIndex]))
        {
            previousIndex--;
        }

        // A marker must begin the input, a line, or a new item after an actual
        // sentence boundary or a list-introducing colon.
        // The preceding period in "p. 15. Read" belongs to an abbreviation, not a
        // sentence ending. "Done. 2. Start" still introduces a numbered item.
        if (previousIndex < 0 || IsLineBoundary(text[previousIndex]) || text[previousIndex] == ':')
        {
            return true;
        }

        return Rules.SentenceTerminators.Contains(text[previousIndex]) &&
            IsRealSentenceBoundary(text, previousIndex);
    }

    /// <summary>Treats a semicolon as a Greek question mark only when preceding letter context supports it.</summary>
    private bool IsGreekQuestionSemicolon(ReadOnlySpan<char> text, int index)
    {
        int previous = index - 1;
        while (previous >= 0 && char.IsWhiteSpace(text[previous]) && !IsLineBoundary(text[previous]))
        {
            previous--;
        }

        if (previous < 0 || !char.IsLetter(text[previous]))
        {
            return false;
        }

        char value = text[previous];
        return value is >= '\u0370' and <= '\u03FF' or >= '\u1F00' and <= '\u1FFF';
    }

    /// <summary>
    /// Ellipsis can express hesitation inside a sentence. It is terminal at end-of-input,
    /// across an explicit line boundary, before another terminal suffix, or before a clearly
    /// new uppercase sentence; otherwise it remains a continuation.
    /// </summary>
    private bool IsRealEllipsisBoundary(ReadOnlySpan<char> text, int start, int runEnd)
    {
        // Merge adjacent ellipsis symbols / period-like dots into one semantic run.
        while (runEnd < text.Length &&
               (Rules.EllipsisMarks.Contains(text[runEnd]) || Rules.PeriodLikeMarks.Contains(text[runEnd])))
        {
            runEnd++;
        }

        int afterClosing = SkipClosingPunctuation(text, runEnd);
        int nextVisible = SkipBoundarySpacing(text, afterClosing, out bool crossedLineBoundary);

        if (crossedLineBoundary || nextVisible >= text.Length)
        {
            return true;
        }

        char next = text[nextVisible];

        // "Wait…!" / "Really…?" are terminal clusters. Validate the end of the whole
        // attached cluster so constructs without a real boundary do not split on its first mark.
        if (Rules.SentenceTerminators.Contains(next) &&
            !Rules.EllipsisMarks.Contains(next) &&
            !Rules.PeriodLikeMarks.Contains(next))
        {
            return HasBoundarySeparationAfterTerminalCluster(text, start);
        }

        // Without whitespace, ellipsis almost always connects the same thought, especially
        // in scripts without case distinctions (Japanese, Chinese, Thai, etc.).
        bool hadSpacing = nextVisible > afterClosing;
        if (!hadSpacing)
        {
            return false;
        }

        // With spacing, an uppercase letter is a useful language-independent signal of a
        // fresh sentence in bicameral scripts. Lowercase and uncased scripts stay continuous.
        Rune.DecodeFromUtf16(text[nextVisible..], out Rune nextRune, out _);
        return IsUppercaseOrTitlecase(nextRune);
    }

    /// <summary>
    /// Protects sentence-like punctuation occurring inside a technical token. This intentionally
    /// recognizes structure rather than a language: URI schemes, www/domain paths, query strings,
    /// relative URLs, and email-like tokens.
    /// </summary>
    private bool IsEmbeddedTechnicalTerminator(ReadOnlySpan<char> text, int index)
    {
        if (index <= 0 || index + 1 >= text.Length)
        {
            return false;
        }

        char after = text[index + 1];
        if (char.IsWhiteSpace(after) || Rules.ClosingPunctuation.Contains(after))
        {
            return false;
        }

        int tokenStart = index - 1;
        while (tokenStart >= 0 && !char.IsWhiteSpace(text[tokenStart]))
        {
            tokenStart--;
        }
        tokenStart++;

        return TechnicalTextRecognizer.TryGetSpanLength(text, tokenStart, Rules, out int length) &&
               index + 1 < tokenStart + length;
    }
}
