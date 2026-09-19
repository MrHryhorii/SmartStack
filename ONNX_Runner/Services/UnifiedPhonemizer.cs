using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Central orchestrator for phonetic transcription. Integrates language detection,
/// eSpeak, punctuation mapping, and model-specific phoneme fallbacks into an optimized pipeline.
/// </summary>
public partial class UnifiedPhonemizer
{
    private readonly EspeakWrapper _espeakWrapper;
    private readonly DynamicPunctuationMapper _punctuationMapper;
    private readonly PiperConfig _piperConfig;
    private readonly MixedLanguagePhonemizer? _mixedPhonemizer;
    private readonly PhonemeFallbackMapper? _fallbackMapper;

    // Cached model language metadata avoids splitting/normalizing the same eSpeak code per request.
    private readonly string _modelEspeakVoice;
    private readonly string _modelEspeakCode;
    private readonly string _canonicalModelEspeakCode;
    private readonly string[] _modelEspeakParts;

    // Fast model-inventory lookup used by the IPA validation path.
    private readonly HashSet<string> _supportedPhonemes;
    private readonly int _maxSupportedPhonemeLength;

    // Matches standard IPA blocks delimited by /.../, [...], or the eSpeak-style
    // double-bracket [[...]] convention for raw phoneme input.
    [GeneratedRegex(@"/(?!\d+/)([^/]+?)/|\[\[([^\]]+?)\]\]|\[(?!\d+\])([^\]]+?)\]", RegexOptions.Compiled)]
    private static partial Regex RawPhonemeBlockRegex();

    // Explicit allowlist of IPA symbols to distinguish actual phonetic transcription
    // from unrelated orthography (e.g., URLs or markdown links).
    private static readonly SearchValues<char> IpaSearchValues = SearchValues.Create(
        "ɑɐɒæɓʙβɔɕçɗɖðʤəɘɚɛɜɝɞɟʄɡɠɢʛɦɧħɥʜɨɪʝɭɬɫɮʟɱɯɰŋɳɲɴøɵɸθœɶʘɹɺɾɻʀʁɽʂʃʈʧʉʊʋⱱʌɣɤʍχʎʏʐʑʒʔʡʕʢǀǁǂǃˈˌːˑʼʴʰʲʷˠˤ");

