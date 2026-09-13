using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Normalizes orthographic punctuation against the exact punctuation inventory exposed by
/// the currently loaded Piper model. All model-dependent decisions are resolved once at
/// startup; synthesis only performs native-support checks and O(1) fallback lookups.
/// </summary>
public sealed class DynamicPunctuationMapper
{
    private enum PunctuationKind : byte
    {
        Period,
        Question,
        Exclamation,
        Interrobang,
        DoubleQuestion,
        QuestionExclamation,
        ExclamationQuestion,
        Comma,
        Semicolon,
        Colon,
        Ellipsis,
        Dash,
        OpenBracket,
        CloseBracket,
        WordSeparator,
        OpeningQuestion,
        OpeningExclamation
    }

    private enum CollapseKind : byte
    {
        None,
        WeakPause,
        Ellipsis
    }

    // Semantic families. The list intentionally contains punctuation from multiple writing
    // systems, while Unicode-category fallback below guarantees safe handling for punctuation
    // not explicitly listed here.
    private const string PeriodMarks =
        ".。．｡۔։।॥།༎༏༐༑༒។៕။።፨᙮᠃᠉꓿꘎෴׃܀܁܂";

    private const string QuestionMarks =
        "?？؟;՞፧꘏⸮";

    private const string ExclamationMarks =
        "!！՜߹";

    private const string InterrobangMarks =
        "‽";

    private const string DoubleQuestionMarks =
        "⁇";

    private const string QuestionExclamationMarks =
        "⁈";

    private const string ExclamationQuestionMarks =
        "⁉";

    private const string CommaMarks =
        ",，、､،՝፣၊᠂᠈߸꓾꘍";

    private const string SemicolonMarks =
        ";；؛፤";

    private const string ColonMarks =
        ":：፥፦៖᠄܃܄܅܆܇܈܉";

    private const string EllipsisMarks =
        "…‥⋯᠅";

    // Hyphen-minus and lexical hyphens are deliberately excluded. When they occur inside a
    // word they are passed to eSpeak; when standalone they degrade as punctuation instead.
    private const string DashMarks =
        "—–―‒⸺⸻〜～";

    private const string OpenBracketMarks =
        "([{（［｛〈《【〔〖〘〚⟨⟦⟪〈";

    private const string CloseBracketMarks =
        ")]}）］｝〉》】〕〗〙〛⟩⟧⟫〉";

    // Punctuation that semantically separates words rather than clauses.
    private const string WordSeparatorMarks =
        "·・･፡";

    // These marks introduce a question/exclamation. If a model does not explicitly know them,
    // silently dropping them is safer than inserting a closing intonation marker at the start.
    private const string OpeningQuestionMarks =
        "¿𞥟";

    private const string OpeningExclamationMarks =
        "¡𞥞";

    private const string VisualQuoteMarks =
        "\"“”„‟«»‹›「」『』〝〞〟❝❞❛❜‘’‚‛'";

    private static readonly FrozenDictionary<Rune, PunctuationKind> SemanticKinds =
        BuildSemanticKinds();

    private static readonly FrozenSet<Rune> VisualQuotes =
        BuildRuneSet(VisualQuoteMarks);

    // Every single-scalar token explicitly exposed by the loaded Piper model.
    private readonly HashSet<Rune> _supportedSymbols = [];

    // Pre-resolved replacement for every known unsupported punctuation mark.
    // Values may contain multiple characters (e.g. the ellipsis fallback "  .").
    private readonly FrozenDictionary<Rune, string> _fallbacks;

    // Used for punctuation that Unicode classifies as punctuation but whose exact semantic
    // family is unknown to this version of the catalog.
    private readonly string _genericPunctuationFallback;

    // Also used when ASCII "..." must be adapted because the model lacks ordinary '.'.
    private readonly string _ellipsisFallback;

    // Weak pause selected for this exact model. Any punctuation that degrades to this same
    // representation can be semantically collapsed to avoid duplicate comma-like boundaries.
    private readonly string _commaFallback;

