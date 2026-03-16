using System.Text;

namespace ONNX_Runner.Services;

public class PhonemeChunker(int minChunkLength = 350, int maxChunkLength = 450)
{
    private readonly int _minChunkLength = minChunkLength;
    private readonly int _maxChunkLength = maxChunkLength;

    public List<string> SplitIntoChunks(string phonemes)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(phonemes)) return result;

        var currentChunk = new StringBuilder();
        var words = phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (currentChunk.Length > 0) currentChunk.Append(' ');
            currentChunk.Append(word);

            // Шукаємо ознаки кінця думки
            bool hasTerminator = word.EndsWith('.') || word.EndsWith('!') || word.EndsWith('?') ||
                                 word.EndsWith(';') || word.EndsWith(':') || word.EndsWith(';') ||
                                 word.EndsWith('؟') || word.EndsWith('۔') || word.EndsWith('։') ||
                                 word.EndsWith('。') || word.EndsWith('！') || word.EndsWith('？') ||
                                 word.EndsWith('…') || word.EndsWith(".\"") || word.EndsWith("!\"") ||
                                 word.EndsWith("?\"") || word.EndsWith(".»") || word.EndsWith("!»") ||
                                 word.EndsWith("?»") || word.EndsWith(".”") || word.EndsWith("!”") ||
                                 word.EndsWith("?”");

            // Нормальне розбиття (Є крапка і достатня довжина)
            if (hasTerminator && currentChunk.Length >= _minChunkLength)
            {
                result.Add(currentChunk.ToString());
                currentChunk.Clear();
            }
            // Аварійне розбиття (Речення занадто довге)
            else if (currentChunk.Length >= _maxChunkLength)
            {
                // Якщо зустріли кому — це ідеальне місце. Модель сама зробить паузу.
                if (word.EndsWith(','))
                {
                    result.Add(currentChunk.ToString());
                    currentChunk.Clear();
                }
                // Якщо коми немає, а ми вже перевищили критичний ліміт (+50 символів) - ріжемо жорстко
                else if (currentChunk.Length >= _maxChunkLength + 50)
                {
                    // Додаємо штучну кому, щоб Piper не опускав інтонацію як в кінці речення
                    currentChunk.Append(',');
                    result.Add(currentChunk.ToString());
                    currentChunk.Clear();
                }
            }
        }

        // Забираємо хвіст, якщо він залишився
        if (currentChunk.Length > 0)
        {
            // Якщо хвіст не закінчується на знак пунктуації, можна теж додати крапку, 
            // щоб звук плавно затухав, а не обривався.
            string finalChunk = currentChunk.ToString();
            char lastChar = finalChunk[^1];

            if (!char.IsPunctuation(lastChar))
            {
                finalChunk += ".";
            }

            result.Add(finalChunk);
        }

        return result;
    }
}