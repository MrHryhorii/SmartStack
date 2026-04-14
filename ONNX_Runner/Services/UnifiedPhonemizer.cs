using System.Text;
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

    /// <summary>
    /// Converts a raw input string into a continuous stream of validated IPA phonemes.
    /// </summary>
    public string GetPhonemes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var finalPhonemes = new StringBuilder();

        // =====================================================================
        // SMART MODE SELECTION
        // =====================================================================
        // If the mixed-language detector is active, it tokenizes the text into language-specific chunks.
        // Otherwise, it treats the entire text as the base model's default language.
        var tokens = _mixedPhonemizer != null
            ? _mixedPhonemizer.ProcessTextToLanguageTokens(text)
            : [new TextChunk { Text = text, DetectedLanguage = _piperConfig.Espeak.Voice ?? "en", IsPunctuationOrSpace = false }];

        // =====================================================================
        // MAIN PROCESSING LOOP
        // =====================================================================
        foreach (var chunk in tokens)
        {
            if (chunk.IsPunctuationOrSpace)
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
                        // =====================================================================
                        // PHONEME ITERATION & FALLBACK VALIDATION (Grapheme Clusters)
                        // =====================================================================
                        // We iterate through the raw IPA output to verify if the loaded Piper model 
                        // actually supports each phonetic symbol.
                        ReadOnlySpan<char> rawSpan = rawPhonemes.AsSpan();
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
                                finalPhonemes.Append(symbolSpan); // Symbol is natively supported
                            }
                            else
                            {
                                // Symbol is unknown to the model; fetch the closest acoustic replacement
                                string fallback = _fallbackMapper.GetClosestPhoneme(symbol);
                                finalPhonemes.Append(!string.IsNullOrEmpty(fallback) ? fallback : symbolSpan);
                            }
                            index += length;
                        }
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
}