    private readonly bool _supportsSpace;

    public DynamicPunctuationMapper(PiperConfig config)
    {
        if (config?.PhonemeIdMap != null)
        {
            foreach (string key in config.PhonemeIdMap.Keys)
            {
                if (TryGetSingleRune(key, out Rune rune))
                {
                    _supportedSymbols.Add(rune);
                }
            }
        }

        _supportsSpace = _supportedSymbols.Contains(new Rune(' '));
        string unknownNativePause = SelectUnknownSupportedPause();

        if (!_supportsSpace && !HasAnyUsablePunctuation())
        {
            Console.WriteLine(
                "[WARNING] Piper model exposes no usable punctuation or space token. " +
                "Punctuation pauses cannot be represented and unsupported punctuation will be stripped.");
        }

        // Resolve one model-native representative per semantic family. Each chain degrades
        // from the closest meaning toward a weaker generic pause and finally to whitespace.
        string period = FirstNonEmpty(
            SelectSupported(PeriodMarks),
            SelectSupported(SemicolonMarks),
            SelectSupported(CommaMarks),
            SelectSupported(ColonMarks),
            unknownNativePause,
            SpaceFallback());

        string nativeQuestion = SelectSupported(QuestionMarks);
        string nativeExclamation = SelectSupported(ExclamationMarks);
        string nativeInterrobang = SelectSupported(InterrobangMarks);
        string nativeDoubleQuestion = SelectSupported(DoubleQuestionMarks);
        string nativeQuestionExclamation = SelectSupported(QuestionExclamationMarks);
        string nativeExclamationQuestion = SelectSupported(ExclamationQuestionMarks);

        string question = FirstNonEmpty(
            nativeQuestion,
            nativeDoubleQuestion,
            nativeQuestionExclamation,
            nativeExclamationQuestion,
            nativeInterrobang,
            period);

        string exclamation = FirstNonEmpty(
            nativeExclamation,
            nativeExclamationQuestion,
            nativeQuestionExclamation,
            nativeInterrobang,
            period);

        // Preserve the semantic order of compound punctuation whenever the model exposes
        // separate question and exclamation tokens. Only degrade to a single mark when the
        // required components are unavailable.
        string doubleQuestion = FirstNonEmpty(
            Combine(nativeQuestion, nativeQuestion),
            nativeDoubleQuestion,
            question);

        string questionExclamation = FirstNonEmpty(
            Combine(nativeQuestion, nativeExclamation),
            nativeQuestionExclamation,
            nativeInterrobang,
            nativeExclamationQuestion,
            question,
            exclamation);

        string exclamationQuestion = FirstNonEmpty(
            Combine(nativeExclamation, nativeQuestion),
            nativeExclamationQuestion,
            nativeInterrobang,
            nativeQuestionExclamation,
            exclamation,
            question);

        string interrobang = FirstNonEmpty(
            nativeInterrobang,
            Combine(nativeQuestion, nativeExclamation),
            nativeQuestionExclamation,
            nativeExclamationQuestion,
            question,
            exclamation);

        _commaFallback = FirstNonEmpty(
            SelectSupported(CommaMarks),
            SelectSupported(SemicolonMarks),
            period);

        string semicolon = FirstNonEmpty(
            SelectSupported(SemicolonMarks),
            SelectSupported(ColonMarks),
            _commaFallback,
            period);

        string colon = FirstNonEmpty(
            SelectSupported(ColonMarks),
            SelectSupported(SemicolonMarks),
            _commaFallback,
            period);

        string dash = FirstNonEmpty(
            SelectSupported(DashMarks),
            _commaFallback,
            period);

        string openBracket = FirstNonEmpty(
            SelectSupported(OpenBracketMarks),
            _commaFallback,
            SpaceFallback());

        string closeBracket = FirstNonEmpty(
            SelectSupported(CloseBracketMarks),
            _commaFallback,
            SpaceFallback());

        string wordSeparator = FirstNonEmpty(
            SpaceFallback(),
            SelectSupported(WordSeparatorMarks),
            _commaFallback);

        _genericPunctuationFallback = FirstNonEmpty(
            _commaFallback,
            semicolon,
            period,
            colon,
            unknownNativePause,
            SpaceFallback());

        string nativeEllipsis = SelectSupported(EllipsisMarks);
        _ellipsisFallback = !string.IsNullOrEmpty(nativeEllipsis)
            ? nativeEllipsis
            : _supportedSymbols.Contains(new Rune('.'))
                ? "..."
                : BuildExtendedPause(period);

        var fallbacks = new Dictionary<Rune, string>(SemanticKinds.Count);

        foreach (var entry in SemanticKinds)
        {
            Rune source = entry.Key;

            // Native support always wins at runtime, so no fallback entry is needed.
            if (_supportedSymbols.Contains(source))
            {
                continue;
            }

            string replacement = entry.Value switch
            {
                PunctuationKind.Period => period,
                PunctuationKind.Question => question,
                PunctuationKind.Exclamation => exclamation,
                PunctuationKind.Interrobang => interrobang,
                PunctuationKind.DoubleQuestion => doubleQuestion,
                PunctuationKind.QuestionExclamation => questionExclamation,
                PunctuationKind.ExclamationQuestion => exclamationQuestion,
                PunctuationKind.Comma => _commaFallback,
                PunctuationKind.Semicolon => semicolon,
                PunctuationKind.Colon => colon,
                PunctuationKind.Ellipsis => _ellipsisFallback,
                PunctuationKind.Dash => dash,
                PunctuationKind.OpenBracket => openBracket,
                PunctuationKind.CloseBracket => closeBracket,
                PunctuationKind.WordSeparator => wordSeparator,
                PunctuationKind.OpeningQuestion => string.Empty,
                PunctuationKind.OpeningExclamation => string.Empty,
                _ => _genericPunctuationFallback
            };

            fallbacks[source] = replacement;
        }

        _fallbacks = fallbacks.ToFrozenDictionary();
    }

