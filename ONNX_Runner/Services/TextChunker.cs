using System.Buffers;
using System.Globalization;
using System.Text;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Multilingual sentence chunker using one immutable shared punctuation and abbreviation catalog.
/// Boundary heuristics deliberately preserve ambiguous uppercase initials and uncased letters.
/// </summary>
public class TextChunker(ChunkerSettings settings, TextChunkerRules? rules = null)
{
    public TextChunkerRules Rules { get; } = rules ?? TextChunkerRules.Default;

    private readonly int _maxLength = settings.EffectiveMaxChunkLength;
    private const string EmergencyGlue = "-";

    /// <summary>
    /// One text chunk plus the boundary information already known by the chunker.
    /// </summary>
    public readonly record struct TextChunk(string Text, bool IsSentenceFinished);

    /// <summary>
    /// Chunks text while respecting semantic sentence boundaries. When EarlySplit is enabled,
    /// only the first non-empty semantic sentence gets one opportunity to end early at a
    /// conservative clause boundary. All following text uses normal sentence chunking and
    /// emergency MaxChunkLength splitting.
    /// </summary>
    public List<TextChunk> Split(string text, bool earlySplit = false)
    {
        var result = new List<TextChunk>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        ReadOnlySpan<char> textSpan = text.AsSpan();
        int currentIndex = 0;

        // EarlySplit is handled before the normal loop so disabled/consumed requests pay
        // no additional branch or punctuation search for subsequent sentences.
        if (earlySplit)
        {
            while (currentIndex < textSpan.Length)
            {
                int firstEndIndex = FindSentenceEnd(textSpan, currentIndex, out bool firstSentenceFinished);
                ReadOnlySpan<char> firstSpan = TrimBoundaryWhitespace(textSpan[currentIndex..firstEndIndex]);

                // Ignore empty leading boundaries without consuming the one allowed EarlySplit.
                if (firstSpan.IsEmpty)
                {
                    currentIndex = firstEndIndex;
                    continue;
                }

                // MaxChunkLength is the normal synthesis upper bound. EarlySplit candidates are
                // validated contextually so punctuation embedded in technical text (https://,
                // foo:bar, etc.) is not treated as a clause boundary merely because the symbol
                // itself is present.
                int earlySearchEnd = Math.Min(firstEndIndex, currentIndex + _maxLength);
                int earlyEndIndex = FindEarlySplitEnd(textSpan, currentIndex, earlySearchEnd, firstEndIndex);

                if (earlyEndIndex >= 0)
                {
                    ReadOnlySpan<char> earlyChunk = TrimBoundaryWhitespace(textSpan[currentIndex..earlyEndIndex]);
                    if (!earlyChunk.IsEmpty)
                    {
                        result.Add(new TextChunk(MaterializeChunk(text, earlyChunk), false));
                    }

                    currentIndex = earlyEndIndex;
                }
                else
                {
                    AddSentence(text, textSpan[currentIndex..firstEndIndex], firstSentenceFinished, result);
                    currentIndex = firstEndIndex;
                }

                break;
            }
        }

        while (currentIndex < textSpan.Length)
        {
            int endIndex = FindSentenceEnd(textSpan, currentIndex, out bool isSentenceFinished);
            AddSentence(text, textSpan[currentIndex..endIndex], isSentenceFinished, result);
            currentIndex = endIndex;
        }

        return result;
    }

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

        if (Rules.PeriodLikeMarks.Contains(terminator))
        {
            return IsRealPeriodBoundary(text, index);
        }

        if (Rules.EllipsisMarks.Contains(terminator))
        {
            return IsRealEllipsisBoundary(text, index, index + 1);
        }

