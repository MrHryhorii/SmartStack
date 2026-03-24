using ONNX_Runner.Models;
using System.Globalization;

namespace ONNX_Runner.Services;

public interface IPhonemizer
{
    long[] PhonemesToIds(string phonemes);
}

public class PiperPhonemizer(PiperConfig config) : IPhonemizer
{
    private readonly PiperConfig _config = config;

    public long[] PhonemesToIds(string phonemes)
    {
        // Початкова місткість 128 покриє більшість речень без реаллокацій
        var corePhonemes = new List<long>(128);

        // Дістаємо службові ID (або беремо стандартні Piper-значення)
        int sentenceStartId = _config.PhonemeIdMap.TryGetValue("^", out var startArr) ? startArr[0] : 1;
        int sentenceEndId = _config.PhonemeIdMap.TryGetValue("$", out var endArr) ? endArr[0] : 2;
        int padId = _config.PhonemeIdMap.TryGetValue("_", out var padArr) ? padArr[0] : 0;
        int spaceId = _config.PhonemeIdMap.TryGetValue(" ", out var spaceArr) ? spaceArr[0] : 3;

        // Початок речення
        corePhonemes.Add(sentenceStartId);
        corePhonemes.Add(spaceId); // Piper любить починати з пробілу

        // =====================================================================
        // Читання Юнікоду через Span
        // =====================================================================
        ReadOnlySpan<char> phonemeSpan = phonemes.AsSpan();
        int index = 0;

        while (index < phonemeSpan.Length)
        {
            // Беремо довжину поточного юнікод-символу/фонеми (без виділення пам'яті)
            int length = StringInfo.GetNextTextElementLength(phonemeSpan[index..]);
            ReadOnlySpan<char> symbolSpan = phonemeSpan.Slice(index, length);

            // Якщо це невідомий символ переносу рядка, перетворюємо на пробіл
            if (symbolSpan.Length == 1 && (symbolSpan[0] == '\n' || symbolSpan[0] == '\r'))
            {
                if (corePhonemes.Count > 0 && corePhonemes[^1] != spaceId)
                {
                    corePhonemes.Add(spaceId);
                }
            }
            else
            {
                // Словник очікує рядок
                string symbol = symbolSpan.ToString();

                // Якщо модель знає цей символ (фонема, кома, пробіл) - просто додаємо його ID
                if (_config.PhonemeIdMap.TryGetValue(symbol, out var ids))
                {
                    corePhonemes.Add(ids[0]);
                }
            }

            index += length;
        }

        // Завершуємо речення тишею
        if (corePhonemes.Count > 0 && corePhonemes[^1] != spaceId)
        {
            corePhonemes.Add(spaceId);
        }
        corePhonemes.Add(sentenceEndId);

        // =====================================================================
        // Zero-Allocation перемежовування (Interspersing)
        // =====================================================================
        // Знаємо точний розмір фінального тензора: (кількість ID) + (кількість паддингів між ними)
        int finalCount = corePhonemes.Count * 2 - 1;
        long[] resultArray = new long[finalCount];

        int resultIndex = 0;
        for (int i = 0; i < corePhonemes.Count; i++)
        {
            resultArray[resultIndex++] = corePhonemes[i];

            // Додаємо padId (пустоту) ТІЛЬКИ між елементами
            if (i < corePhonemes.Count - 1)
            {
                resultArray[resultIndex++] = padId;
            }
        }

        // Виводимо в консоль для дебагу, щоб бачити, що реально пішло в нейромережу
        Console.WriteLine($"\n[DEBUG] Input Phonemes: {phonemes}");
        Console.WriteLine($"[DEBUG] Target Tensor Size: {resultArray.Length}\n");

        return resultArray;
    }
}