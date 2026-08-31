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
        // Armenian
        { "hy", Language.Armenian },
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
        // Chinese (cmn = Mandarin, yue = Cantonese, hak = Hakka are macro-language base codes without hyphens)
        { "zh", Language.Chinese }, { "cmn", Language.Chinese }, { "yue", Language.Chinese }, { "hak", Language.Chinese },
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
        // Greek (grc is Ancient Greek base code)
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
        // Norwegian (nn = Nynorsk, nb = Bokmal, no = Generic/Bokmal - all are distinct base codes)
        { "nn", Language.Nynorsk }, { "nb", Language.Bokmal }, { "no", Language.Bokmal },
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
        // Slovak
        { "sk", Language.Slovak },
        // Slovenian
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
    /// Also caches the mapping to allow reverse lookups later (e.g., mapping Lingua.English back to "en-us").
    /// </summary>
    public Language[] BuildLinguaList(IEnumerable<string> espeakCodes)
    {
        var linguaLangs = new HashSet<Language>();

        foreach (var code in espeakCodes)
        {
            string cleanCode = code.Trim().ToLower();

            // Attempt to find an exact match for base codes like "en", "cmn", or "nb"
            if (EspeakToLinguaBase.TryGetValue(cleanCode, out var linguaLang))
            {
                linguaLangs.Add(linguaLang);

                // Cache the association for reverse mapping (Lingua Enum -> eSpeak string)
                _linguaToEspeak.TryAdd(linguaLang, cleanCode);
            }
            else
            {
                // If no exact match is found, strip the dialect part (everything after the hyphen or underscore)
                string baseFamily = cleanCode.Split('-', '_')[0];
                
                if (EspeakToLinguaBase.TryGetValue(baseFamily, out var fallbackLang))
                {
                    linguaLangs.Add(fallbackLang);
                    
                    // CRITICAL: Cache the original cleanCode (e.g., "en-us"), not the baseFamily.
                    // Lingua will search using the base language enum, but espeak-ng will receive the exact dialect string.
                    _linguaToEspeak.TryAdd(fallbackLang, cleanCode);
                }
                else
                {
                    Console.WriteLine($"[WARNING] EspeakLinguaMapper encountered an unknown code: {cleanCode}");
                }
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