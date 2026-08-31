using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Central orchestrator for phonetic transcription. Integrates language detection, 
/// eSpeak, punctuation mapping, and phoneme fallbacks into an optimized pipeline.
/// </summary>
public partial class UnifiedPhonemizer(
    EspeakWrapper espeakWrapper,
    DynamicPunctuationMapper punctuationMapper,
    PiperConfig piperConfig,
    MixedLanguagePhonemizer? mixedPhonemizer = null,
    PhonemeFallbackMapper? fallbackMapper = null)
{
    private readonly EspeakWrapper _espeakWrapper = espeakWrapper;
    private readonly DynamicPunctuationMapper _punctuationMapper = punctuationMapper;
    private readonly PiperConfig _piperConfig = piperConfig;
    private readonly MixedLanguagePhonemizer? _mixedPhonemizer = mixedPhonemizer;
    private readonly PhonemeFallbackMapper? _fallbackMapper = fallbackMapper;

    // Matches standard IPA blocks delimited by /.../, [...], or the eSpeak-style
    // double-bracket [[...]] convention for raw phoneme input.
    [GeneratedRegex(@"/(?!\d+/)([^/]+?)/|\[\[([^\]]+?)\]\]|\[(?!\d+\])([^\]]+?)\]", RegexOptions.Compiled)]
    private static partial Regex RawPhonemeBlockRegex();

    // Explicit allowlist of IPA symbols to distinguish actual phonetic transcription 
    // from unrelated orthography (e.g., URLs or markdown links).
    private static readonly SearchValues<char> IpaSearchValues = SearchValues.Create("ɑɐɒæɓʙβɔɕçɗɖðʤəɘɚɛɜɝɞɟʄɡɠɢʛɦɧħɥʜɨɪʝɭɬɫɮʟɱɯɰŋɳɲɴøɵɸθœɶʘɹɺɾɻʀʁɽʂʃʈʧʉʊʋⱱʌɣɤʍχʎʏʐʑʒʔʡʕʢǀǁǂǃˈˌːˑʼʴʰʲʷˠʲˤ");

    private static bool ContainsIpaSymbol(ReadOnlySpan<char> text) => text.IndexOfAny(IpaSearchValues) >= 0;

    // Mirrors MixedLanguagePhonemizer's own forced-language resolution (smart inheritance +
    // trim/lowercase normalization) for the no-detector fallback, so the "language" parameter
    // behaves the same regardless of whether the statistical detector is registered.
    private string ResolveFallbackLanguage(string? forcedLanguage)
    {
        string modelCode = (_piperConfig.Espeak.Voice ?? "en").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(forcedLanguage)) return modelCode;
        
        string[] modelParts = modelCode.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return MixedLanguagePhonemizer.ResolveForcedLanguageCode(forcedLanguage, modelCode, modelParts);
    }

    // Extracts raw IPA blocks to prevent the language detector and eSpeak 
    // from treating hand-written phonemes as ordinary orthographic text.
    private static List<(string Text, bool IsRawPhonemes)> SplitRawPhonemeBlocks(string text)
    {
        var segments = new List<(string Text, bool IsRawPhonemes)>();
        int lastEnd = 0;
        ReadOnlySpan<char> textSpan = text.AsSpan();

        foreach (ValueMatch match in RawPhonemeBlockRegex().EnumerateMatches(textSpan))
        {
            // Determine delimiter width: "/" and "[" are single-char delimiters,
            // while "[[...]]" (eSpeak raw-phoneme convention) uses two chars on each side.
            int delimLen = 1;
            if (match.Length >= 4 &&
                textSpan[match.Index] == '[' && textSpan[match.Index + 1] == '[' &&
                textSpan[match.Index + match.Length - 1] == ']' && textSpan[match.Index + match.Length - 2] == ']')
            {
                delimLen = 2;
            }

            ReadOnlySpan<char> phonemeContent = textSpan.Slice(match.Index + delimLen, match.Length - 2 * delimLen);

            if (!ContainsIpaSymbol(phonemeContent)) continue;

            if (match.Index > lastEnd)
                segments.Add((textSpan[lastEnd..match.Index].ToString(), false));

            segments.Add((phonemeContent.ToString(), true));
            lastEnd = match.Index + match.Length;
        }

        if (lastEnd < text.Length)
            segments.Add((text[lastEnd..].ToString(), false));

        return segments;
    }

    /// <summary>
    /// Converts a raw input string into a continuous stream of validated IPA phonemes.
    /// </summary>
    public string GetPhonemes(string text, string? forcedLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var finalPhonemes = new StringBuilder();
        var tokens = new List<TextChunk>();

        // Pre-process raw IPA blocks (Green Channel) and apply smart mode selection.
        foreach (var (segmentText, isRaw) in SplitRawPhonemeBlocks(text))
        {
            if (isRaw)
            {
                tokens.Add(new TextChunk { Text = segmentText, DetectedLanguage = "raw", IsRawPhonemes = true });
            }
            else if (!string.IsNullOrEmpty(segmentText))
            {
                // Pass forcedLanguage to the detector.
                // If the detector is disabled (null), resolve forcedLanguage ourselves using the same
                // smart-inheritance rule the detector applies, so a forced language behaves identically
                // whether or not MixedLanguagePhonemizer is registered.
                tokens.AddRange(_mixedPhonemizer?.ProcessTextToLanguageTokens(segmentText, forcedLanguage)
                    ?? [new TextChunk { Text = segmentText, DetectedLanguage = ResolveFallbackLanguage(forcedLanguage), IsPunctuationOrSpace = false }]);
            }
        }

        for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            var chunk = tokens[tokenIndex];

            if (chunk.IsRawPhonemes)
            {
                // Green channel: bypass eSpeak entirely for pre-written IPA.
                AppendValidatedPhonemes(chunk.Text, finalPhonemes);
            }
            else if (chunk.IsPunctuationOrSpace)
            {
                // TITLE & ACRONYM PERIOD STRIPPING:
                // Look back at the previous token to determine if the period belongs to a title or initial.
                bool skipPeriod = false;

                if (chunk.Text == "." && tokenIndex > 0)
                {
                    ReadOnlySpan<char> prevText = tokens[tokenIndex - 1].Text.AsSpan().TrimEnd();
                    int lastSpaceIdx = prevText.LastIndexOf(' ');
                    ReadOnlySpan<char> lastWord = prevText[(lastSpaceIdx + 1)..];

                    bool isTitle = TextChunker.CommonTitles.GetAlternateLookup<ReadOnlySpan<char>>().Contains(lastWord);
                    bool isAcronym = lastWord.Length == 1 && char.IsLetter(lastWord[0]) && tokenIndex < tokens.Count - 1;

                    skipPeriod = isTitle || isAcronym;
                }

                if (!skipPeriod) finalPhonemes.Append(_punctuationMapper.Normalize(chunk.Text));
            }
            else
            {
                SetVoiceSafely(chunk.DetectedLanguage);

                string normalizedChunk = _punctuationMapper.Normalize(chunk.Text);
                ReadOnlySpan<char> chunkSpan = normalizedChunk.AsSpan();

                // PREFIX, CORE, AND SUFFIX EXTRACTION:
                // Isolates the core word from surrounding punctuation to prevent eSpeak mispronunciations.
                int start = 0, end = chunkSpan.Length;
                while (start < end && !IsCoreChar(chunkSpan[start])) start++;
                while (end > start && !IsCoreChar(chunkSpan[end - 1])) end--;

                finalPhonemes.Append(chunkSpan[..start]);

                if (start < end)
                {
                    string core = chunkSpan[start..end].ToString();
                    string rawPhonemes = _espeakWrapper.GetIpaPhonemes(core);

                    if (_fallbackMapper != null) AppendValidatedPhonemes(rawPhonemes, finalPhonemes);
                    else finalPhonemes.Append(rawPhonemes);
                }

                finalPhonemes.Append(chunkSpan[end..]);
            }
        }

        return finalPhonemes.ToString();
    }

    private void SetVoiceSafely(string language)
    {
        try { _espeakWrapper.SetVoice(language); }
        catch { _espeakWrapper.SetVoice(_piperConfig.Espeak.Voice ?? "en"); }
    }

    // Identifies letters, digits, and combining marks to define the "core" of a word.
    private static bool IsCoreChar(char c) =>
        char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    // Walks IPA symbols by grapheme cluster, applying model-specific acoustic fallbacks if needed.
    private void AppendValidatedPhonemes(string phonemes, StringBuilder output)
    {
        if (_fallbackMapper == null)
        {
            output.Append(phonemes);
            return;
        }

        var si = new StringInfo(phonemes);
        for (int i = 0; i < si.LengthInTextElements; i++)
        {
            string symbol = si.SubstringByTextElements(i, 1);

            if (_piperConfig.PhonemeIdMap.ContainsKey(symbol)) output.Append(symbol);
            else output.Append(_fallbackMapper.GetClosestPhoneme(symbol) ?? symbol);
        }
    }
}