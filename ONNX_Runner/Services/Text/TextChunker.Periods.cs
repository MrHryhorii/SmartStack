using System.Globalization;
using System.Text;

namespace ONNX_Runner.Services.Text;

public partial class TextChunker
{

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

        return !IsRealSentenceBoundary(text, index);
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
        if (index + 1 < text.Length && IsWordLikeAfterPeriod(text[(index + 1)..]))
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
        }

        Rune.DecodeFromUtf16(text[continuationIndex..], out Rune nextRune, out _);
        UnicodeCategory nextCategory = Rune.GetUnicodeCategory(nextRune);
        bool nextIsLower = nextCategory == UnicodeCategory.LowercaseLetter;
        bool nextIsUpper = nextCategory is UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter;
        bool nextIsDigit = nextCategory == UnicodeCategory.DecimalDigitNumber;
        bool nextIsLetterOrDigit = nextIsLower || nextIsUpper || nextIsDigit ||
            nextCategory is UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;

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
            bool nextIsUncasedLetter = nextCategory is UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;
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
        if (nextIsLower && nextRune.Value is not (>= 0x10D0 and <= 0x10FF))
        {
            return false;
        }

        return true;
    }

    /// <summary>Checks letters, digits, and combining marks as word continuation after a period.</summary>
    private bool IsWordLikeAfterPeriod(ReadOnlySpan<char> text)
    {
        if (text[0] <= 0x7F) return char.IsAsciiLetterOrDigit(text[0]);
        Rune.DecodeFromUtf16(text, out Rune value, out _);
        return Rune.IsLetterOrDigit(value) || IsCombiningMark(value);
    }
}
