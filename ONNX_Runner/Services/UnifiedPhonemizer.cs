using System.Text;
using System.Text.RegularExpressions;
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
        // Якщо детектор є — ріжемо на мови. Якщо немає — створюємо один "штучний" чанк на весь текст.
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
                var match = BoundaryRegex().Match(normalizedChunk);

                string prefix = match.Groups[1].Value;
                string core = match.Groups[2].Value;
                string suffix = match.Groups[3].Value;

                finalPhonemes.Append(prefix);

                if (!string.IsNullOrEmpty(core))
                {
                    string rawPhonemes = _espeakWrapper.GetIpaPhonemes(core);

                    if (_fallbackMapper != null)
                    {
                        var safePhonemes = new StringBuilder();
                        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(rawPhonemes);

                        while (enumerator.MoveNext())
                        {
                            string symbol = enumerator.GetTextElement();

                            if (_piperConfig.PhonemeIdMap.ContainsKey(symbol)) safePhonemes.Append(symbol);
                            else
                            {
                                string fallback = _fallbackMapper.GetClosestPhoneme(symbol);
                                safePhonemes.Append(!string.IsNullOrEmpty(fallback) ? fallback : symbol);
                            }
                        }
                        finalPhonemes.Append(safePhonemes);
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

    // Перенесли регулярку сюди, де їй і місце
    [GeneratedRegex(@"^([^\p{L}\p{Nd}\p{M}]*)(.*?)([^\p{L}\p{Nd}\p{M}]*)$", RegexOptions.Singleline)]
    private static partial Regex BoundaryRegex();
}