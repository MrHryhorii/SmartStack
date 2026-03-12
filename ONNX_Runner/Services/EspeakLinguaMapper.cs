using Lingua;

namespace ONNX_Runner.Services;

public class EspeakLinguaMapper
{
    private readonly Dictionary<Language, string> _linguaToEspeak = new();

    private static readonly Dictionary<string, Language> EspeakToLinguaBase = new(StringComparer.OrdinalIgnoreCase)
    {
        {"en", Language.English}, {"en-us", Language.English}, {"en-gb", Language.English},
        {"uk", Language.Ukrainian},
        {"ru", Language.Russian},
        {"nb", Language.Bokmal},
        {"nn", Language.Nynorsk},
        {"no", Language.Bokmal},
        {"de", Language.German},
        {"pl", Language.Polish},
        {"es", Language.Spanish},
        {"fr", Language.French},
        {"it", Language.Italian},
        {"ja", Language.Japanese},
        {"zh", Language.Chinese},
        {"cmn", Language.Chinese},   // Mandarin
        {"yue", Language.Chinese},   // Cantonese
        {"da", Language.Danish},
        {"sv", Language.Swedish},
        {"fi", Language.Finnish},
        {"mi", Language.Maori},
        {"be", Language.Belarusian}
    };

    public Language[] BuildLinguaList(IEnumerable<string> espeakCodes)
    {
        var linguaLangs = new HashSet<Language>();

        foreach (var code in espeakCodes)
        {
            string cleanCode = code.Trim().ToLower();

            if (EspeakToLinguaBase.TryGetValue(cleanCode, out var linguaLang))
            {
                linguaLangs.Add(linguaLang);
                // Запам'ятовуємо зв'язок для зворотного мапінгу
                _linguaToEspeak.TryAdd(linguaLang, cleanCode);
            }
            else
            {
                Console.WriteLine($"[WARNING] EspeakLinguaMapper не знає коду: {cleanCode}");
            }
        }
        return linguaLangs.ToArray();
    }

    public string MapBackToEspeak(Language lang, string fallback)
    {
        return _linguaToEspeak.TryGetValue(lang, out var espeakCode) ? espeakCode : fallback;
    }

    public Language? GetLinguaLanguage(string espeakCode)
    {
        return EspeakToLinguaBase.TryGetValue(espeakCode.Trim().ToLower(), out var lang) ? lang : null;
    }
}