    /// <summary>
    /// Normalizes punctuation while preserving ordinary orthographic text for eSpeak.
    /// Returns the original string instance when no normalization is required.
    /// </summary>
    public string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        ReadOnlySpan<char> text = input.AsSpan();

        // Most text needs no rewriting. This avoids StringBuilder allocation on the common path.
        if (!NeedsNormalization(text))
        {
            return input;
        }

        var builder = new StringBuilder(input.Length + 4);
        int index = 0;
        CollapseKind lastCollapseKind = CollapseKind.None;

        while (index < text.Length)
        {
            int consumed = DecodeRune(text[index..], out Rune rune);

            // If ordinary periods are unsupported, treat a run of three or more ASCII periods as
            // one semantic ellipsis rather than expanding it into several unrelated fallbacks.
            if (rune.Value == '.' &&
                !_supportedSymbols.Contains(rune) &&
                TryConsumeAsciiEllipsis(text, index, out int ellipsisLength))
            {
                AppendReplacement(
                    builder,
                    _ellipsisFallback,
                    CollapseKind.Ellipsis,
                    ref lastCollapseKind);
                index += ellipsisLength;
                continue;
            }

            // Lexical apostrophes/hyphens belong to orthography and must reach eSpeak even when
            // they are not model phonemes themselves.
            if (IsLexicalConnector(text, index, consumed, rune))
            {
                AppendRune(builder, rune);
                lastCollapseKind = CollapseKind.None;
                index += consumed;
                continue;
            }

            // Never allow raw text to inject Piper's structural IDs.
            if (IsModelControlRune(rune))
            {
                index += consumed;
                continue;
            }

            // Quotes are visual structure rather than phonetic content, but removing one
            // must never glue the surrounding text together. Insert one ordinary word separator
            // only when meaningful content exists on both sides and no separator is already present.
            // This preserves lexical boundaries without inventing a comma-like pause or forcing
            // quote-specific prosody on models that do not provide it.
            if (IsVisualQuote(rune))
            {
                AppendQuoteSeparatorIfNeeded(builder, text, index + consumed);
                index += consumed;
                continue;
            }

            // Preserve symbols explicitly known by this model. Whitespace is transparent for
            // collapse state: this lets `comma + removed quote/garbage + comma` collapse even
            // when spaces remain between the two boundaries.
            if (_supportedSymbols.Contains(rune))
            {
                if (Rune.IsWhiteSpace(rune))
                {
                    AppendRune(builder, rune);
                    index += consumed;
                    continue;
                }

                CollapseKind collapseKind = GetNativeCollapseKind(rune);

                AppendNativeRune(
                    builder,
                    rune,
                    collapseKind,
                    ref lastCollapseKind);
                index += consumed;
                continue;
            }

            // Known punctuation families use a replacement resolved once at startup.
            if (_fallbacks.TryGetValue(rune, out string? replacement))
            {
                SemanticKinds.TryGetValue(rune, out PunctuationKind sourceKind);

                AppendReplacement(
                    builder,
                    replacement,
                    GetReplacementCollapseKind(sourceKind, replacement),
                    ref lastCollapseKind);
                index += consumed;
                continue;
            }

            // Unknown Unicode punctuation is still handled safely. We cannot infer its exact
            // linguistic semantics without a catalog entry, so degrade to the model's weakest
            // generic pause instead of forwarding an unsupported symbol.
            if (IsUnicodePunctuation(rune))
            {
                AppendReplacement(
                    builder,
                    _genericPunctuationFallback,
                    IsWeakPauseReplacement(_genericPunctuationFallback)
                        ? CollapseKind.WeakPause
                        : CollapseKind.None,
                    ref lastCollapseKind);
                index += consumed;
                continue;
            }

            // Ordinary letters, digits, marks, and whitespace remain untouched for eSpeak.
            if (IsSafeTextRune(rune))
            {
                AppendRune(builder, rune);

                // Keep collapse state across whitespace so a removed quote/symbol between two
                // equivalent boundaries cannot reintroduce duplicate punctuation. Real text ends it.
                if (!Rune.IsWhiteSpace(rune))
                {
                    lastCollapseKind = CollapseKind.None;
                }
            }

            // Emoji, dingbats, math symbols, and other unsupported non-text symbols are stripped.
            // Deliberately keep lastCollapseKind unchanged across stripped garbage.
            index += consumed;
        }

