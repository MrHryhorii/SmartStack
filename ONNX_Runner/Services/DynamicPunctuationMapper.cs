using System.Text;

namespace ONNX_Runner.Services;

public class DynamicPunctuationMapper
{
    // Зберігаємо всі підтримувані символи/фонеми, які є в phoneme_id_map моделі
    private readonly HashSet<char> _supportedSymbols;

    public DynamicPunctuationMapper(Models.PiperConfig config)
    {
        _supportedSymbols = [];

        if (config?.PhonemeIdMap != null)
        {
            foreach (var key in config.PhonemeIdMap.Keys)
            {
                // Зазвичай пунктуація — це один символ (напр. ".", "¿")
                if (key.Length == 1)
                {
                    _supportedSymbols.Add(key[0]);
                }
            }
        }
    }

    public string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        var sb = new StringBuilder(input.Length);

        foreach (char c in input)
        {
            // ПЕРЕВІРКА НА НАЯВНІСТЬ У МОДЕЛІ:
            // Якщо модель знає цей знак, просто пропускаємо його як є
            if (_supportedSymbols.Contains(c))
            {
                sb.Append(c);
                continue;
            }

            // ФОЛБЕК-ЗАМІНИ ДЛЯ НЕВІДОМИХ ЗНАКІВ:
            switch (c)
            {
                // Азійська пунктуація -> Стандартна
                case '。': sb.Append('.'); break;
                case '！': sb.Append('!'); break;
                case '？': sb.Append('?'); break;
                case '，':
                case '、': sb.Append(','); break;
                case '；': sb.Append(';'); break;
                case '：': sb.Append(':'); break;

                // Перевернуті іспанські знаки: якщо їх немає в моделі, просто ігноруємо
                case '¿':
                case '¡':
                    break;

                // Дужки: перетворюємо на кому, щоб Piper зробив паузу
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                case '⟨':
                case '⟩':
                    sb.Append(',');
                    break;

                // Лапки: безпечно ігноруємо, бо вони не впливають на паузи
                case '«':
                case '»':
                case '「':
                case '」':
                case '『':
                case '』':
                case '"':
                case '\'':
                    break;

                default:
                    // Інші невідомі символи залишаємо (можливо Piper сам їх проігнорує), 
                    // або можна взагалі пропускати, щоб не засмічувати вивід.
                    sb.Append(c);
                    break;
            }
        }

        // Очищаємо можливі дублікати ком, які виникли через заміни
        return sb.ToString().Replace(",,", ",");
    }
}