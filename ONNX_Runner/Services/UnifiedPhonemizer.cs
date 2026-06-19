using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// The central orchestrator for phonetic transcription.
/// It seamlessly integrates the mixed-language detector, the native eSpeak engine, 
/// the punctuation mapper, and the phoneme fallback system into a single, highly optimized pipeline.
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

    // Matches text inside /slashes/ or [brackets] as candidates for IPA transcription.
    // Requires further validation via ContainsIpaSymbol() to avoid false positives (URLs, Markdown).
    [GeneratedRegex(@"/(?!\d+/)([^/]+?)/|\[(?!\d+\])([^\]]+?)\]")]
    private static partial Regex RawPhonemeBlockRegex();

    // Curated allowlist of specific IPA symbols. Used to confidently verify if a text block 
    // inside brackets/slashes is an actual transcription and not just a URL or Markdown.
    // Uses SIMD instructions for zero-allocation, high-speed matching.
    private static readonly System.Buffers.SearchValues<char> IpaSearchValues =
        System.Buffers.SearchValues.Create("ɑɐɒæɓʙβɔɕçɗɖðʤəɘɚɛɜɝɞɟʄɡɠɢʛɦɧħɥʜɨɪʝɭɬɫɮʟɱɯɰŋɳɲɴøɵɸθœɶʘɹɺɾɻʀʁɽʂʃʈʧʉʊʋⱱʌɣɤʍχʎʏʐʑʒʔʡʕʢǀǁǂǃˈˌːˑʼʴʰʲʷˠʲˤ");

    private static bool ContainsIpaSymbol(ReadOnlySpan<char> text)
    {
        return text.IndexOfAny(IpaSearchValues) >= 0;
    }

    /// <summary>
    /// Extracts true IPA transcription blocks (e.g., [hɛˈloʊ]) from the text before language detection.
    /// Validates candidates to ensure URLs or Markdown links are ignored and left as plain text.
    /// </summary>
    private static List<(string Text, bool IsRawPhonemes)> SplitRawPhonemeBlocks(string text)
    {
        var segments = new List<(string Text, bool IsRawPhonemes)>();
        int lastEnd = 0;

        foreach (Match match in RawPhonemeBlockRegex().Matches(text))
        {
            // Group 1 captures /slash/ content, group 2 captures [bracket] content
            string phonemeContent = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

            if (!ContainsIpaSymbol(phonemeContent))
            {
                // False positive (e.g., URL or Markdown). Leave it untouched.
                continue;
            }

            if (match.Index > lastEnd)
            {
                segments.Add((text[lastEnd..match.Index], false));
            }

            segments.Add((phonemeContent, true));
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
    /// </summary>
    public string GetPhonemes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var finalPhonemes = new StringBuilder();

        // =====================================================================
        // RAW PHONEME EXTRACTION (Green Channel)
        // =====================================================================
        // Extract hand-written IPA blocks before normal language detection runs.
        var rawSegments = SplitRawPhonemeBlocks(text);

        var tokens = new List<TextChunk>();
        foreach (var (segmentText, isRaw) in rawSegments)
        {
            if (isRaw)
            {
                tokens.Add(new TextChunk
                {
                    Text = segmentText,
                    DetectedLanguage = "raw",
                    IsPunctuationOrSpace = false,
                    IsRawPhonemes = true
                });
                continue;
            }

            if (string.IsNullOrEmpty(segmentText)) continue;

            // =====================================================================
            // SMART MODE SELECTION
            // =====================================================================
            // If the mixed-language detector is active, it tokenizes the text into language-specific chunks.
            // Otherwise, it treats the entire text as the base model's default language.
            var segmentTokens = _mixedPhonemizer != null
                ? _mixedPhonemizer.ProcessTextToLanguageTokens(segmentText)
                : [new TextChunk { Text = segmentText, DetectedLanguage = _piperConfig.Espeak.Voice ?? "en", IsPunctuationOrSpace = false }];

            tokens.AddRange(segmentTokens);
        }

        // =====================================================================
        // MAIN PROCESSING LOOP
        // =====================================================================

        foreach (var chunk in tokens)
        {
            if (chunk.IsRawPhonemes)
            {
                // Green channel: Skip eSpeak and process raw phonemes directly through the fallback mapper.
                AppendValidatedPhonemes(chunk.Text, finalPhonemes);
            }
            else if (chunk.IsPunctuationOrSpace)
            {
                // Punctuation is universally mapped without invoking the heavy eSpeak engine
                finalPhonemes.Append(_punctuationMapper.Normalize(chunk.Text));
            }
            else
            {
                // Dynamically switch the native eSpeak voice based on the chunk's detected language.
                // If the voice is missing, safely fallback to the base model's default voice.
                try { _espeakWrapper.SetVoice(chunk.DetectedLanguage); }
                catch { _espeakWrapper.SetVoice(_piperConfig.Espeak.Voice ?? "en"); }

                string normalizedChunk = _punctuationMapper.Normalize(chunk.Text);
                ReadOnlySpan<char> chunkSpan = normalizedChunk.AsSpan();

                // =====================================================================
                // PREFIX, CORE, AND SUFFIX EXTRACTION
                // =====================================================================
                // eSpeak often mispronounces or crashes when words are attached to complex punctuation.
                // We isolate the actual word (core) from surrounding symbols (prefix/suffix).
                int start = 0;
                while (start < chunkSpan.Length && !IsCoreChar(chunkSpan[start])) start++;

                int end = chunkSpan.Length;
                while (end > start && !IsCoreChar(chunkSpan[end - 1])) end--;

                // Slice the memory without allocating new string objects (Zero-Allocation)
                ReadOnlySpan<char> prefix = chunkSpan[..start];
                ReadOnlySpan<char> coreSpan = chunkSpan[start..end];
                ReadOnlySpan<char> suffix = chunkSpan[end..];

                // StringBuilder natively supports appending Spans directly
                finalPhonemes.Append(prefix);

                if (!coreSpan.IsEmpty)
                {
                    // eSpeak requires a standard string, so we only allocate memory for the clean core word
                    string core = coreSpan.ToString();
                    string rawPhonemes = _espeakWrapper.GetIpaPhonemes(core);

                    if (_fallbackMapper != null)
                    {
                        AppendValidatedPhonemes(rawPhonemes, finalPhonemes);
                    }
                    else
                    {
                        // If the fallback mapper is disabled, append the raw eSpeak output directly
                        finalPhonemes.Append(rawPhonemes);
                    }
                }
                finalPhonemes.Append(suffix);
            }
        }

        return finalPhonemes.ToString();
    }

    /// <summary>
    /// Helper method equivalent to the regex [\p{L}\p{Nd}\p{M}]. 
    /// Identifies letters, digits, and combining marks to define what constitutes the "core" of a word.
    /// </summary>
    private static bool IsCoreChar(char c)
    {
        if (char.IsLetterOrDigit(c)) return true;

        var category = char.GetUnicodeCategory(c);
        return category == UnicodeCategory.NonSpacingMark ||
               category == UnicodeCategory.SpacingCombiningMark ||
               category == UnicodeCategory.EnclosingMark;
    }

    /// <summary>
    /// Validates phonemes against the loaded Piper model's dictionary.
    /// Substitutes unsupported symbols using the fallback mapper.
    /// </summary>
    private void AppendValidatedPhonemes(string phonemes, StringBuilder output)
    {
        if (_fallbackMapper == null)
        {
            output.Append(phonemes);
            return;
        }

        ReadOnlySpan<char> rawSpan = phonemes.AsSpan();
        int index = 0;

        while (index < rawSpan.Length)
        {
            // Extract the length of the current Unicode text element without allocating memory
            int length = StringInfo.GetNextTextElementLength(rawSpan[index..]);
            ReadOnlySpan<char> symbolSpan = rawSpan.Slice(index, length);

            // Dictionaries require a string key for lookups
            string symbol = symbolSpan.ToString();

            if (_piperConfig.PhonemeIdMap.ContainsKey(symbol))
            {
                output.Append(symbolSpan); // Symbol is natively supported
            }
            else
            {
                // Symbol is unknown to the model; fetch the closest acoustic replacement
                string fallback = _fallbackMapper.GetClosestPhoneme(symbol);
                output.Append(!string.IsNullOrEmpty(fallback) ? fallback : symbolSpan);
            }
            index += length;
        }
    }
}