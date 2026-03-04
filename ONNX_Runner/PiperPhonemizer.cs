using ONNX_Runner.Models;
using ONNX_Runner.Services;
using System.Text.RegularExpressions;

namespace ONNX_Runner;

public interface IPhonemizer
{
    long[] TextToPhonemeIds(string text);
}

public partial class PiperPhonemizer : IPhonemizer, IDisposable
{
    private readonly PiperConfig _config;
    private readonly EspeakWrapper _espeakWrapper;

    public PiperPhonemizer(PiperConfig config)
    {
        _config = config;
        string espeakVoice = _config.Espeak.Voice ?? "en";

        // Важливо: espeak очікує шлях до ПАПКИ, в якій лежить espeak-ng-data.
        // Оскільки espeak-ng-data лежить всередині PiperNative, ми передаємо шлях до PiperNative.
        string dataPath = Path.GetFullPath("PiperNative");

        _espeakWrapper = new EspeakWrapper(dataPath, espeakVoice);
    }

    public long[] TextToPhonemeIds(string text)
    {
        text = text.Trim();
        var corePhonemes = new List<long>();

        int sentenceStartId = _config.PhonemeIdMap.TryGetValue("^", out var startArr) ? startArr[0] : 1;
        int sentenceEndId = _config.PhonemeIdMap.TryGetValue("$", out var endArr) ? endArr[0] : 2;
        int padId = _config.PhonemeIdMap.TryGetValue("_", out var padArr) ? padArr[0] : 0;
        int spaceId = _config.PhonemeIdMap.TryGetValue(" ", out var spaceArr) ? spaceArr[0] : 3;

        corePhonemes.Add(sentenceStartId);
        corePhonemes.Add(spaceId);

        string[] tokens = MyRegex().Split(text);

        Console.WriteLine($"\n[DEBUG] Original Text: {text}");

        // Рядок для чистого логування
        var debugIpa = new System.Text.StringBuilder();

        foreach (string token in tokens)
        {
            string cleanToken = token.Trim();
            if (string.IsNullOrEmpty(cleanToken)) continue;

            if (cleanToken.Length == 1 && ".,!?;:-()".Contains(cleanToken))
            {
                string symbol = cleanToken;
                if (_config.PhonemeIdMap.TryGetValue(symbol, out var ids))
                {
                    corePhonemes.Add(ids[0]);

                    // Для красивого логу: забираємо пробіл перед знаком, якщо він там є
                    if (debugIpa.Length > 0 && debugIpa[^1] == ' ')
                    {
                        debugIpa.Length--;
                    }

                    // Друкуємо знак і звичайний пробіл ПІСЛЯ нього
                    debugIpa.Append(symbol).Append(' ');

                    if (symbol == "," || symbol == "-" || symbol == ":" || symbol == ";")
                    {
                        corePhonemes.Add(spaceId);
                    }
                    else if (symbol == "." || symbol == "!" || symbol == "?")
                    {
                        corePhonemes.Add(spaceId);
                    }
                }
            }
            else
            {
                string ipaText = _espeakWrapper.GetIpaPhonemes(cleanToken);

                // Додаємо чисті фонеми слова і звичайний пробіл після нього
                debugIpa.Append(ipaText).Append(' ');

                foreach (char ipaChar in ipaText)
                {
                    string symbol = ipaChar.ToString();

                    if (_config.PhonemeIdMap.TryGetValue(symbol, out var ids))
                    {
                        corePhonemes.Add(ids[0]);
                    }
                    else if (symbol == " " || symbol == "\n")
                    {
                        corePhonemes.Add(spaceId);
                    }
                }
            }
        }

        // --- ФІНАЛЬНА ПЕРЕВІРКА ТИШІ ---
        if (corePhonemes.Count > 0 && corePhonemes.Last() != spaceId)
        {
            corePhonemes.Add(spaceId);
        }

        corePhonemes.Add(sentenceEndId);

        // Виводимо наш чистий лог, обрізавши можливий зайвий пробіл в самому кінці
        Console.WriteLine($"[DEBUG] Exact Rhythm:  {debugIpa.ToString().Trim()}");

        // Перемежовування нулями (Pad)
        var finalIds = new List<long>
        {
            padId
        };

        foreach (var id in corePhonemes)
        {
            finalIds.Add(id);
            finalIds.Add(padId);
        }

        var resultArray = finalIds.ToArray();
        Console.WriteLine($"[DEBUG] Phoneme IDs:   [{string.Join(", ", resultArray)}]\n");

        return resultArray;
    }

    public void Dispose()
    {
        _espeakWrapper?.Dispose();

        // Кажемо Garbage Collector не викликати фіналізатор
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"([.,!?;:\(\)\-])")]
    private static partial Regex MyRegex();
}