        // A quoted terminal can still belong to the same grammatical sentence when a
        // lowercase reporting clause follows: "Really?!" she asked.
        if (IsQuotedReportingContinuation(text, index))
        {
            return false;
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

    /// <summary>
    /// Returns true when a period belongs to an in-sentence token rather than a
    /// semantic sentence boundary. Used by language segmentation so references,
    /// titles, initials, and other protected syntax follow the same decisions as
    /// the sentence chunker without turning language candidates into audio chunks.
    /// Ellipsis runs remain punctuation for the language tokenizer.
    /// </summary>
    internal bool IsIntraSentencePeriod(ReadOnlySpan<char> text, int index)
    {
        if ((uint)index >= (uint)text.Length || !Rules.PeriodLikeMarks.Contains(text[index]))
        {
            return false;
        }

        if ((index > 0 && Rules.PeriodLikeMarks.Contains(text[index - 1])) ||
            (index + 1 < text.Length && Rules.PeriodLikeMarks.Contains(text[index + 1])))
        {
            return false;
        }

        return !IsRealPeriodBoundary(text, index);
    }

    /// <summary>
    /// Handles '.', U+2024, U+FE52 and U+FF0E using one shared contextual rule set.
    /// </summary>
    private bool IsRealPeriodBoundary(ReadOnlySpan<char> text, int index)
    {
        // Consecutive period-like marks are an ASCII/compatibility ellipsis rather than an
        // abbreviation separator. Treat the complete run using ellipsis context rules.
        int runEnd = index + 1;
        while (runEnd < text.Length && Rules.PeriodLikeMarks.Contains(text[runEnd]))
        {
            runEnd++;
        }

        if (runEnd - index >= 2)
        {
            return IsRealEllipsisBoundary(text, index, runEnd);
        }

        // An ordered-list marker is spoken as a separate completed audio chunk.
        // "1. Download" becomes "1." + "Download", allowing the existing sentence pause
        // after the number. Context checks in IsOrderedListMarker still protect references
        // such as "Fig. 2", "p. 15", and ordinary sentence-final numbers.
        if (IsOrderedListMarker(text, index))
        {
            return true;
        }

        // A period directly embedded between token characters is never a sentence boundary:
        // S.T.A.L.K.E.R, 3.14, v1.2.3, example.com, user.name@example.com, etc.
        if (index + 1 < text.Length && IsWordLikeAfterPeriod(text[index + 1]))
        {
            return false;
        }

        int afterClosing = SkipClosingPunctuation(text, index + 1);

        // Period-like marks follow the same boundary-separation principle as the ambiguous
        // Western ?/! family. If text continues immediately after the complete period/closing
        // suffix, keep it in the same semantic sentence. A following terminal mark is allowed
        // to extend the terminal cluster and is validated below as one unit.
        if (afterClosing < text.Length &&
            !char.IsWhiteSpace(text[afterClosing]) &&
            !IsBoundaryFormatControl(text[afterClosing]) &&
            !Rules.SentenceTerminators.Contains(text[afterClosing]))
        {
            return false;
        }

        int nextVisible = SkipBoundarySpacing(text, afterClosing, out bool crossedLineBoundary);

        if (crossedLineBoundary)
        {
            return true;
        }

        // End-of-input makes the period a real sentence ending even when the final token is
        // itself an abbreviation. There is no following sentence fragment to protect.
        if (nextVisible >= text.Length)
        {
            return true;
        }

        char next = text[nextVisible];

        // A following terminal cluster (?, !, another hard full stop, etc.) belongs to the
        // same sentence. Validate the boundary only after the complete attached cluster rather
        // than committing on its first character.
        if (Rules.SentenceTerminators.Contains(next) && !Rules.PeriodLikeMarks.Contains(next))
        {
            return HasBoundarySeparationAfterTerminalCluster(text, index);
        }

        ReadOnlySpan<char> token = GetTokenBefore(text, index);
        ReadOnlySpan<char> cleanToken = TrimLeadingTokenPunctuation(token);

        if (cleanToken.IsEmpty)
        {
            return true;
        }

        // The period in a bibliographic citation belongs to "et al.", not to the sentence:
        // "Smith et al. (2020)". Restrict this exception to a parenthesized year so
        // "Smith et al. Next sentence" can still end after the abbreviation.
        bool isEtAlAbbreviation = IsEtAlAbbreviation(text, index, cleanToken);
        if (next == '(' &&
            nextVisible + 1 < text.Length &&
            char.IsDigit(text[nextVisible + 1]) &&
            isEtAlAbbreviation)
        {
            return false;
        }

        // Abbreviations can be followed by clause punctuation before the actual continuation:
        // "e.g., this", "etc.; however", and similar multilingual constructions. Look through
        // that punctuation only for abbreviation/context classification; it is not swallowed here.
        int continuationIndex = nextVisible;
        if (Rules.PauseMarks.Contains(next))
        {
            while (continuationIndex < text.Length && Rules.PauseMarks.Contains(text[continuationIndex]))
            {
                continuationIndex++;
            }

            continuationIndex = SkipClosingPunctuation(text, continuationIndex);
            continuationIndex = SkipBoundarySpacing(text, continuationIndex, out bool pauseCrossedLineBoundary);

            if (pauseCrossedLineBoundary || continuationIndex >= text.Length)
            {
                return true;
            }

            next = text[continuationIndex];
        }

        bool nextIsLower = char.IsLower(next);
        bool nextIsUpper = char.IsUpper(next);
        bool nextIsDigit = char.IsDigit(next);
        bool nextIsLetterOrDigit = char.IsLetterOrDigit(next);

        bool knownAbbreviation = Rules.KnownAbbreviations
            .GetAlternateLookup<ReadOnlySpan<char>>()
            .Contains(cleanToken);

        // Unknown lowercase single-letter tokens usually end a sentence, while uppercase
        // initials and uncased scripts stay protected. Check registered abbreviations first
        // so users can keep one-letter abbreviations intact through TextChunkerRules.json.
        // Georgian Mkhedruli is Unicode lowercase but has no ordinary initial capitalization.
        if (IsSingleLetterInitial(cleanToken) && nextIsLetterOrDigit)
        {
            return IsLowercaseCasedLetter(cleanToken) && !knownAbbreviation;
        }

        if (knownAbbreviation && nextIsLetterOrDigit)
        {
            // Lowercase and numeric continuations strongly favor an in-sentence abbreviation:
            // "etc. before", "approx. 25.4", "no. 12".
            if (nextIsLower || nextIsDigit)
            {
                return false;
            }

            // Prefixes normally bind to a following name even when it starts uppercase:
            // "St. Olav", "Sr. García", "Dr. Smith". Scripts without case also need this
            // path: "ดร. สมชาย". Cased source tokens still require capitalization, so
            // lowercase units such as "ms." cannot masquerade as "Ms.".
            bool nextIsUncasedLetter = char.IsLetter(next) && !nextIsLower && !nextIsUpper;
            if ((nextIsUpper || nextIsUncasedLetter) &&
                IsCapitalizedPrefixAbbreviation(cleanToken))
            {
                return false;
            }

            // In these languages the abbreviation itself is often lowercase, but the following
            // proper name is capitalized: "św. Jan", "вул. Хрещатик", "ул. Пушкина".
            if ((nextIsUpper || nextIsUncasedLetter) &&
                !isEtAlAbbreviation &&
                Rules.NameBindingAbbreviations.GetAlternateLookup<ReadOnlySpan<char>>().Contains(cleanToken))
            {
                return false;
            }

            // A numbered reference is not a sentence boundary: "Fig. 2", "kl. 10".
            // Introductory abbreviations also take proper names: "f.eks. Oslo", "e.g. Python".
            if (nextIsUpper &&
                Rules.IntroductoryAbbreviations.GetAlternateLookup<ReadOnlySpan<char>>().Contains(cleanToken))
            {
                return false;
            }

            // A general abbreviation followed by an uppercase token may legitimately end a
            // sentence: "etc. Next...", "ms. Capt...".
            return true;
        }

        bool dottedAbbreviation = LooksLikeDottedAbbreviation(cleanToken);
        if (dottedAbbreviation && nextIsLetterOrDigit)
        {
            if (nextIsLower || nextIsDigit)
            {
                return false;
            }

            // Uppercase dotted initialisms such as U.S. Army or S.T.A.L.K.E.R. remain intact.
            // Lowercase dotted forms such as p.m. followed by an uppercase token are allowed
            // to terminate the sentence.
            if (nextIsUpper && ContainsUppercaseLetter(cleanToken))
            {
                return false;
            }

            return true;
        }

        // A lowercase continuation usually belongs to the same sentence. Georgian Mkhedruli
        // is an exception: Unicode categorizes its letters as lowercase, but Georgian does not
        // capitalize the first word of a sentence. Earlier checks still protect initials and
        // recognized abbreviations before this general fallback.
        if (nextIsLower && (next < '\u10D0' || next > '\u10FF'))
        {
            return false;
        }

        return true;
    }

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

    // One initial may use a supplementary letter or decomposed accents.
    // Combining marks do not turn that single letter into an ordinary word.
    private bool IsSingleLetterInitial(ReadOnlySpan<char> token)
    {
        return TryGetInitialLetter(token, out _);
    }

    private bool IsLowercaseCasedLetter(ReadOnlySpan<char> token)
    {
        return TryGetInitialLetter(token, out Rune letter) &&
            Rune.GetUnicodeCategory(letter) == UnicodeCategory.LowercaseLetter &&
            letter.Value is not (>= 0x10D0 and <= 0x10FF);
    }

    private static bool TryGetInitialLetter(ReadOnlySpan<char> token, out Rune letter)
    {
        if (Rune.DecodeFromUtf16(token, out letter, out int consumed) != OperationStatus.Done ||
            !Rune.IsLetter(letter)) return false;

        int index = consumed;
        while (index < token.Length)
        {
            if (Rune.DecodeFromUtf16(token[index..], out Rune mark, out consumed) != OperationStatus.Done ||
                Rune.GetUnicodeCategory(mark) is not (UnicodeCategory.NonSpacingMark or
                    UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)) return false;

            index += consumed;
        }

        return true;
    }

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

    private bool IsCapitalizedPrefixAbbreviation(ReadOnlySpan<char> token)
    {
        if (!Rules.PrefixAbbreviations
            .GetAlternateLookup<ReadOnlySpan<char>>()
            .Contains(token))
        {
            return false;
        }

        bool hasCasedLetter = false;

        for (int i = 0; i < token.Length; i++)
        {
            char value = token[i];
            if (!char.IsLetter(value))
            {
                continue;
            }

            if (char.IsUpper(value) || char.GetUnicodeCategory(value) == UnicodeCategory.TitlecaseLetter)
            {
                return true;
            }

            if (char.IsLower(value))
            {
                hasCasedLetter = true;
                break;
            }
        }

        // Scripts without upper/lower case cannot satisfy a capitalization test; the curated
        // prefix list itself is therefore the strongest available signal for them.
        return !hasCasedLetter;
    }

    private bool ContainsUppercaseLetter(ReadOnlySpan<char> token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsUpper(token[i]) || char.GetUnicodeCategory(token[i]) == UnicodeCategory.TitlecaseLetter)
            {
                return true;
            }
        }

