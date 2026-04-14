using Lingua;

namespace ONNX_Runner.Services;

/// <summary>
/// Acts as a translation layer between eSpeak-ng language codes and the Lingua language detection library.
/// eSpeak uses standard ISO codes or regional tags (e.g., "en-us", "zh-cn"), whereas Lingua uses strict 
/// strongly-typed enums. This mapper allows the system to seamlessly detect the input text language 
/// and route it back to the correct eSpeak voice variant.
/// </summary>
public class EspeakLinguaMapper
{
    private readonly Dictionary<Language, string> _linguaToEspeak = [];

    // Comprehensive mapping of eSpeak language/dialect codes to Lingua macro-languages.
    // Includes 75 languages supported by lingua-dotnet and their common regional variants.
    private static readonly Dictionary<string, Language> EspeakToLinguaBase = new(StringComparer.OrdinalIgnoreCase)
    {
        // Afrikaans
        { "af", Language.Afrikaans },
        // Albanian
        { "sq", Language.Albanian },
        // Arabic
        { "ar", Language.Arabic },
        // Armenian (including Western Armenian fallback)
        { "hy", Language.Armenian }, { "hy-arevmda", Language.Armenian }, { "hyw", Language.Armenian },
        // Azerbaijani
        { "az", Language.Azerbaijani },
        // Basque
        { "eu", Language.Basque },
        // Belarusian
        { "be", Language.Belarusian },
        // Bengali
        { "bn", Language.Bengali },
        // Bosnian
        { "bs", Language.Bosnian },
        // Bulgarian
        { "bg", Language.Bulgarian },
        // Catalan
        { "ca", Language.Catalan },
        // Chinese (including Mandarin, Cantonese, Hakka and regional codes)
        { "zh", Language.Chinese }, { "zh-cn", Language.Chinese }, { "zh-tw", Language.Chinese },
        { "zh-hk", Language.Chinese }, { "cmn", Language.Chinese }, { "yue", Language.Chinese }, { "hak", Language.Chinese },
        // Croatian
        { "hr", Language.Croatian },
        // Czech
        { "cs", Language.Czech },
        // Danish
        { "da", Language.Danish },
        // Dutch (including Flemish/Belgium variant)
        { "nl", Language.Dutch }, { "nl-be", Language.Dutch },
        // English (covering major global accents)
        { "en", Language.English }, { "en-us", Language.English }, { "en-gb", Language.English },
        { "en-au", Language.English }, { "en-ca", Language.English }, { "en-nz", Language.English },
        { "en-ie", Language.English }, { "en-za", Language.English }, { "en-in", Language.English },
        // Esperanto
        { "eo", Language.Esperanto },
        // Estonian
        { "et", Language.Estonian },
        // Finnish
        { "fi", Language.Finnish },
        // French (including Canadian, Belgian, Swiss variants)
        { "fr", Language.French }, { "fr-ca", Language.French }, { "fr-be", Language.French }, { "fr-ch", Language.French },
        // Ganda (Luganda)
        { "lg", Language.Ganda },
        // Georgian
        { "ka", Language.Georgian },
        // German (including Austrian and Swiss variants)
        { "de", Language.German }, { "de-at", Language.German }, { "de-ch", Language.German },
        // Greek (including Ancient Greek fallback)
        { "el", Language.Greek }, { "grc", Language.Greek },
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
        // Italian (including Swiss Italian)
        { "it", Language.Italian }, { "it-ch", Language.Italian },
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
        // Norwegian (Nynorsk & Bokmal + generic "no" fallback to Bokmal)
        { "nn", Language.Nynorsk }, { "nb", Language.Bokmal }, { "no", Language.Bokmal },
        // Persian (Farsi)
        { "fa", Language.Persian },
        // Polish
        { "pl", Language.Polish },
        // Portuguese (including Brazilian variant)
        { "pt", Language.Portuguese }, { "pt-br", Language.Portuguese }, { "pt-pt", Language.Portuguese },
        // Punjabi
        { "pa", Language.Punjabi },
        // Romanian
        { "ro", Language.Romanian },
        // Russian
        { "ru", Language.Russian },
        // Serbian (covering both scripts if defined by eSpeak)
        { "sr", Language.Serbian }, { "sr-cyrl", Language.Serbian }, { "sr-latn", Language.Serbian },
        // Shona
        { "sn", Language.Shona },
        // Slovak
        { "sk", Language.Slovak },
        // Slovenian
        { "sl", Language.Slovene },
        // Somali
        { "so", Language.Somali },
        // Sotho (Sesotho)
        { "st", Language.Sotho },
        // Spanish (covering Latin America, Mexico, Spain variants)
        { "es", Language.Spanish }, { "es-419", Language.Spanish }, { "es-mx", Language.Spanish }, { "es-es", Language.Spanish }, { "es-ar", Language.Spanish },
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

    /// <summary>
    /// Builds a unique collection of Lingua Enum languages based on the provided eSpeak codes.
    /// Also caches the mapping to allow reverse lookups later.
    /// </summary>
    public Language[] BuildLinguaList(IEnumerable<string> espeakCodes)
    {
        var linguaLangs = new HashSet<Language>();

        foreach (var code in espeakCodes)
        {
            string cleanCode = code.Trim().ToLower();

            if (EspeakToLinguaBase.TryGetValue(cleanCode, out var linguaLang))
            {
                linguaLangs.Add(linguaLang);

                // Cache the association for reverse mapping (Lingua Enum -> eSpeak string)
                _linguaToEspeak.TryAdd(linguaLang, cleanCode);
            }
            else
            {
                Console.WriteLine($"[WARNING] EspeakLinguaMapper encountered an unknown code: {cleanCode}");
            }
        }
        return [.. linguaLangs];
    }

    /// <summary>
    /// Converts a detected Lingua language enum back into the corresponding eSpeak code string.
    /// </summary>
    public string MapBackToEspeak(Language lang, string fallback)
    {
        return _linguaToEspeak.TryGetValue(lang, out var espeakCode) ? espeakCode : fallback;
    }

    /// <summary>
    /// Directly maps an eSpeak code string to its corresponding Lingua Enum, if available.
    /// </summary>
    public Language? GetLinguaLanguage(string espeakCode)
    {
        return EspeakToLinguaBase.TryGetValue(espeakCode.Trim().ToLower(), out var lang) ? lang : null;
    }
}