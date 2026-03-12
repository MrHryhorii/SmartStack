using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public interface IPhonemizer
{
    // Змінили назву методу, щоб вона відповідала PiperRunner
    long[] PhonemesToIds(string phonemes);
}

public class PiperPhonemizer(PiperConfig config) : IPhonemizer
{
    private readonly PiperConfig _config = config;

    public long[] PhonemesToIds(string phonemes)
    {
        var corePhonemes = new List<long>();

        // Дістаємо службові ID (або беремо стандартні Piper-значення)
        int sentenceStartId = _config.PhonemeIdMap.TryGetValue("^", out var startArr) ? startArr[0] : 1;
        int sentenceEndId = _config.PhonemeIdMap.TryGetValue("$", out var endArr) ? endArr[0] : 2;
        int padId = _config.PhonemeIdMap.TryGetValue("_", out var padArr) ? padArr[0] : 0;
        int spaceId = _config.PhonemeIdMap.TryGetValue(" ", out var spaceArr) ? spaceArr[0] : 3;

        // Початок речення
        corePhonemes.Add(sentenceStartId);
        corePhonemes.Add(spaceId); // Piper любить починати з пробілу

        // Використовуємо StringInfo для безпечного читання складних Unicode символів
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(phonemes);

        while (enumerator.MoveNext())
        {
            string symbol = enumerator.GetTextElement();

            // Якщо модель знає цей символ (фонема, кома, пробіл) - просто додаємо його ID
            if (_config.PhonemeIdMap.TryGetValue(symbol, out var ids))
            {
                corePhonemes.Add(ids[0]);
            }
            // Якщо це невідомий символ переносу рядка, перетворюємо на пробіл
            else if (symbol == "\n" || symbol == "\r")
            {
                if (corePhonemes.Count > 0 && corePhonemes.Last() != spaceId)
                {
                    corePhonemes.Add(spaceId);
                }
            }
        }

        // Завершуємо речення тишею
        if (corePhonemes.Count > 0 && corePhonemes.Last() != spaceId)
        {
            corePhonemes.Add(spaceId);
        }
        corePhonemes.Add(sentenceEndId);

        // --- Ідеальне математичне перемежовування індексом пустоти (Pad) ---
        // Кожна фонема має бути обгорнута padId (зазвичай це 0)
        var finalIds = new List<long> { padId };
        foreach (var id in corePhonemes)
        {
            finalIds.Add(id);
            finalIds.Add(padId);
        }

        var resultArray = finalIds.ToArray();

        // Виводимо в консоль для дебагу, щоб бачити, що реально пішло в нейромережу
        Console.WriteLine($"\n[DEBUG] Input Phonemes: {phonemes}");
        Console.WriteLine($"[DEBUG] Target Tensor:  [{string.Join(", ", resultArray)}]\n");

        return resultArray;
    }
}