    public UnifiedPhonemizer(
        EspeakWrapper espeakWrapper,
        DynamicPunctuationMapper punctuationMapper,
        PiperConfig piperConfig,
        MixedLanguagePhonemizer? mixedPhonemizer = null,
        PhonemeFallbackMapper? fallbackMapper = null)
    {
        _espeakWrapper = espeakWrapper;
        _punctuationMapper = punctuationMapper;
        _piperConfig = piperConfig;
        _mixedPhonemizer = mixedPhonemizer;
        _fallbackMapper = fallbackMapper;

        _modelEspeakVoice = _piperConfig.Espeak.Voice ?? "en";
        _modelEspeakCode = _modelEspeakVoice
            .Trim()
            .ToLowerInvariant();
        _canonicalModelEspeakCode = CanonicalizeEspeakVoiceCode(_modelEspeakCode);

        _modelEspeakParts = _modelEspeakCode.Split(
            ['-', '_'],
            StringSplitOptions.RemoveEmptyEntries);

        _supportedPhonemes = _piperConfig.PhonemeIdMap != null
            ? new HashSet<string>(_piperConfig.PhonemeIdMap.Keys, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        int maxSupportedLength = 1;
        foreach (string phoneme in _supportedPhonemes)
        {
            if (phoneme.Length > maxSupportedLength)
            {
                maxSupportedLength = phoneme.Length;
            }
        }

        _maxSupportedPhonemeLength = maxSupportedLength;
    }

    // Checks whether a span contains at least one explicit IPA symbol.
    private static bool ContainsIpaSymbol(ReadOnlySpan<char> text)
    {
        return text.IndexOfAny(IpaSearchValues) >= 0;
    }

    // Mirrors MixedLanguagePhonemizer's own forced-language resolution (smart inheritance +
    // trim/lowercase normalization) for the no-detector fallback, so the "language" parameter
    // behaves the same regardless of whether the statistical detector is registered.
    private string ResolveFallbackLanguage(string? forcedLanguage)
    {
        if (string.IsNullOrWhiteSpace(forcedLanguage))
        {
            return _modelEspeakCode;
        }

        return MixedLanguagePhonemizer.ResolveForcedLanguageCode(
            forcedLanguage,
            _modelEspeakCode,
            _modelEspeakParts);
    }

    // Extracts raw IPA blocks to prevent the language detector and eSpeak
    // from treating hand-written phonemes as ordinary orthographic text.
    private static List<(string Text, bool IsRawPhonemes)> SplitRawPhonemeBlocks(string text)
    {
        var segments = new List<(string Text, bool IsRawPhonemes)>(4);
        int lastEnd = 0;
        ReadOnlySpan<char> textSpan = text.AsSpan();

        foreach (ValueMatch match in RawPhonemeBlockRegex().EnumerateMatches(textSpan))
        {
            // Double brackets are an explicit eSpeak/raw-phoneme contract and therefore bypass
            // orthographic heuristics completely. Single brackets and /.../ are more ambiguous
            // (markdown, paths, ordinary notation), so they still need an explicit IPA signal.
            bool isExplicitDoubleBracket =
                match.Length >= 4 &&
                textSpan[match.Index] == '[' &&
                textSpan[match.Index + 1] == '[' &&
                textSpan[match.Index + match.Length - 1] == ']' &&
                textSpan[match.Index + match.Length - 2] == ']';

            int delimLen = isExplicitDoubleBracket ? 2 : 1;

            ReadOnlySpan<char> phonemeContent = textSpan.Slice(
                match.Index + delimLen,
                match.Length - (2 * delimLen));

            if (!isExplicitDoubleBracket && !ContainsIpaSymbol(phonemeContent))
            {
                continue;
            }

            if (match.Index > lastEnd)
            {
                segments.Add((textSpan[lastEnd..match.Index].ToString(), false));
            }

            segments.Add((phonemeContent.ToString(), true));
            lastEnd = match.Index + match.Length;
        }

        if (lastEnd < text.Length)
        {
            segments.Add((text[lastEnd..], false));
        }

        return segments;
    }

    /// <summary>
    /// Converts a raw input string into a continuous stream of validated IPA phonemes.
    /// Foreign IPA is preserved whenever the loaded model supports it and otherwise adapted
    /// through model-specific complex-sequence rules and PHOIBLE nearest-neighbor fallbacks.
    /// </summary>
    public string GetPhonemes(string text, string? forcedLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var finalPhonemes = new StringBuilder(Math.Max(64, text.Length * 2));
        var tokens = new List<TextChunk>(8);

        // Pre-process raw IPA blocks (Green Channel) and apply smart mode selection.
        foreach (var (segmentText, isRaw) in SplitRawPhonemeBlocks(text))
        {
            if (isRaw)
            {
                tokens.Add(new TextChunk
                {
                    Text = segmentText,
                    DetectedLanguage = "raw",
                    IsRawPhonemes = true
                });

                continue;
            }

            if (string.IsNullOrEmpty(segmentText))
            {
                continue;
            }

            // Language segmentation must see the original orthography. Normalizing punctuation
            // first would destroy structural evidence inside URLs, paths, emails, code-like tokens,
            // and abbreviations before the mixed-language tokenizer can protect them. Raw IPA blocks
            // were already extracted above and remain completely untouched.
            if (string.IsNullOrWhiteSpace(segmentText))
            {
                continue;
            }

            // Pass forcedLanguage to the detector. If the detector is disabled, resolve the
            // language locally using the same smart-inheritance rule.
            if (_mixedPhonemizer != null)
            {
                tokens.AddRange(
                    _mixedPhonemizer.ProcessTextToLanguageTokens(
                        segmentText,
                        forcedLanguage));

                continue;
            }

            tokens.Add(new TextChunk
            {
                Text = segmentText,
                DetectedLanguage = ResolveFallbackLanguage(forcedLanguage),
                IsPunctuationOrSpace = false
            });
        }

        for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            TextChunk chunk = tokens[tokenIndex];

            if (chunk.IsRawPhonemes)
            {
                // Green channel: bypass eSpeak entirely for pre-written IPA, but still validate
                // it against the current model so unsupported foreign sounds are adapted safely.
                AppendValidatedPhonemes(chunk.Text, finalPhonemes);
                continue;
            }

            // Punctuation normalization intentionally happens AFTER language segmentation. The
            // structural tokenizer therefore sees original technical syntax, while eSpeak/Piper
            // still receives only punctuation supported (or safely degraded) by the loaded model.
            string normalizedText = _punctuationMapper.Normalize(chunk.Text);
            if (normalizedText.Length == 0)
            {
                continue;
            }

            if (chunk.IsPunctuationOrSpace)
            {
                AppendPunctuationToken(
                    chunk,
                    normalizedText,
                    tokenIndex,
                    tokens,
                    finalPhonemes);

                continue;
            }

            ReadOnlySpan<char> chunkSpan = normalizedText.AsSpan();

            // PREFIX, CORE, AND SUFFIX EXTRACTION:
            // Isolates the core word from surrounding punctuation to prevent eSpeak mispronunciations.
            int start = FindCoreStart(chunkSpan);
            int end = FindCoreEnd(chunkSpan, start);

            finalPhonemes.Append(chunkSpan[..start]);

            if (start < end)
            {
                string core = chunkSpan[start..end].ToString();
                string? rawPhonemes = TryGetIpaPhonemesSafely(
                    core,
                    chunk.DetectedLanguage);

                if (rawPhonemes != null)
                {
                    if (_fallbackMapper != null)
                    {
                        AppendValidatedPhonemes(rawPhonemes, finalPhonemes);
                    }
                    else
                    {
                        finalPhonemes.Append(rawPhonemes);
                    }
                }
            }

            finalPhonemes.Append(chunkSpan[end..]);
        }

        return finalPhonemes.ToString();
    }

