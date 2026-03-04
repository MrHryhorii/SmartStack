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
        var debugIpa = new System.Text.StringBuilder();

        foreach (string token in tokens)
        {
            string cleanToken = token.Trim();
            if (string.IsNullOrEmpty(cleanToken)) continue;

            // Перевіряємо, чи це розділовий знак (включаючи іспанські та азіатські)
            if (cleanToken.Length == 1 && ".,!?;:-()¡¿。！？、，".Contains(cleanToken))
            {
                char symbolChar = cleanToken[0];
                string symbolStr = cleanToken;

                // Перевіряємо, чи підтримує ПОТОЧНА МОДЕЛЬ цей знак
                if (_config.PhonemeIdMap.TryGetValue(symbolStr, out var ids))
                {
                    corePhonemes.Add(ids[0]); // Додаємо ID знаку з конфігу

                    // ЛОГІКА ВІЗУАЛІЗАЦІЇ ТА ПАУЗ
                    // Якщо це "відкриваючий" знак (¡ ¿ або дужка)
                    if ("¡¿(".Contains(symbolChar))
                    {
                        // Знак прилипає до НАСТУПНОГО слова, паузу після нього НЕ ставимо
                        debugIpa.Append(symbolChar);
                    }
                    // Якщо це "закриваючий" знак (крапка, кома тощо)
                    else
                    {
                        // Забираємо пробіл ПЕРЕД знаком у лозі
                        if (debugIpa.Length > 0 && debugIpa[^1] == ' ')
                        {
                            debugIpa.Length--;
                        }

                        debugIpa.Append(symbolChar).Append(' ');

                        // Додаємо реальну паузу в нейромережу ПІСЛЯ знаку
                        corePhonemes.Add(spaceId);
                    }
                }
            }
            else
            {
                string ipaText = _espeakWrapper.GetIpaPhonemes(cleanToken);

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

        Console.WriteLine($"[DEBUG] Exact Rhythm:  {debugIpa.ToString().Trim()}");

        // Ідеальне математичне перемежовування індексом пустоти (Pad)
        var finalIds = new List<long> { padId };
        foreach (var id in corePhonemes)
        {
            finalIds.Add(id);
            finalIds.Add(padId);
        }

        var resultArray = finalIds.ToArray();
        Console.WriteLine($"[DEBUG] Phoneme IDs:   [{string.Join(", ", resultArray)}]\n");

        return resultArray;
    }

    // Оновлений Regex, який розпізнає іспанські та азіатські знаки
    [GeneratedRegex(@"([.,!?;:\(\)\-¡¿。！？、，])")]
    private static partial Regex MyRegex();

    public void Dispose()
    {
        _espeakWrapper?.Dispose();

        // Кажемо Garbage Collector не викликати фіналізатор
        GC.SuppressFinalize(this);
    }
}