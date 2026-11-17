using System.Buffers;
using System.Globalization;
using System.Text;
using ONNX_Runner.Models.Text;

namespace ONNX_Runner.Services.Text;

public partial class TextChunker
{

    /// <summary>
    /// Detects constructions such as "Really?!" she asked. The closing quote/bracket is required;
    /// punctuation followed directly by lowercase text remains a normal sentence boundary so that
    /// malformed casing does not silently merge unrelated sentences.
    /// </summary>
    private bool IsQuotedReportingContinuation(ReadOnlySpan<char> text, int boundaryIndex)
    {
        int index = ConsumeTerminalCluster(text, boundaryIndex, out bool crossedTerminalLine, out bool sawClosing);
        if (!sawClosing || crossedTerminalLine)
        {
            return false;
        }

        int nextVisible = SkipBoundarySpacing(text, index, out bool crossedLineBoundary);
        if (crossedLineBoundary || nextVisible >= text.Length)
        {
            return false;
        }

        // Reporting clauses may be introduced by a dialogue dash after a quoted question:
        // «Τι συμβαίνει;» — ρώτησε κάποιος. The dash itself is not a new sentence.
        if (Rules.ReportingDashes.Contains(text[nextVisible]))
        {
            nextVisible = SkipBoundarySpacing(text, nextVisible + 1, out crossedLineBoundary);
            if (crossedLineBoundary || nextVisible >= text.Length)
            {
                return false;
            }
        }

        // Georgian Mkhedruli is categorized as lowercase but does not use sentence-initial
        // capitalization. Supplementary cased letters still provide a valid Unicode signal.
        return Rune.DecodeFromUtf16(text[nextVisible..], out Rune next, out _) == OperationStatus.Done &&
            Rune.IsLower(next) && next.Value is not (>= 0x10D0 and <= 0x10FF);
    }