        return builder.ToString();
    }

    private bool NeedsNormalization(ReadOnlySpan<char> text)
    {
        int index = 0;
        CollapseKind lastCollapseKind = CollapseKind.None;

        while (index < text.Length)
        {
            int consumed = DecodeRune(text[index..], out Rune rune);

            if (rune.Value == '.' &&
                !_supportedSymbols.Contains(rune) &&
                TryConsumeAsciiEllipsis(text, index, out _))
            {
                return true;
            }

            if (IsLexicalConnector(text, index, consumed, rune))
            {
                lastCollapseKind = CollapseKind.None;
                index += consumed;
                continue;
            }

            if (IsModelControlRune(rune) || IsVisualQuote(rune))
            {
                return true;
            }

            if (_supportedSymbols.Contains(rune))
            {
                if (Rune.IsWhiteSpace(rune))
                {
                    index += consumed;
                    continue;
                }

                CollapseKind collapseKind = GetNativeCollapseKind(rune);
                if (collapseKind != CollapseKind.None &&
                    collapseKind == lastCollapseKind)
                {
                    return true;
                }

                lastCollapseKind = collapseKind;
                index += consumed;
                continue;
            }

            if (_fallbacks.ContainsKey(rune) || IsUnicodePunctuation(rune))
            {
                return true;
            }

            if (IsSafeTextRune(rune))
            {
                if (!Rune.IsWhiteSpace(rune))
                {
                    lastCollapseKind = CollapseKind.None;
                }

                index += consumed;
                continue;
            }

            // Unknown unsupported symbol would be stripped.
            return true;
        }

        return false;
    }

    private string SelectSupported(string candidates)
    {
        foreach (Rune rune in candidates.EnumerateRunes())
        {
            if (!IsModelControlRune(rune) && _supportedSymbols.Contains(rune))
            {
                return rune.ToString();
            }
        }

        return string.Empty;
    }

    private string SpaceFallback()
    {
        return _supportsSpace ? " " : string.Empty;
    }

    private string BuildExtendedPause(string terminator)
    {
        if (_supportsSpace)
        {
            return string.IsNullOrWhiteSpace(terminator)
                ? "  "
                : "  " + terminator;
        }

        return terminator;
    }

    private string SelectUnknownSupportedPause()
    {
        foreach (Rune rune in _supportedSymbols)
        {
            if (IsModelControlRune(rune) ||
                IsVisualQuote(rune) ||
                SemanticKinds.ContainsKey(rune))
            {
                continue;
            }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.OtherPunctuation or UnicodeCategory.DashPunctuation)
            {
                return rune.ToString();
            }
        }

        return string.Empty;
    }

    // Startup-only selection helpers intentionally use fixed-arity overloads instead of params.
    // This avoids temporary string[] allocations while keeping the degradation chains readable.
    private static string FirstNonEmpty(string a, string b)
    {
        if (!string.IsNullOrEmpty(a)) return a;
        return b;
    }

    private static string FirstNonEmpty(string a, string b, string c)
    {
        if (!string.IsNullOrEmpty(a)) return a;
        if (!string.IsNullOrEmpty(b)) return b;
        return c;
    }

    private static string FirstNonEmpty(string a, string b, string c, string d)
    {
        if (!string.IsNullOrEmpty(a)) return a;
        if (!string.IsNullOrEmpty(b)) return b;
        if (!string.IsNullOrEmpty(c)) return c;
        return d;
    }

    private static string FirstNonEmpty(
        string a,
        string b,
        string c,
        string d,
        string e)
    {
        if (!string.IsNullOrEmpty(a)) return a;
        if (!string.IsNullOrEmpty(b)) return b;
        if (!string.IsNullOrEmpty(c)) return c;
        if (!string.IsNullOrEmpty(d)) return d;
        return e;
    }

    private static string Combine(string first, string second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
        {
            return string.Empty;
        }

        return string.Concat(first, second);
    }

    private static string FirstNonEmpty(
        string a,
        string b,
        string c,
        string d,
        string e,
        string f)
    {
        if (!string.IsNullOrEmpty(a)) return a;
        if (!string.IsNullOrEmpty(b)) return b;
        if (!string.IsNullOrEmpty(c)) return c;
        if (!string.IsNullOrEmpty(d)) return d;
        if (!string.IsNullOrEmpty(e)) return e;
        return f;
    }

    private static CollapseKind GetNativeCollapseKind(Rune rune)
    {
        if (!SemanticKinds.TryGetValue(rune, out PunctuationKind kind))
        {
            return CollapseKind.None;
        }

        return kind switch
        {
            PunctuationKind.Comma => CollapseKind.WeakPause,
            PunctuationKind.Ellipsis => CollapseKind.Ellipsis,
            _ => CollapseKind.None
        };
    }

    private CollapseKind GetReplacementCollapseKind(
        PunctuationKind sourceKind,
        string replacement)
    {
        if (sourceKind == PunctuationKind.Ellipsis)
        {
            return CollapseKind.Ellipsis;
        }

        return IsWeakPauseReplacement(replacement)
            ? CollapseKind.WeakPause
            : CollapseKind.None;
    }

    private bool IsWeakPauseReplacement(string replacement)
    {
        return !string.IsNullOrEmpty(_commaFallback) &&
               string.Equals(replacement, _commaFallback, StringComparison.Ordinal);
    }

    private bool HasAnyUsablePunctuation()
    {
        foreach (Rune rune in _supportedSymbols)
        {
            if (IsModelControlRune(rune) || IsVisualQuote(rune))
            {
                continue;
            }

            if (IsUnicodePunctuation(rune))
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendNativeRune(
        StringBuilder builder,
        Rune rune,
        CollapseKind collapseKind,
        ref CollapseKind lastCollapseKind)
    {
        if (collapseKind != CollapseKind.None &&
            collapseKind == lastCollapseKind)
        {
            return;
        }

        AppendRune(builder, rune);
        lastCollapseKind = collapseKind;
    }

    private static void AppendReplacement(
        StringBuilder builder,
        string replacement,
        CollapseKind collapseKind,
        ref CollapseKind lastCollapseKind)
    {
        if (string.IsNullOrEmpty(replacement))
        {
            // Stripped punctuation is transparent for collapse purposes. This prevents
            // equivalent boundaries separated only by removed garbage from being duplicated.
            return;
        }

        if (collapseKind != CollapseKind.None &&
            collapseKind == lastCollapseKind)
        {
            return;
        }

        builder.Append(replacement);
        lastCollapseKind = collapseKind;
    }

    private static FrozenDictionary<Rune, PunctuationKind> BuildSemanticKinds()
    {
        var map = new Dictionary<Rune, PunctuationKind>();

        AddSemanticGroup(map, PunctuationKind.Interrobang, InterrobangMarks);
        AddSemanticGroup(map, PunctuationKind.DoubleQuestion, DoubleQuestionMarks);
        AddSemanticGroup(map, PunctuationKind.QuestionExclamation, QuestionExclamationMarks);
        AddSemanticGroup(map, PunctuationKind.ExclamationQuestion, ExclamationQuestionMarks);
        AddSemanticGroup(map, PunctuationKind.Ellipsis, EllipsisMarks);
        AddSemanticGroup(map, PunctuationKind.OpeningQuestion, OpeningQuestionMarks);
        AddSemanticGroup(map, PunctuationKind.OpeningExclamation, OpeningExclamationMarks);
        AddSemanticGroup(map, PunctuationKind.Question, QuestionMarks);
        AddSemanticGroup(map, PunctuationKind.Exclamation, ExclamationMarks);
        AddSemanticGroup(map, PunctuationKind.Period, PeriodMarks);
        AddSemanticGroup(map, PunctuationKind.Comma, CommaMarks);
        AddSemanticGroup(map, PunctuationKind.Semicolon, SemicolonMarks);
        AddSemanticGroup(map, PunctuationKind.Colon, ColonMarks);
        AddSemanticGroup(map, PunctuationKind.Dash, DashMarks);
        AddSemanticGroup(map, PunctuationKind.OpenBracket, OpenBracketMarks);
        AddSemanticGroup(map, PunctuationKind.CloseBracket, CloseBracketMarks);
        AddSemanticGroup(map, PunctuationKind.WordSeparator, WordSeparatorMarks);

        return map.ToFrozenDictionary();
    }

    private static void AddSemanticGroup(
        Dictionary<Rune, PunctuationKind> map,
        PunctuationKind kind,
        string symbols)
    {
        foreach (Rune rune in symbols.EnumerateRunes())
        {
            map.TryAdd(rune, kind);
        }
    }

    private static bool TryConsumeAsciiEllipsis(
        ReadOnlySpan<char> text,
        int index,
        out int consumed)
    {
        consumed = 0;
        int cursor = index;

        while (cursor < text.Length && text[cursor] == '.')
        {
            cursor++;
        }

        int count = cursor - index;
        if (count < 3)
        {
            return false;
        }

        consumed = count;
        return true;
    }

    private static void AppendQuoteSeparatorIfNeeded(
        StringBuilder builder,
        ReadOnlySpan<char> text,
        int nextIndex)
    {
        if (builder.Length == 0 || char.IsWhiteSpace(builder[^1]))
        {
            return;
        }

        int cursor = nextIndex;
        while (cursor < text.Length)
        {
            int consumed = DecodeRune(text[cursor..], out Rune next);

            if (IsVisualQuote(next) || IsModelControlRune(next))
            {
                cursor += consumed;
                continue;
            }

            if (Rune.IsWhiteSpace(next) || IsUnicodePunctuation(next))
            {
                return;
            }

            if (IsSafeTextRune(next))
            {
                builder.Append(' ');
            }

            return;
        }
    }

    private static bool IsVisualQuote(Rune rune)
    {
        return VisualQuotes.Contains(rune) ||
               Rune.GetUnicodeCategory(rune) is UnicodeCategory.InitialQuotePunctuation
                   or UnicodeCategory.FinalQuotePunctuation;
    }

    private static FrozenSet<Rune> BuildRuneSet(string symbols)
    {
        var set = new HashSet<Rune>();

        foreach (Rune rune in symbols.EnumerateRunes())
        {
            set.Add(rune);
        }

        return set.ToFrozenSet();
    }

    private static bool IsLexicalConnector(
        ReadOnlySpan<char> text,
        int index,
        int consumed,
        Rune rune)
    {
        if (!IsConnectorRune(rune))
        {
            return false;
        }

        if (index == 0 || index + consumed >= text.Length)
        {
            return false;
        }

        if (!TryDecodePreviousRune(text[..index], out Rune previous) ||
            !TryDecodeNextRune(text[(index + consumed)..], out Rune next))
        {
            return false;
        }

        return IsWordRune(previous) && IsWordRune(next);
    }

    private static bool IsConnectorRune(Rune rune)
    {
        return rune.Value is
            '\'' or '’' or 'ʼ' or
            '-' or '\u2010' or '\u2011' or
            '\u058A' or '\u05BE' or '\u30A0';
    }

    private static bool IsModelControlRune(Rune rune)
    {
        return rune.Value is '^' or '$' or '_';
    }

    private static bool IsUnicodePunctuation(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or
            UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or
            UnicodeCategory.FinalQuotePunctuation or
            UnicodeCategory.OtherPunctuation;
    }

    private static bool IsWordRune(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;
    }

    private static bool IsSafeTextRune(Rune rune)
    {
        if (Rune.IsWhiteSpace(rune))
        {
            return true;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.LetterNumber or
            UnicodeCategory.OtherNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;
    }

    private static void AppendRune(StringBuilder builder, Rune rune)
    {
        Span<char> buffer = stackalloc char[2];
        int written = rune.EncodeToUtf16(buffer);
        builder.Append(buffer[..written]);
    }

    private static bool TryGetSingleRune(string value, out Rune rune)
    {
        rune = default;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        OperationStatus status = Rune.DecodeFromUtf16(
            value.AsSpan(),
            out rune,
            out int consumed);

        return status == OperationStatus.Done && consumed == value.Length;
    }

    private static int DecodeRune(ReadOnlySpan<char> text, out Rune rune)
    {
        OperationStatus status = Rune.DecodeFromUtf16(
            text,
            out rune,
            out int consumed);

        if (status == OperationStatus.Done)
        {
            return consumed;
        }

        rune = Rune.ReplacementChar;
        return 1;
    }

    private static bool TryDecodePreviousRune(
        ReadOnlySpan<char> text,
        out Rune rune)
    {
        OperationStatus status = Rune.DecodeLastFromUtf16(
            text,
            out rune,
            out _);

        return status == OperationStatus.Done;
    }

    private static bool TryDecodeNextRune(
        ReadOnlySpan<char> text,
        out Rune rune)
    {
        OperationStatus status = Rune.DecodeFromUtf16(
            text,
            out rune,
            out _);

        return status == OperationStatus.Done;
    }
}