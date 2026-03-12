using System.Text;

namespace ONNX_Runner.Services;

public class PhonemeChunker(int minChunkLength = 350, int maxChunkLength = 450)
{
    // Мінімальна довжина: рятує від розривів на ініціалах "А. С. Пушкін"
    private readonly int _minChunkLength = minChunkLength;

    // Максимальна довжина: рятує відеокарту/RAM від переповнення (OOM)
    private readonly int _maxChunkLength = maxChunkLength;

    public List<string> SplitIntoChunks(string phonemes)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(phonemes)) return result;

        var currentChunk = new StringBuilder();

        // Розбиваємо текст на слова (фонеми зазвичай розділені пробілами)
        var words = phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (currentChunk.Length > 0) currentChunk.Append(' ');
            currentChunk.Append(word);

            // Шукаємо ознаки кінця думки (включаючи екзотичні та типографічні)
            bool hasTerminator = word.EndsWith('.') ||
                                 word.EndsWith('!') ||
                                 word.EndsWith('?') ||
                                 word.EndsWith(';') ||    // Звичайна крапка з комою
                                 word.EndsWith(':') ||
                                 word.EndsWith(';') ||    // Грецький знак питання (U+037E)
                                 word.EndsWith('؟') ||    // Арабський/перський знак питання (U+061F)
                                 word.EndsWith('۔') ||    // Арабська/урду крапка (U+06D4)
                                 word.EndsWith('։') ||    // Вірменська крапка (U+0589, виглядає як двокрапка)
                                 word.EndsWith('。') ||   // Китайська/японська крапка
                                 word.EndsWith('！') ||   // Широкий знак оклику
                                 word.EndsWith('？') ||   // Широкий знак питання
                                 word.EndsWith('…') ||    // Трикрапка (один символ)
                                 word.EndsWith(".\"") ||
                                 word.EndsWith("!\"") ||
                                 word.EndsWith("?\"") ||
                                 word.EndsWith(".»") ||
                                 word.EndsWith("!»") ||
                                 word.EndsWith("?»") ||
                                 word.EndsWith(".”") ||    // Англійські фігурні лапки
                                 word.EndsWith("!”") ||
                                 word.EndsWith("?”");

            // Нормальне розбиття: є крапка І ми вже набрали мінімальну масу
            if (hasTerminator && currentChunk.Length >= _minChunkLength)
            {
                result.Add(currentChunk.ToString());
                currentChunk.Clear();
            }
            // Аварійне розбиття: речення занадто довге (наприклад, перелік без крапок)
            else if (currentChunk.Length >= _maxChunkLength)
            {
                // Якщо зустріли кому — це ідеальне місце для "м'якого" розриву
                if (word.EndsWith(','))
                {
                    result.Add(currentChunk.ToString());
                    currentChunk.Clear();
                }
                // Якщо коми немає, а ми вже перевищили критичний ліміт (+50 символів) - ріжемо жорстко
                else if (currentChunk.Length >= _maxChunkLength + 50)
                {
                    result.Add(currentChunk.ToString());
                    currentChunk.Clear();
                }
            }
        }

        // Забираємо хвіст, якщо він залишився
        if (currentChunk.Length > 0)
        {
            result.Add(currentChunk.ToString());
        }

        return result;
    }
}