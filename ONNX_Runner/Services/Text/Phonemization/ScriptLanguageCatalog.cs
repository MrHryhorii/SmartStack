using System.Buffers;
using ScriptType = ONNX_Runner.Services.Text.Phonemization.MixedLanguagePhonemizer.ScriptType;

namespace ONNX_Runner.Services.Text.Phonemization;

/// <summary>Shared script-language fallback and distinctive-letter data, independent of detection state.</summary>
internal static class ScriptLanguageCatalog
{
    // Ultimate script-level emergency fallback for when both Lingua and strong script-language hints
    // find ZERO signal (e.g. Cyrillic text on a server with only Latin models loaded). 
    // Direct eSpeak codes, not Lingua enums, so this works even if the fallback language 
    // isn't in SupportedLanguages. None/Other stay unmapped.
    private static readonly Dictionary<ScriptType, string> ScriptFallbackLanguage = new()
    {
        { ScriptType.Latin, "en" },
        { ScriptType.Cyrillic, "uk" },
        { ScriptType.Greek, "el" },
        { ScriptType.Han, "cmn" },
        { ScriptType.Hiragana, "ja" },
        { ScriptType.Katakana, "ja" },
        { ScriptType.Hangul, "ko" },
        { ScriptType.Arabic, "ar" },
        { ScriptType.Hebrew, "he" },
        { ScriptType.Armenian, "hy" },
        { ScriptType.Bengali, "bn" },
        { ScriptType.Devanagari, "hi" },
        { ScriptType.Georgian, "ka" },
        { ScriptType.Gujarati, "gu" },
        { ScriptType.Gurmukhi, "pa" },
        { ScriptType.Odia, "or" },
        { ScriptType.Tamil, "ta" },
        { ScriptType.Telugu, "te" },
        { ScriptType.Kannada, "kn" },
        { ScriptType.Malayalam, "ml" },
        { ScriptType.Sinhala, "si" },
        { ScriptType.Thai, "th" },
        { ScriptType.Myanmar, "my" },
    };

