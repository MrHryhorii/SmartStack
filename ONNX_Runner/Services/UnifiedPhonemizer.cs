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
    private readonly string _modelEspeakCode;
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

        _modelEspeakCode = (_piperConfig.Espeak.Voice ?? "en")
            .Trim()
            .ToLowerInvariant();

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
            // Determine delimiter width: "/" and "[" are single-char delimiters,
            // while "[[...]]" (eSpeak raw-phoneme convention) uses two chars on each side.
            int delimLen = 1;

            if (match.Length >= 4 &&
                textSpan[match.Index] == '[' &&
                textSpan[match.Index + 1] == '[' &&
                textSpan[match.Index + match.Length - 1] == ']' &&
                textSpan[match.Index + match.Length - 2] == ']')
            {
                delimLen = 2;
            }

            ReadOnlySpan<char> phonemeContent = textSpan.Slice(
                match.Index + delimLen,
                match.Length - (2 * delimLen));

            if (!ContainsIpaSymbol(phonemeContent))
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

            // Normalize the complete orthographic segment before language tokenization.
            // This lets punctuation collapse operate across future TextChunk boundaries and
            // prevents visual quotes, unsupported symbols, and model-specific punctuation from
            // leaking into eSpeak as pronounceable text. Raw IPA blocks were already extracted
            // above and therefore remain completely untouched.
            string normalizedSegment = _punctuationMapper.Normalize(segmentText);

            if (string.IsNullOrWhiteSpace(normalizedSegment))
            {
                continue;
            }

            // Pass forcedLanguage to the detector. If the detector is disabled, resolve the
            // language locally using the same smart-inheritance rule.
            if (_mixedPhonemizer != null)
            {
                tokens.AddRange(
                    _mixedPhonemizer.ProcessTextToLanguageTokens(
                        normalizedSegment,
                        forcedLanguage));

                continue;
            }

            tokens.Add(new TextChunk
            {
                Text = normalizedSegment,
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

            if (chunk.IsPunctuationOrSpace)
            {
                AppendPunctuationToken(
                    chunk,
                    tokenIndex,
                    tokens,
                    finalPhonemes);

                continue;
            }

            bool voiceAvailable = TrySetVoiceSafely(chunk.DetectedLanguage);

            // Non-raw text was normalized once at segment level before tokenization.
            // Re-normalizing each chunk would lose cross-chunk collapse state and duplicate work.
            ReadOnlySpan<char> chunkSpan = chunk.Text.AsSpan();

            // PREFIX, CORE, AND SUFFIX EXTRACTION:
            // Isolates the core word from surrounding punctuation to prevent eSpeak mispronunciations.
            int start = FindCoreStart(chunkSpan);
            int end = FindCoreEnd(chunkSpan, start);

            finalPhonemes.Append(chunkSpan[..start]);

            if (start < end && voiceAvailable)
            {
                string core = chunkSpan[start..end].ToString();
                string rawPhonemes = _espeakWrapper.GetIpaPhonemes(core);

                if (_fallbackMapper != null)
                {
                    AppendValidatedPhonemes(rawPhonemes, finalPhonemes);
                }
                else
                {
                    finalPhonemes.Append(rawPhonemes);
                }
            }

            finalPhonemes.Append(chunkSpan[end..]);
        }

        return finalPhonemes.ToString();
    }

    private void AppendPunctuationToken(
        TextChunk chunk,
        int tokenIndex,
        List<TextChunk> tokens,
        StringBuilder output)
    {
        // TITLE & ACRONYM PERIOD STRIPPING:
        // Look back at the previous token to determine if the period belongs to a title or initial.
        bool skipPeriod = false;

        if (chunk.Text == "." && tokenIndex > 0)
        {
            ReadOnlySpan<char> prevText = tokens[tokenIndex - 1].Text.AsSpan().TrimEnd();
            int lastSpaceIdx = prevText.LastIndexOf(' ');
            ReadOnlySpan<char> lastWord = prevText[(lastSpaceIdx + 1)..];

            bool isTitle = TextChunker.CommonTitles
                .GetAlternateLookup<ReadOnlySpan<char>>()
                .Contains(lastWord);

            bool isAcronym = IsSingleLetter(lastWord) &&
                             tokenIndex < tokens.Count - 1;

            skipPeriod = isTitle || isAcronym;
        }

        if (!skipPeriod)
        {
            // Punctuation was already normalized before tokenization, so append the model-native
            // token directly. Keeping this path allocation-free also preserves collapse decisions
            // that were made across the original segment.
            output.Append(chunk.Text);
        }
    }

    private bool TrySetVoiceSafely(string language)
    {
        string resolvedLanguage = CanonicalizeEspeakVoiceCode(language);

        try
        {
            _espeakWrapper.SetVoice(resolvedLanguage);
            return true;
        }
        catch
        {
            // Falling back to an English/model voice for a foreign script produces spoken labels
            // such as "Chinese letter" for every Han character. Only use the model voice when the
            // failed code belongs to the same base language family; otherwise skip that core safely.
            if (!HasSameBaseLanguage(
                    resolvedLanguage,
                    CanonicalizeEspeakVoiceCode(_modelEspeakCode)))
            {
                return false;
            }

            try
            {
                _espeakWrapper.SetVoice(_piperConfig.Espeak.Voice ?? "en");
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

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

    private static bool SplitsSurrogatePair(ReadOnlySpan<char> input, int length)
    {
        return length > 0 &&
               length < input.Length &&
               char.IsHighSurrogate(input[length - 1]) &&
               char.IsLowSurrogate(input[length]);
    }
}