    // Appends a punctuation token while suppressing title and acronym periods.
    private static void AppendPunctuationToken(
        TextChunk originalChunk,
        string normalizedText,
        int tokenIndex,
        List<TextChunk> tokens,
        StringBuilder output)
    {
        // TITLE & ACRONYM PERIOD STRIPPING:
        // Look back at the previous token to determine if the period belongs to a title or initial.
        bool skipPeriod = false;

        if (originalChunk.Text == "." &&
            tokenIndex > 0 &&
            tokenIndex < tokens.Count - 1)
        {
            ReadOnlySpan<char> prevText = tokens[tokenIndex - 1].Text.AsSpan().TrimEnd();
            int lastSpaceIdx = prevText.LastIndexOf(' ');
            ReadOnlySpan<char> lastWord = prevText[(lastSpaceIdx + 1)..];

            bool isTitle = TextChunker.CommonAbbreviations
                .GetAlternateLookup<ReadOnlySpan<char>>()
                .Contains(lastWord);

            bool isAcronym = IsSingleLetter(lastWord);
            skipPeriod = isTitle || isAcronym;
        }

        if (!skipPeriod)
        {
            // Append the model-native punctuation selected after structural language tokenization.
            // A sentence-final abbreviation period is intentionally preserved for TTS prosody.
            output.Append(normalizedText);
        }
    }

