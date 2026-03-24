using System.Text;
using System.Globalization;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

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

    public string GetPhonemes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var finalPhonemes = new StringBuilder();

        // РОЗУМНИЙ ВИБІР РЕЖИМУ
        var tokens = _mixedPhonemizer != null
            ? _mixedPhonemizer.ProcessTextToLanguageTokens(text)
            : [new TextChunk { Text = text, DetectedLanguage = _piperConfig.Espeak.Voice ?? "en", IsPunctuationOrSpace = false }];

        // ГОЛОВНИЙ ЦИКЛ
        foreach (var chunk in tokens)
        {
            if (chunk.IsPunctuationOrSpace)
            {
                finalPhonemes.Append(_punctuationMapper.Normalize(chunk.Text));
            }
            else
            {
                try { _espeakWrapper.SetVoice(chunk.DetectedLanguage); }
                catch { _espeakWrapper.SetVoice(_piperConfig.Espeak.Voice ?? "en"); }

                string normalizedChunk = _punctuationMapper.Normalize(chunk.Text);
                ReadOnlySpan<char> chunkSpan = normalizedChunk.AsSpan();

                // =====================================================================
                // Знаходження префіксу, ядра та суфіксу
                // =====================================================================
                int start = 0;
                while (start < chunkSpan.Length && !IsCoreChar(chunkSpan[start])) start++;

                int end = chunkSpan.Length;
                while (end > start && !IsCoreChar(chunkSpan[end - 1])) end--;

                // Нарізаємо пам'ять без створення нових рядків
                ReadOnlySpan<char> prefix = chunkSpan[..start];
                ReadOnlySpan<char> coreSpan = chunkSpan[start..end];
                ReadOnlySpan<char> suffix = chunkSpan[end..];

                // StringBuilder додає Span
                finalPhonemes.Append(prefix);

                if (!coreSpan.IsEmpty)
                {
                    // Для eSpeak потрібен звичайний string, тому конвертуємо тільки ядро
                    string core = coreSpan.ToString();
                    string rawPhonemes = _espeakWrapper.GetIpaPhonemes(core);

                    if (_fallbackMapper != null)
                    {
                        // =====================================================================
                        // Перебір фонем (Grapheme Clusters)
                        // =====================================================================
                        ReadOnlySpan<char> rawSpan = rawPhonemes.AsSpan();
                        int index = 0;

                        while (index < rawSpan.Length)
                        {
                            // Отримуємо довжину поточного юнікод-символу (фонеми) без виділення пам'яті
                            int length = StringInfo.GetNextTextElementLength(rawSpan[index..]);
                            ReadOnlySpan<char> symbolSpan = rawSpan.Slice(index, length);

                            // Словники вимагають string для пошуку ключа
                            string symbol = symbolSpan.ToString();

                            if (_piperConfig.PhonemeIdMap.ContainsKey(symbol))
                            {
                                finalPhonemes.Append(symbolSpan);
                            }
                            else
                            {
                                string fallback = _fallbackMapper.GetClosestPhoneme(symbol);
                                finalPhonemes.Append(!string.IsNullOrEmpty(fallback) ? fallback : symbolSpan);
                            }
                            index += length;
                        }
                    }
                    else
                    {
                        finalPhonemes.Append(rawPhonemes);
                    }
                }
                finalPhonemes.Append(suffix);
            }
        }

        return finalPhonemes.ToString();
    }

    // Допоміжний метод, який робить те саме, що й [\p{L}\p{Nd}\p{M}] у Regex
    private static bool IsCoreChar(char c)
    {
        if (char.IsLetterOrDigit(c)) return true;

        var category = char.GetUnicodeCategory(c);
        return category == UnicodeCategory.NonSpacingMark ||
               category == UnicodeCategory.SpacingCombiningMark ||
               category == UnicodeCategory.EnclosingMark;
    }
}