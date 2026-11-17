using System.Globalization;

namespace ONNX_Runner.Services.Text;

public partial class TextChunker
{

    /// <summary>
    /// Finds the first valid one-time EarlySplit boundary inside the first semantic sentence.
    /// Quoted terminals before lowercase reporting may end early without finishing the sentence.
    /// ASCII clause marks (, ; :) require a real boundary after the complete attached punctuation
    /// cluster. Non-ASCII clause marks keep script-native behavior because many writing systems do
    /// not use spaces between clauses.
    /// </summary>
    private int FindEarlySplitEnd(
        ReadOnlySpan<char> text,
        int start,
        int searchEnd,
        int sentenceEnd)
    {
        int searchIndex = start;

        while (searchIndex < searchEnd)
        {
            int offset = text[searchIndex..searchEnd].IndexOfAny(Rules.EarlySplitCandidates);
            if (offset < 0)
            {
                return -1;
            }

            int candidateIndex = searchIndex + offset;

            if (!Rules.ClausePunctuation.Contains(text[candidateIndex]))
            {
                if (!IsLineBoundary(text[candidateIndex]) && IsQuotedReportingContinuation(text, candidateIndex))
                {
                    return ConsumeBoundarySuffix(text, candidateIndex);
                }

                searchIndex = candidateIndex + 1;
                continue;
            }

            // A Greek question written with ';' is a completed semantic sentence,
            // not an EarlySplit clause. Preserve IsSentenceFinished and its pause.
            if (text[candidateIndex] == ';' && IsRealSentenceBoundary(text, candidateIndex))
            {
                searchIndex = candidateIndex + 1;
                continue;
            }

            int clusterEnd = ConsumeAttachedPunctuationCluster(text, candidateIndex, sentenceEnd);

            if (IsValidEarlySplitBoundary(text, candidateIndex, clusterEnd, sentenceEnd))
            {
                return clusterEnd;
            }

            searchIndex = candidateIndex + 1;
        }

        return -1;
    }

    /// <summary>Validates an early clause split using punctuation, spacing, and the remaining sentence context.</summary>
    private bool IsValidEarlySplitBoundary(
        ReadOnlySpan<char> text,
        int candidateIndex,
        int clusterEnd,
        int sentenceEnd)
    {
        char candidate = text[candidateIndex];

        // Non-ASCII clause punctuation is allowed to follow script-native spacing rules.
        // Examples: Chinese/Japanese fullwidth punctuation, Arabic, Myanmar, Khmer, etc.
        if (candidate > 0x7F)
        {
            return true;
        }

        // For ASCII comma/semicolon/colon, absence of a separator after the complete attached
        // punctuation cluster is strong evidence that the mark is inside a token or construct:
        // https://host, foo:bar, x,y, and similar technical text.
        if (clusterEnd >= sentenceEnd || clusterEnd >= text.Length)
        {
            return true;
        }

        char next = text[clusterEnd];
        return char.IsWhiteSpace(next) || IsBoundaryFormatControl(next);
    }

    /// <summary>
    /// Consumes directly attached punctuation as one cluster. This is intentionally broader than
    /// sentence terminators because EarlySplit candidates may sit inside constructs such as ://.
    /// Spacing is crossed only when a matching closing quote belongs to the same suffix.
    /// </summary>
    private int ConsumeAttachedPunctuationCluster(ReadOnlySpan<char> text, int index, int limit)
    {
        int end = index;
        int max = Math.Min(limit, text.Length);
        bool sawQuote = false;

        while (end < max && IsPunctuationClusterChar(text[end]))
        {
            bool visualQuote = Rules.VisualQuotes.Contains(text[end]);
            if (end > index &&
                (Rules.OpeningPunctuation.Contains(text[end]) ||
                 (visualQuote && !IsClosingPunctuationAt(text, end, sawQuote)))) break;

            sawQuote |= visualQuote;
            end += StringInfo.GetNextTextElementLength(text[end..]);
        }

        return ConsumeClosingSuffix(text, end, ref sawQuote, out _);
    }
}
