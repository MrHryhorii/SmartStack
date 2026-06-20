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

    // Matches standard IPA blocks delimited by /.../ or [...]. 
    // Acts as a candidate filter before checking for actual IPA symbols.
    [GeneratedRegex(@"/(?!\d+/)([^/]+?)/|\[(?!\d+\])([^\]]+?)\]", RegexOptions.Compiled)]
    private static partial Regex RawPhonemeBlockRegex();

    // Explicit allowlist of IPA symbols to distinguish actual phonetic transcription 
    // from unrelated orthography (e.g., URLs or markdown links).
    private static readonly SearchValues<char> IpaSearchValues = SearchValues.Create("ɑɐɒæɓʙβɔɕçɗɖðʤəɘɚɛɜɝɞɟʄɡɠɢʛɦɧħɥʜɨɪʝɭɬɫɮʟɱɯɰŋɳɲɴøɵɸθœɶʘɹɺɾɻʀʁɽʂʃʈʧʉʊʋⱱʌɣɤʍχʎʏʐʑʒʔʡʕʢǀǁǂǃˈˌːˑʼʴʰʲʷˠʲˤ");

    private static bool ContainsIpaSymbol(ReadOnlySpan<char> text) => text.IndexOfAny(IpaSearchValues) >= 0;

    // Extracts raw IPA blocks to prevent the language detector and eSpeak 
    // from treating hand-written phonemes as ordinary orthographic text.
    private static List<(string Text, bool IsRawPhonemes)> SplitRawPhonemeBlocks(string text)
    {
        var segments = new List<(string Text, bool IsRawPhonemes)>();
        int lastEnd = 0;
        ReadOnlySpan<char> textSpan = text.AsSpan();

        foreach (ValueMatch match in RawPhonemeBlockRegex().EnumerateMatches(textSpan))
        {
            // Regex matches delimiters at the boundaries. We slice them out manually 
            // since EnumerateMatches avoids allocating Group objects on the heap.
            ReadOnlySpan<char> phonemeContent = textSpan.Slice(match.Index + 1, match.Length - 2);

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
    public string GetPhonemes(string text)
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
                tokens.AddRange(_mixedPhonemizer?.ProcessTextToLanguageTokens(segmentText)
                    ?? [new TextChunk { Text = segmentText, DetectedLanguage = _piperConfig.Espeak.Voice ?? "en", IsPunctuationOrSpace = false }]);
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