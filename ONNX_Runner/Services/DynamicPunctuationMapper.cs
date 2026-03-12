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

                // Близькосхідна та інші екзотичні кінці речень -> Стандартні
                case ';': sb.Append('?'); break; // Грецький знак питання (U+037E)
                case '؟': sb.Append('?'); break; // Арабський/перський знак питання (U+061F)
                case '۔': sb.Append('.'); break; // Арабська/урду крапка (U+06D4)
                case '։': sb.Append('.'); break; // Вірменська крапка (виглядає як :, але це кінець речення)
                case '…': sb.Append('.'); break; // Трикрапка (один символ) -> просто крапка для паузи

                // Перевернуті іспанські знаки: ігноруємо, бо інтонацію задасть знак в кінці
                case '¿':
                case '¡':
                    break;

                // Дужки: перетворюємо на кому, щоб Piper зробив коротку паузу
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
                case '”':
                case '“':
                    break;

                default:
                    sb.Append(c);
                    break;
            }
        }

        // Очищаємо можливі дублікати ком, які виникли через заміни
        return sb.ToString().Replace(",,", ",");
    }
}