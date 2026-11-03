using Lingua;

namespace ONNX_Runner.Services.Text.Phonemization;

/// <summary>
/// Acts as a translation layer between eSpeak-ng language codes and the Lingua language detection library.
/// eSpeak uses standard ISO codes or regional tags (e.g., "en-us", "zh-cn"), whereas Lingua uses strict 
/// strongly-typed enums. This mapper allows the system to seamlessly detect the input text language 
/// and route it back to the correct eSpeak voice variant.
/// </summary>
public class EspeakLinguaMapper
{
    private readonly Dictionary<Language, string> _linguaToEspeak = [];

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
            if (EspeakLanguageCatalog.TryGetLanguage(cleanCode, out var linguaLang))
            {
                linguaLangs.Add(linguaLang);

                // Cache the association for reverse mapping (Lingua Enum -> eSpeak string)
                _linguaToEspeak.TryAdd(linguaLang, cleanCode);
            }
            else
            {
                // If no exact match is found, strip the dialect part (everything after the hyphen or underscore)
                string baseFamily = cleanCode.Split('-', '_')[0];

                if (EspeakLanguageCatalog.TryGetLanguage(baseFamily, out var fallbackLang))
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
        return EspeakLanguageCatalog.TryGetLanguage(espeakCode.Trim().ToLower(), out var lang) ? lang : null;
    }
}