    // Strong script-language hints used when Lingua is absent or produced no usable signal.
    // Each subphrase has already been classified by Unicode script, so this table performs one
    // cheap IndexOfAny over a small script-specific set. The markers are deliberately conservative:
    // they need not be mathematically unique, but must strongly narrow the most likely language.
    // If no marker is present, ScriptFallbackLanguage provides the final coarse degradation.
    //
    // Turkish: dotless "ı" uppercases to plain "I", which would misfire on virtually every
    // capitalized Latin word — deliberately NOT mapped by itself. Its dotted capital "İ" (U+0130)
    // and other strong Turkish letters remain useful hints. Arabic-script letters have no case.
    private static readonly Dictionary<ScriptType, (SearchValues<char> Chars, Dictionary<char, string> Map)> StrongLanguageHintsByScript = new()
    {
        [ScriptType.Cyrillic] = (
            SearchValues.Create("їЇєЄґҐ" + "ўЎ" + "эЭ" + "ѝЍ" + "әӘғҒқҚңҢұҰһҺ" + "ѓЃѕЅќЌ" + "ђЂћЋ" + "јЈљЉњЊџЏ"),
            new Dictionary<char, string>
            {
                ['ї'] = "uk",
                ['Ї'] = "uk",
                ['є'] = "uk",
                ['Є'] = "uk",
                ['ґ'] = "uk",
                ['Ґ'] = "uk",
                ['ў'] = "be",
                ['Ў'] = "be",
                // Russian-specific strong signal. Hard sign (ъ) is deliberately excluded
                // because it is also a very common Bulgarian vowel letter.
                ['э'] = "ru",
                ['Э'] = "ru",
                // Bulgarian-specific I with grave.
                ['ѝ'] = "bg",
                ['Ѝ'] = "bg",
                // Strong Kazakh letters among the languages supported by the detector.
                // ө/ү are deliberately excluded because Mongolian shares them.
                ['ә'] = "kk",
                ['Ә'] = "kk",
                ['ғ'] = "kk",
                ['Ғ'] = "kk",
                ['қ'] = "kk",
                ['Қ'] = "kk",
                ['ң'] = "kk",
                ['Ң'] = "kk",
                ['ұ'] = "kk",
                ['Ұ'] = "kk",
                ['һ'] = "kk",
                ['Һ'] = "kk",
                // Exclusive to Macedonian (not in Serbian):
                ['ѓ'] = "mk",
                ['Ѓ'] = "mk",
                ['ѕ'] = "mk",
                ['Ѕ'] = "mk",
                ['ќ'] = "mk",
                ['Ќ'] = "mk",
                // Exclusive to Serbian (not in Macedonian):
                ['ђ'] = "sr",
                ['Ђ'] = "sr",
                ['ћ'] = "sr",
                ['Ћ'] = "sr",
                // Shared by Serbian AND Macedonian (not in ru/uk/be) — no single-language
                // signal, so default to "sr" (larger population) absent one of the exclusive
                // letters above also being present in the same word.
                ['ј'] = "sr",
                ['Ј'] = "sr",
                ['љ'] = "sr",
                ['Љ'] = "sr",
                ['њ'] = "sr",
                ['Њ'] = "sr",
                ['џ'] = "sr",
                ['Џ'] = "sr",
            }
        ),
        [ScriptType.Latin] = (
            SearchValues.Create(
                "øØåÅ" +
                "ßẞ" +
                "łŁąĄęĘśŚźŹżŻ" +
                "řŘěĚůŮ" +
                "ľĽĺĹŕŔ" +
                "ģĢķĶļĻņŅ" +
                "ėĖįĮųŲ" +
                "őŐűŰ" +
                "șȘțȚ" +
                "ơƠưƯ" +
                "ĉĈĝĜĥĤĵĴŝŜŭŬ" +
                "ŵŴŷŶ" +
                "ığİĞ" +
                "þÞðÐ" +
                "œŒ" +
                "ħĦ"),
            new Dictionary<char, string>
            {
                // Norwegian/Danish overlap; keep the existing Bokmål-biased degradation.
                ['ø'] = "nb",
                ['Ø'] = "nb",
                ['å'] = "nb",
                ['Å'] = "nb",
                ['ß'] = "de",
                ['ẞ'] = "de",

                ['ł'] = "pl",
                ['Ł'] = "pl",
                ['ą'] = "pl",
                ['Ą'] = "pl",
                ['ę'] = "pl",
                ['Ę'] = "pl",
                ['ś'] = "pl",
                ['Ś'] = "pl",
                ['ź'] = "pl",
                ['Ź'] = "pl",
                ['ż'] = "pl",
                ['Ż'] = "pl",

                ['ř'] = "cs",
                ['Ř'] = "cs",
                ['ě'] = "cs",
                ['Ě'] = "cs",
                ['ů'] = "cs",
                ['Ů'] = "cs",

                ['ľ'] = "sk",
                ['Ľ'] = "sk",
                ['ĺ'] = "sk",
                ['Ĺ'] = "sk",
                ['ŕ'] = "sk",
                ['Ŕ'] = "sk",

                ['ģ'] = "lv",
                ['Ģ'] = "lv",
                ['ķ'] = "lv",
                ['Ķ'] = "lv",
                ['ļ'] = "lv",
                ['Ļ'] = "lv",
                ['ņ'] = "lv",
                ['Ņ'] = "lv",

                ['ė'] = "lt",
                ['Ė'] = "lt",
                ['į'] = "lt",
                ['Į'] = "lt",
                ['ų'] = "lt",
                ['Ų'] = "lt",

                ['ő'] = "hu",
                ['Ő'] = "hu",
                ['ű'] = "hu",
                ['Ű'] = "hu",
                ['ș'] = "ro",
                ['Ș'] = "ro",
                ['ț'] = "ro",
                ['Ț'] = "ro",
                ['ơ'] = "vi",
                ['Ơ'] = "vi",
                ['ư'] = "vi",
                ['Ư'] = "vi",

                ['ĉ'] = "eo",
                ['Ĉ'] = "eo",
                ['ĝ'] = "eo",
                ['Ĝ'] = "eo",
                ['ĥ'] = "eo",
                ['Ĥ'] = "eo",
                ['ĵ'] = "eo",
                ['Ĵ'] = "eo",
                ['ŝ'] = "eo",
                ['Ŝ'] = "eo",
                ['ŭ'] = "eo",
                ['Ŭ'] = "eo",

                ['ŵ'] = "cy",
                ['Ŵ'] = "cy",
                ['ŷ'] = "cy",
                ['Ŷ'] = "cy",

                ['ı'] = "tr",
                ['İ'] = "tr",
                ['ğ'] = "tr",
                ['Ğ'] = "tr",
                ['þ'] = "is",
                ['Þ'] = "is",
                ['ð'] = "is",
                ['Ð'] = "is",
                ['œ'] = "fr",
                ['Œ'] = "fr",

                // Maltese is not a Lingua language in the current mapper, but eSpeak supports it.
                ['ħ'] = "mt",
                ['Ħ'] = "mt",
            }
        ),
        [ScriptType.Arabic] = (
            // پ/چ/گ are shared by Persian and Urdu, so they are intentionally not used as
            // single-character language decisions. Keep only substantially stronger hints.
            SearchValues.Create("ژۀٹڈڑںھہےۂۓ"),
            new Dictionary<char, string>
            {
                ['ژ'] = "fa",
                ['ۀ'] = "fa",

                ['ٹ'] = "ur",
                ['ڈ'] = "ur",
                ['ڑ'] = "ur",
                ['ں'] = "ur",
                ['ھ'] = "ur",
                ['ہ'] = "ur",
                ['ے'] = "ur",
                ['ۂ'] = "ur",
                ['ۓ'] = "ur",
            }
        ),
        [ScriptType.Devanagari] = (
            // Marathi strongly favors retroflex lateral /ळ/ and /ऱ/ compared with Hindi.
            SearchValues.Create("ळऱ"),
            new Dictionary<char, string>
            {
                ['ळ'] = "mr",
                ['ऱ'] = "mr",
            }
        ),
        [ScriptType.Bengali] = (
            // Assamese shares the Bengali script, but these two letters are strong Assamese signals.
            // eSpeak supports Assamese directly even though Lingua does not participate in this tier.
            SearchValues.Create("ৰৱ"),
            new Dictionary<char, string>
            {
                ['ৰ'] = "as",
                ['ৱ'] = "as",
            }
        ),
    };

    /// <summary>Returns the shared search index and character map without copying their contents.</summary>
    internal static bool TryGetHints(ScriptType script,
        out (SearchValues<char> Chars, Dictionary<char, string> Map) hints) =>
        StrongLanguageHintsByScript.TryGetValue(script, out hints);

    /// <summary>Returns the coarse fallback, leaving unmapped scripts without a language.</summary>
    internal static string? GetFallbackLanguage(ScriptType script) =>
        ScriptFallbackLanguage.GetValueOrDefault(script);
}
