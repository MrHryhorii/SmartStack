using Lingua;

namespace ONNX_Runner.Services.Text.Phonemization;

/// <summary>Shared eSpeak base-code mappings; configured dialect preferences remain instance-owned.</summary>
internal static class EspeakLanguageCatalog
{
    // Comprehensive mapping of eSpeak / BCP 47 base language codes to Lingua languages.
    // Includes all 79 languages supported by SearchPioneer.Lingua 2.x,
    // plus a small set of useful alternative codes that cannot be resolved
    // by simply stripping a regional/dialect suffix.
    private static readonly Dictionary<string, Language> EspeakToLinguaBase =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Afrikaans
        { "af", Language.Afrikaans },
        // Albanian
        { "sq", Language.Albanian },
        // Amharic
        { "am", Language.Amharic },
        // Arabic
        { "ar", Language.Arabic },
        // Armenian
        // hy  = Eastern / generic Armenian
        // hyw = Western Armenian
        { "hy", Language.Armenian },
        { "hyw", Language.Armenian },
        // Azerbaijani
        { "az", Language.Azerbaijani },
        // Basque
        { "eu", Language.Basque },
        // Belarusian
        { "be", Language.Belarusian },
        // Bengali
        { "bn", Language.Bengali },
        // Bokmål (Norwegian)
        // nb = Norwegian Bokmål
        // no = generic Norwegian; treated as Bokmål for compatibility
        { "nb", Language.Bokmal },
        { "no", Language.Bokmal },
        // Bosnian
        { "bs", Language.Bosnian },
        // Bulgarian
        { "bg", Language.Bulgarian },
        // Catalan
        { "ca", Language.Catalan },
        // Chinese
        // Lingua detects only the Chinese macro-language.
        // The configured eSpeak code determines the actual frontend/dialect.
        { "zh", Language.Chinese },
        { "cmn", Language.Chinese }, // Mandarin
        { "yue", Language.Chinese }, // Cantonese
        { "hak", Language.Chinese }, // Hakka
        // Croatian
        { "hr", Language.Croatian },
        // Czech
        { "cs", Language.Czech },
        // Danish
        { "da", Language.Danish },
        // Dutch
        { "nl", Language.Dutch },
        // English
        { "en", Language.English },
        // Esperanto
        { "eo", Language.Esperanto },
        // Estonian
        { "et", Language.Estonian },
        // Finnish
        { "fi", Language.Finnish },
        // French
        { "fr", Language.French },
        // Ganda (Luganda)
        { "lg", Language.Ganda },
        // Georgian
        { "ka", Language.Georgian },
        // German
        { "de", Language.German },
        // Greek
        // el  = Modern Greek
        // grc = Ancient Greek
        { "el", Language.Greek },
        { "grc", Language.Greek },
        // Gujarati
        { "gu", Language.Gujarati },
        // Hebrew
        { "he", Language.Hebrew },
        // Hindi
        { "hi", Language.Hindi },
        // Hungarian
        { "hu", Language.Hungarian },
        // Icelandic
        { "is", Language.Icelandic },
        // Indonesian
        { "id", Language.Indonesian },
        // Irish
        { "ga", Language.Irish },
        // Italian
        { "it", Language.Italian },
        // Japanese
        { "ja", Language.Japanese },
        // Kazakh
        { "kk", Language.Kazakh },
        // Korean
        { "ko", Language.Korean },
        // Latin
        { "la", Language.Latin },
        // Latvian
        { "lv", Language.Latvian },
        // Lithuanian
        { "lt", Language.Lithuanian },
        // Macedonian
        { "mk", Language.Macedonian },
        // Malay
        { "ms", Language.Malay },
        // Maori
        { "mi", Language.Maori },
        // Marathi
        { "mr", Language.Marathi },
        // Mongolian
        { "mn", Language.Mongolian },
        // Nynorsk (Norwegian)
        { "nn", Language.Nynorsk },
        // Oromo
        { "om", Language.Oromo },
        // Persian (Farsi)
        { "fa", Language.Persian },
        // Polish
        { "pl", Language.Polish },
        // Portuguese
        { "pt", Language.Portuguese },
        // Punjabi
        { "pa", Language.Punjabi },
        // Romanian
        { "ro", Language.Romanian },
        // Russian
        { "ru", Language.Russian },
        // Serbian
        { "sr", Language.Serbian },
        // Shona
        { "sn", Language.Shona },
        // Sinhala
        { "si", Language.Sinhala },
        // Slovak
        { "sk", Language.Slovak },
        // Slovene
        { "sl", Language.Slovene },
        // Somali
        { "so", Language.Somali },
        // Sotho (Sesotho)
        { "st", Language.Sotho },
        // Spanish
        { "es", Language.Spanish },
        // Swahili
        { "sw", Language.Swahili },
        // Swedish
        { "sv", Language.Swedish },
        // Tagalog
        { "tl", Language.Tagalog },
        // Tamil
        { "ta", Language.Tamil },
        // Telugu
        { "te", Language.Telugu },
        // Thai
        { "th", Language.Thai },
        // Tigrinya
        { "ti", Language.Tigrinya },
        // Tsonga
        { "ts", Language.Tsonga },
        // Tswana
        { "tn", Language.Tswana },
        // Turkish
        { "tr", Language.Turkish },
        // Ukrainian
        { "uk", Language.Ukrainian },
        // Urdu
        { "ur", Language.Urdu },
        // Vietnamese
        { "vi", Language.Vietnamese },
        // Welsh
        { "cy", Language.Welsh },
        // Xhosa
        { "xh", Language.Xhosa },
        // Yoruba
        { "yo", Language.Yoruba },
        // Zulu
        { "zu", Language.Zulu }
    };

    /// <summary>Looks up the shared mapping without exposing its backing dictionary.</summary>
    internal static bool TryGetLanguage(string code, out Language language) =>
        EspeakToLinguaBase.TryGetValue(code, out language);
}
