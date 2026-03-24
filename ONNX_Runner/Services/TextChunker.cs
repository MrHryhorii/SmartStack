using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public class TextChunker(ChunkerSettings settings)
{
    private static readonly System.Buffers.SearchValues<char> s_sentenceTerminators = System.Buffers.SearchValues.Create(".!?\n。！？؟۔։;");
    private readonly int _maxLength = settings.MaxChunkLength > 50 ? settings.MaxChunkLength : 250;
    private const string EmergencyGlue = "_";

    private static readonly char[] SentenceTerminators = ['.', '!', '?', '\n', '。', '！', '？', '؟', '۔', '։', ';'];
    private static readonly char[] PauseMarks = [',', ';', ':', '-', '—', '，', '、', '；', '：'];

    // Містить найпопулярніші звернення багатьох європейських мов.
    private static readonly HashSet<string> CommonTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "rev", "rep", "sen", "gov",
        // Norwegian / Danish / Swedish
        "hr", "fr", "fru", "kapt",
        // German
        "herr", "frau", "ing",
        // Spanish / Portuguese
        "srta", "sra", "don", "doña",
        // French
        "mme", "mlle", "mgr"
    };

    public List<string> Split(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        // ВКАЗІВНИК НА ПАМ'ЯТЬ БЕЗ КОПІЮВАННЯ
        ReadOnlySpan<char> textSpan = text.AsSpan();
        int currentIndex = 0;

        while (currentIndex < textSpan.Length)
        {
            int nextTerminator = currentIndex;
            bool foundValidTerminator = false;

            while (nextTerminator < textSpan.Length)
            {
                // Пошук наступного розділового знаку у залишку тексту
                int offset = textSpan[nextTerminator..].IndexOfAny(s_sentenceTerminators);
                if (offset == -1)
                {
                    nextTerminator = -1;
                    break;
                }

                nextTerminator += offset;

                if (nextTerminator + 1 >= textSpan.Length)
                {
                    foundValidTerminator = true;
                    break;
                }

                char currentTerminator = textSpan[nextTerminator];

                // ====================================================================
                // КЛЮЧОВИЙ ЗАХИСТ ДЛЯ АЗІАТСЬКИХ МОВ ТА УНІВЕРСАЛЬНОСТІ
                // Якщо це не звичайна крапка (а наприклад '。', '！', '?', '\n', '؟') - 
                // це 100% кінець речення. В азіатських мовах немає пробілів після '。', 
                // і не буває абревіатур з такими знаками.
                // ====================================================================
                if (currentTerminator != '.')
                {
                    foundValidTerminator = true;
                    break;
                }

                // Якщо це звичайна крапка ('.'), застосовуємо смарт-логіку
                char nextChar = textSpan[nextTerminator + 1];

                // Крапка вимагає після себе пробілу або іншої пунктуації (напр. ... або .")
                if (char.IsWhiteSpace(nextChar) || SentenceTerminators.Contains(nextChar) || PauseMarks.Contains(nextChar))
                {
                    int nextVisibleCharIdx = nextTerminator + 1;
                    while (nextVisibleCharIdx < textSpan.Length && char.IsWhiteSpace(textSpan[nextVisibleCharIdx]))
                    {
                        nextVisibleCharIdx++;
                    }
                    bool isNextLower = nextVisibleCharIdx < textSpan.Length && char.IsLower(textSpan[nextVisibleCharIdx]);

                    int wordStart = nextTerminator - 1;
                    while (wordStart >= 0 && !char.IsWhiteSpace(textSpan[wordStart]))
                    {
                        wordStart--;
                    }
                    wordStart++;

                    ReadOnlySpan<char> wordBeforeDot = textSpan[wordStart..nextTerminator];
                    bool isAbbreviation = false;

                    // Ініціали (одна літера)
                    if (wordBeforeDot.Length == 1 && char.IsLetter(wordBeforeDot[0]))
                    {
                        isAbbreviation = true;
                    }
                    // Наступне слово з малої літери
                    else if (isNextLower)
                    {
                        isAbbreviation = true;
                    }
                    // Абревіатури vs Веб-адреси
                    else if (wordBeforeDot.IndexOf('.') != -1)
                    {
                        int maxSegmentLength = 0;
                        int currentSegmentLength = 0;

                        for (int i = 0; i < wordBeforeDot.Length; i++)
                        {
                            if (wordBeforeDot[i] == '.')
                            {
                                if (currentSegmentLength > maxSegmentLength) maxSegmentLength = currentSegmentLength;
                                currentSegmentLength = 0;
                            }
                            else
                            {
                                currentSegmentLength++;
                            }
                        }
                        if (currentSegmentLength > maxSegmentLength) maxSegmentLength = currentSegmentLength;

                        // Абревіатури мають короткі сегменти (<= 3), веб-адреси - довгі
                        if (maxSegmentLength <= 3)
                        {
                            isAbbreviation = true;
                        }
                    }
                    // Міжнародні титули
                    else
                    {
                        if (CommonTitles.Contains(wordBeforeDot.ToString()))
                        {
                            isAbbreviation = true;
                        }
                    }

                    if (!isAbbreviation)
                    {
                        foundValidTerminator = true;
                        break;
                    }
                }

                nextTerminator++; // Це була абревіатура, шукаємо далі
            }

            int endIndex;
            if (!foundValidTerminator || nextTerminator == -1)
            {
                endIndex = textSpan.Length;
            }
            else
            {
                endIndex = nextTerminator + 1;
                while (endIndex < textSpan.Length && SentenceTerminators.Contains(textSpan[endIndex])) endIndex++;
            }

            // Нарізаємо і конвертуємо у фінальний рядок
            string sentence = textSpan[currentIndex..endIndex].Trim().ToString();

            if (!string.IsNullOrWhiteSpace(sentence))
            {
                if (sentence.Length <= _maxLength) result.Add(sentence);
                else result.AddRange(SplitLongSentence(sentence));
            }

            currentIndex = endIndex;
        }

        return result;
    }

    private List<string> SplitLongSentence(string sentence)
    {
        var result = new List<string>();
        int currentIndex = 0;

        // Використовуємо Span для аварійної нарізки довгого речення
        ReadOnlySpan<char> sentenceSpan = sentence.AsSpan();

        while (currentIndex < sentenceSpan.Length)
        {
            int remainingLength = sentenceSpan.Length - currentIndex;
            if (remainingLength <= _maxLength)
            {
                result.Add(sentenceSpan[currentIndex..].Trim().ToString());
                break;
            }

            int windowEnd = currentIndex + _maxLength;

            // Використовуємо метод пошуку через Span
            int splitIndex = FindLastOccurrence(sentenceSpan, currentIndex, windowEnd, PauseMarks);

            if (splitIndex == -1)
            {
                // Ручний пошук останнього пробілу у вікні
                for (int i = windowEnd - 1; i >= currentIndex; i--)
                {
                    if (char.IsWhiteSpace(sentenceSpan[i]))
                    {
                        splitIndex = i;
                        break;
                    }
                }
            }

            if (splitIndex == -1 || splitIndex < currentIndex)
            {
                splitIndex = windowEnd;
            }
            else
            {
                splitIndex++; // Включаємо знайдений символ пунктуації/пробіл у результат
            }

            // Нарізаємо чанк через Span без створення зайвих рядків
            ReadOnlySpan<char> chunkSpan = sentenceSpan[currentIndex..splitIndex].Trim();

            if (!chunkSpan.IsEmpty)
            {
                char lastChar = chunkSpan[^1];
                string finalChunk = chunkSpan.ToString();

                if (!char.IsPunctuation(lastChar))
                {
                    finalChunk += EmergencyGlue;
                }

                result.Add(finalChunk);
            }

            currentIndex = splitIndex;
        }

        return result;
    }

    // Метод пошуку, який працює з ReadOnlySpan
    private static int FindLastOccurrence(ReadOnlySpan<char> text, int startIndex, int endIndex, char[] charsToFind)
    {
        ReadOnlySpan<char> window = text[startIndex..endIndex];
        int relativeIndex = window.LastIndexOfAny(charsToFind);

        return relativeIndex == -1 ? -1 : startIndex + relativeIndex;
    }
}