    // Atomically selects an eSpeak voice and phonemizes the text without falling back across
    // unrelated language families. Voice selection and native transcription happen under one
    // EspeakWrapper lock, eliminating the SetVoice -> GetIpaPhonemes race between requests.
    private string? TryGetIpaPhonemesSafely(string text, string language)
    {
        string resolvedLanguage = CanonicalizeEspeakVoiceCode(language);

        if (_espeakWrapper.TryGetIpaPhonemes(
                text,
                resolvedLanguage,
                out string phonemes))
        {
            return phonemes;
        }

        // Falling back to an English/model voice for a foreign script produces spoken labels
        // such as "Chinese letter" for every Han character. Only use the model voice when the
        // failed code belongs to the same base language family; otherwise skip that core safely.
        if (!HasSameBaseLanguage(
                resolvedLanguage,
                _canonicalModelEspeakCode))
        {
            return null;
        }

        return _espeakWrapper.TryGetIpaPhonemes(
            text,
            _modelEspeakVoice,
            out phonemes)
                ? phonemes
                : null;
    }

    // Normalizes equivalent eSpeak language aliases to one canonical code.
    private static string CanonicalizeEspeakVoiceCode(string language)
    {
        if (language.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-cn", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-hans", StringComparison.OrdinalIgnoreCase))
        {
            return "cmn";
        }