    /// <summary>Checks Unicode punctuation categories that may join a terminal punctuation cluster.</summary>
    private bool IsPunctuationClusterChar(char value)
    {
        UnicodeCategory category = char.GetUnicodeCategory(value);
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    /// <summary>
    /// Western question/exclamation marks are ambiguous when immediately glued to following text.
    /// Script-specific hard terminators remain spacing-independent.
    /// </summary>
    private bool RequiresBoundarySeparation(char value)
    {
        return Rules.SeparatedTerminals.Contains(value);
    }

    /// <summary>Checks whether a terminal cluster is separated from following text or crosses a line boundary.</summary>
    private bool HasBoundarySeparationAfterTerminalCluster(ReadOnlySpan<char> text, int boundaryIndex)
    {
        int clusterEnd = ConsumeTerminalCluster(text, boundaryIndex, out bool crossedLineBoundary);

        if (crossedLineBoundary || clusterEnd >= text.Length)
        {
            return true;
        }

        char next = text[clusterEnd];
        return char.IsWhiteSpace(next) || IsBoundaryFormatControl(next);
    }

    /// <summary>
    /// Consumes a complete sentence-terminal/closing cluster, e.g. ?!, ...?!" or !").
    /// </summary>
    private int ConsumeTerminalCluster(
        ReadOnlySpan<char> text,
        int boundaryIndex,
        out bool crossedLineBoundary)
    {
        return ConsumeTerminalCluster(text, boundaryIndex, out crossedLineBoundary, out _);
    }

    private int ConsumeTerminalCluster(
        ReadOnlySpan<char> text,
        int boundaryIndex,
        out bool crossedLineBoundary,
        out bool sawClosing)
    {
        // The contextual ASCII Greek question mark is deliberately absent from the global
        // terminator set. Consume it here once a Greek boundary has been confirmed.
        int index = text[boundaryIndex] == ';' ? boundaryIndex + 1 : boundaryIndex;
        crossedLineBoundary = false;
        sawClosing = false;
        bool sawQuote = false;

        while (index < text.Length)
        {
            int before = index;

            while (index < text.Length && Rules.SentenceTerminators.Contains(text[index]))
            {
                if (IsLineBoundary(text[index]))
                {
                    crossedLineBoundary = true;
                }

                index += StringInfo.GetNextTextElementLength(text[index..]);
            }

            index = ConsumeClosingSuffix(text, index, ref sawQuote, out bool closed);
            sawClosing |= closed;

            if (index == before)
            {
                break;
            }
        }

        return index;
    }

    /// <summary>Keeps closing quotes attached without taking the opening quote of following text.</summary>
    private int ConsumeClosingSuffix(ReadOnlySpan<char> text, int index, ref bool sawQuote, out bool sawClosing)
    {
        sawClosing = false;
        while (index < text.Length)
        {
            int next = index;
            while (next < text.Length && !IsLineBoundary(text[next]) &&
                (char.IsWhiteSpace(text[next]) || IsBoundaryFormatControl(text[next])))
            {
                // Closing bidi scopes stay with preceding content. A scope alone does
                // not count as a closing quote for reporting-clause detection.
                if (text[next] is '\u202C' or '\u2069') index = next + 1;
                next++;
            }

            bool separated = next > index;
            if (next >= text.Length || (separated && !Rules.VisualQuotes.Contains(text[next])) ||
                !IsClosingPunctuationAt(text, next, separated || sawQuote)) break;

            sawClosing = true;
            sawQuote |= Rules.VisualQuotes.Contains(text[next]);
            index = next + StringInfo.GetNextTextElementLength(text[next..]);
        }

        return index;
    }

    private bool IsClosingPunctuationAt(ReadOnlySpan<char> text, int index, bool requireOpening = false)
    {
        char value = text[index];
        if (!Rules.ClosingPunctuation.Contains(value)) return false;
        if (!Rules.VisualQuotes.Contains(value)) return true;

        UnicodeCategory category = char.GetUnicodeCategory(value);
        int after = index + StringInfo.GetNextTextElementLength(text[index..]);
        bool beforeContent = after < text.Length &&
            Rune.DecodeFromUtf16(text[after..], out Rune next, out _) == OperationStatus.Done && IsContentRune(next);
        if (requireOpening || beforeContent || category is UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.OpenPunctuation)
        {
            return HasOpeningQuote(text, index);
        }

        return true;
    }

    /// <summary>Resolves ambiguous, reversed, and separated quotes using punctuation pairs only.</summary>
    private bool HasOpeningQuote(ReadOnlySpan<char> text, int closingIndex)
    {
        ReadOnlySpan<char> family = TextChunkerDefaults.GetQuoteFamily(text[closingIndex]);
        if (family.IsEmpty) return false;

        int search = 0;
        bool open = false;
        char opening = default;
        while (search < closingIndex)
        {
            int offset = text[search..closingIndex].IndexOfAny(family);
            if (offset < 0) break;

            int index = search + offset;
            search = index + 1;
            if (Rules.LexicalApostrophes.Contains(text[index]) && index > 0 && char.IsLetterOrDigit(text[index - 1]) &&
                (!open || (index + 1 < text.Length && char.IsLetterOrDigit(text[index + 1])))) continue;

            int backslash = index - 1;
            while (backslash >= 0 && text[backslash] == '\\') backslash--;
            if ((index - backslash - 1) % 2 != 0) continue;

            if (!open) opening = text[index];
            open = !open;
        }

        return open && TextChunkerDefaults.CanCloseQuote(opening, text[closingIndex]);
    }

    /// <summary>Checks the shared catalog for a hard line boundary.</summary>
    private bool IsLineBoundary(char value)
    {
        return Rules.LineBoundaries.Contains(value);
    }

    /// <summary>Advances past catalogued closing punctuation without consuming following content.</summary>
    private int SkipClosingPunctuation(ReadOnlySpan<char> text, int index)
    {
        bool sawQuote = false;
        return ConsumeClosingSuffix(text, index, ref sawQuote, out _);
    }

    /// <summary>
    /// Skips ordinary spacing/format controls while preserving whether an explicit line boundary
    /// was crossed. Line separators themselves are semantic sentence evidence, not mere whitespace.
    /// </summary>
    private int SkipBoundarySpacing(ReadOnlySpan<char> text, int index, out bool crossedLineBoundary)
    {
        crossedLineBoundary = false;

        while (index < text.Length)
        {
            char value = text[index];

            if (IsLineBoundary(value))
            {
                crossedLineBoundary = true;
                index++;
                continue;
            }

            if (char.IsWhiteSpace(value) || IsBoundaryFormatControl(value))
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    /// <summary>Checks invisible format controls allowed to separate sentence boundaries.</summary>
    private bool IsBoundaryFormatControl(char value)
    {
        return Rules.BoundaryFormatControls.Contains(value);
    }

    /// <summary>
    /// Consumes a complete terminal suffix such as ?!", ...), or mixed full-width terminal clusters.
    /// Terminators and closing punctuation may alternate; none should leak into the next sentence.
    /// </summary>
    private int ConsumeBoundarySuffix(ReadOnlySpan<char> text, int boundaryIndex)
    {
        return ConsumeTerminalCluster(text, boundaryIndex, out _);
    }
}