        return false;
    }

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

    /// <summary>
    /// Detects constructions such as "Really?!" she asked. The closing quote/bracket is required;
    /// punctuation followed directly by lowercase text remains a normal sentence boundary so that
    /// malformed casing does not silently merge unrelated sentences.
    /// </summary>
    private bool IsQuotedReportingContinuation(ReadOnlySpan<char> text, int boundaryIndex)
    {
        int index = boundaryIndex + 1;
        bool sawClosing = false;

        // A terminal cluster may come before the closing quote: ?!", !!!"), etc.
        while (index < text.Length)
        {
            bool advanced = false;

            while (index < text.Length &&
                   Rules.SentenceTerminators.Contains(text[index]) &&
                   !IsLineBoundary(text[index]))
            {
                index++;
                advanced = true;
            }

            while (index < text.Length && Rules.ClosingPunctuation.Contains(text[index]))
            {
                sawClosing = true;
                index++;
                advanced = true;
            }

            if (!advanced)
            {
                break;
            }
        }

        if (!sawClosing)
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

        return char.IsLower(text[nextVisible]);
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
        return char.IsUpper(next);
    }

    /// <summary>
    /// Finds the first valid one-time EarlySplit boundary inside the first semantic sentence.
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
            int offset = text[searchIndex..searchEnd].IndexOfAny(Rules.ClausePunctuation);
            if (offset < 0)
            {
                return -1;
            }

            int candidateIndex = searchIndex + offset;

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
    /// Whitespace always ends the cluster.
    /// </summary>
    private int ConsumeAttachedPunctuationCluster(ReadOnlySpan<char> text, int index, int limit)
    {
        int end = index;
        int max = Math.Min(limit, text.Length);

        while (end < max && IsPunctuationClusterChar(text[end]))
        {
            end++;
        }

        return end;
    }

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
        // The contextual ASCII Greek question mark is deliberately absent from the global
        // terminator set. Consume it here once a Greek boundary has been confirmed.
        int index = text[boundaryIndex] == ';' ? boundaryIndex + 1 : boundaryIndex;
        crossedLineBoundary = false;

        while (index < text.Length)
        {
            int before = index;

            while (index < text.Length && Rules.SentenceTerminators.Contains(text[index]))
            {
                if (IsLineBoundary(text[index]))
                {
                    crossedLineBoundary = true;
                }

                index++;
            }

            while (index < text.Length && Rules.ClosingPunctuation.Contains(text[index]))
            {
                index++;
            }

            if (index == before)
            {
                break;
            }
        }

        return index;
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

    private bool IsLineBoundary(char value)
    {
        return Rules.LineBoundaries.Contains(value);
    }

    private bool IsWordLikeAfterPeriod(char value)
    {
        return char.IsLetterOrDigit(value) ||
               char.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark
                   or UnicodeCategory.SpacingCombiningMark
                   or UnicodeCategory.EnclosingMark;
    }

    private int SkipClosingPunctuation(ReadOnlySpan<char> text, int index)
    {
        while (index < text.Length && Rules.ClosingPunctuation.Contains(text[index]))
        {
            index++;
        }

        return index;
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

    private bool IsBoundaryFormatControl(char value)
    {
        return Rules.BoundaryFormatControls.Contains(value);
    }

    private ReadOnlySpan<char> GetTokenBefore(ReadOnlySpan<char> text, int endExclusive)
    {
        int start = endExclusive - 1;
        while (start >= 0 && !char.IsWhiteSpace(text[start]))
        {
            start--;
        }

        return text[(start + 1)..endExclusive];
    }

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

    private bool LooksLikeDottedAbbreviation(ReadOnlySpan<char> token)
    {
        int separatorCount = 0;
        int segmentLength = 0;
        int maxSegmentLength = 0;
        int minSegmentLength = int.MaxValue;

        for (int i = 0; i < token.Length; i++)
        {
            if (Rules.PeriodLikeMarks.Contains(token[i]))
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
            if (!char.IsLetter(token[i]))
            {
                return false;
            }

            segmentLength++;
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

    /// <summary>
    /// Consumes a complete terminal suffix such as ?!", ...), or mixed full-width terminal clusters.
    /// Terminators and closing punctuation may alternate; none should leak into the next sentence.
    /// </summary>
    private int ConsumeBoundarySuffix(ReadOnlySpan<char> text, int boundaryIndex)
    {
        return ConsumeTerminalCluster(text, boundaryIndex, out _);
    }

    /// <summary>
    /// Adds one normal sentence or routes an oversized sentence through emergency splitting.
    /// </summary>
    private void AddSentence(
        string originalText,
        ReadOnlySpan<char> sentenceSpan,
        bool isSentenceFinished,
        List<TextChunk> result)
    {
        sentenceSpan = TrimBoundaryWhitespace(sentenceSpan);
        if (sentenceSpan.IsEmpty) return;

        if (sentenceSpan.Length <= _maxLength)
        {
            // A whole-input sentence is known to produce one result. Avoid the default
            // four-element backing array without guessing capacity for multi-chunk input.
            if (sentenceSpan.Length == originalText.Length) result.Capacity = 1;
            result.Add(new TextChunk(MaterializeChunk(originalText, sentenceSpan), isSentenceFinished));
            return;
        }

        SplitLongSentence(originalText, sentenceSpan, isSentenceFinished, result);
    }

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
                    !EndsWithProtectedEmergencyToken(sentenceSpan, currentIndex, splitIndex);
                string finalChunk = needsEmergencyGlue
                    ? string.Concat(chunkSpan, EmergencyGlue)
                    : MaterializeChunk(originalText, chunkSpan);

                result.Add(new TextChunk(finalChunk, splitIndex == sentenceSpan.Length && isSentenceFinished));
            }

            currentIndex = splitIndex;
        }
    }

    // Every supplied span is a slice of originalText. Reuse the input for a whole-text
    // chunk; output slices and emergency glue still require their own immutable strings.
    private static string MaterializeChunk(string originalText, ReadOnlySpan<char> chunk)
    {
        return chunk.Length == originalText.Length ? originalText : chunk.ToString();
    }

    // Whitespace can be the base of a combining sequence. Trim complete whitespace
    // elements without detaching their marks; the common unpadded path needs no scan.
    private static ReadOnlySpan<char> TrimBoundaryWhitespace(ReadOnlySpan<char> text)
    {
        while (!text.IsEmpty && char.IsWhiteSpace(text[0]))
        {
            int length = StringInfo.GetNextTextElementLength(text);
            if (!text[..length].IsWhiteSpace()) break;
            text = text[length..];
        }

        return text.TrimEnd();
    }

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
        while (!token.IsEmpty &&
               (Rules.SentenceTerminators.Contains(token[^1]) ||
                Rules.TechnicalSuffixMarks.Contains(token[^1]) ||
                Rules.ClosingPunctuation.Contains(token[^1]) ||
                IsBoundaryFormatControl(token[^1])))
        {
            token = token[..^1];
        }

        // Technical boundaries use visible syntax, not specific domains or languages.
        // Preserve URL schemes, paths, query strings, emails, and dotted identifiers.
        return token.IndexOfAny(Rules.TechnicalMarkers) >= 0;
    }

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