        return language;
    }

    // Checks whether two eSpeak voice codes belong to the same base language family.
    private static bool HasSameBaseLanguage(string left, string right)
    {
        ReadOnlySpan<char> leftSpan = left.AsSpan();
        ReadOnlySpan<char> rightSpan = right.AsSpan();

        int leftSeparator = leftSpan.IndexOfAny('-', '_');
        int rightSeparator = rightSpan.IndexOfAny('-', '_');

        if (leftSeparator >= 0)
        {
            leftSpan = leftSpan[..leftSeparator];
        }

        if (rightSeparator >= 0)
        {
            rightSpan = rightSpan[..rightSeparator];
        }

        return leftSpan.Equals(rightSpan, StringComparison.OrdinalIgnoreCase);
    }

    // Identifies letters, decimal digits, and combining marks to define the "core" of a word.
    // Rune-based scanning prevents supplementary-plane letters from being mistaken for punctuation.
    private static bool IsCoreRune(Rune rune)
    {
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

    // Finds the first rune that belongs to the speakable core of a token.
    private static int FindCoreStart(ReadOnlySpan<char> text)
    {
        int index = 0;

        while (index < text.Length)
        {
            int consumed = DecodeRune(text[index..], out Rune rune);

            if (IsCoreRune(rune))
            {
                return index;
            }

            index += consumed;
        }

        return text.Length;
    }

    // Finds the exclusive end of the speakable core of a token.
    private static int FindCoreEnd(ReadOnlySpan<char> text, int start)
    {
        int end = text.Length;

        while (end > start)
        {
            OperationStatus status = Rune.DecodeLastFromUtf16(
                text[..end],
                out Rune rune,
                out int consumed);

            if (status != OperationStatus.Done)
            {
                consumed = 1;
                rune = Rune.ReplacementChar;
            }

            if (IsCoreRune(rune))
            {
                return end;
            }

            end -= consumed;
        }

        return start;
    }

    // Checks whether a span contains exactly one Unicode letter.
    private static bool IsSingleLetter(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        int consumed = DecodeRune(text, out Rune rune);
        if (consumed != text.Length)
        {
            return false;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter;
    }

    // Decodes the first Unicode scalar and returns its UTF-16 width.
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

    /// <summary>
    /// Validates an IPA stream against the loaded model without assuming that one Unicode
    /// grapheme equals one phoneme. Exact model sequences win first, then active complex
    /// fallback rules, then ordinary PHOIBLE nearest-neighbor fallback for one IPA unit.
    /// Unknown content is preserved rather than deleted silently.
    /// </summary>
    private void AppendValidatedPhonemes(string phonemes, StringBuilder output)
    {
        if (_fallbackMapper == null || string.IsNullOrEmpty(phonemes))
        {
            output.Append(phonemes);
            return;
        }

        ReadOnlySpan<char> input = phonemes.AsSpan();
        var supportedLookup = _supportedPhonemes.GetAlternateLookup<ReadOnlySpan<char>>();
        int index = 0;

        while (index < input.Length)
        {
            ReadOnlySpan<char> remaining = input[index..];

            // 1. Preserve a multi-character model token exactly when the model explicitly knows it.
            // This includes true multi-phoneme tokens and supplementary-plane scalar values.
            if (TryFindLongestSupported(
                remaining,
                minimumLength: 2,
                out int supportedLength))
            {
                output.Append(remaining[..supportedLength]);
                index += supportedLength;
                continue;
            }

            // 2. Apply complex IPA fallback rules only when startup analysis determined that
            // the current model cannot represent that source sequence directly.
            if (TryFindSequenceFallback(
                remaining,
                out int sequenceLength,
                out string? sequenceFallback))
            {
                output.Append(sequenceFallback);
                index += sequenceLength;
                continue;
            }

            // 3. Resolve exactly one IPA unit: one Unicode scalar plus trailing combining marks.
            // Do NOT consume a supported base symbol before checking the full unit. Otherwise a
            // sequence such as ɛ̃ could become "ɛ" + orphaned combining tilde and bypass its fallback.
            int unitLength = PhonemeFallbackMapper.GetPhonemeUnitLength(remaining);
            ReadOnlySpan<char> unit = remaining[..unitLength];

            if (supportedLookup.Contains(unit))
            {
                output.Append(unit);
                index += unitLength;
                continue;
            }

            if (_fallbackMapper.TryGetClosestPhoneme(unit, out string? fallback) &&
                !string.IsNullOrEmpty(fallback))
            {
                output.Append(fallback);
            }
            else
            {
                // CRITICAL: Never silently drop an unknown phoneme. Preserving the source is safer
                // than creating an unexplained gap and lets downstream diagnostics expose the issue.
                output.Append(unit);
            }

            index += unitLength;
        }
    }

    // Finds the longest active complex IPA sequence with a precomputed fallback.
    private bool TryFindSequenceFallback(
        ReadOnlySpan<char> input,
        out int matchedLength,
        out string? fallback)
    {
        matchedLength = 0;
        fallback = null;

        if (_fallbackMapper == null || _fallbackMapper.MaxSequenceLength <= 0)
        {
            return false;
        }

        int maxLength = Math.Min(
            _fallbackMapper.MaxSequenceLength,
            input.Length);

        for (int length = maxLength; length > 0; length--)
        {
            if (SplitsSurrogatePair(input, length))
            {
                continue;
            }

            if (!_fallbackMapper.TryGetSequenceFallback(input[..length], out string candidate))
            {
                continue;
            }

            matchedLength = length;
            fallback = candidate;
            return true;
        }

        return false;
    }

    // Finds the longest model-supported phoneme beginning at the current position.
    private bool TryFindLongestSupported(
        ReadOnlySpan<char> input,
        int minimumLength,
        out int matchedLength)
    {
        matchedLength = 0;

        var supportedLookup = _supportedPhonemes.GetAlternateLookup<ReadOnlySpan<char>>();

        int maxLength = Math.Min(
            _maxSupportedPhonemeLength,
            input.Length);

        for (int length = maxLength; length >= minimumLength; length--)
        {
            if (SplitsSurrogatePair(input, length))
            {
                continue;
            }

            if (!supportedLookup.Contains(input[..length]))
            {
                continue;
            }

            matchedLength = length;
            return true;
        }

        return false;
    }

    // Checks whether a candidate UTF-16 length would split a surrogate pair.
    private static bool SplitsSurrogatePair(ReadOnlySpan<char> input, int length)
    {
        return length > 0 &&
               length < input.Length &&
               char.IsHighSurrogate(input[length - 1]) &&
               char.IsLowSurrogate(input[length]);
    }
}
