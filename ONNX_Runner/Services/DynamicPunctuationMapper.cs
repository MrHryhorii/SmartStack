using System.Text;

namespace ONNX_Runner.Services;

/// <summary>
/// Dynamically maps unsupported punctuation marks to standard equivalents based on the currently loaded model.
/// Piper TTS models often fail or skip words when encountering exotic punctuation (like Asian full stops or Arabic question marks) 
/// if those symbols are not explicitly defined in their phoneme dictionary.
/// </summary>
public class DynamicPunctuationMapper
{
    // A cached set of all symbols/phonemes natively supported by the loaded Piper model
    private readonly HashSet<char> _supportedSymbols;

    public DynamicPunctuationMapper(Models.PiperConfig config)
    {
        _supportedSymbols = [];

        if (config?.PhonemeIdMap != null)
        {
            foreach (var key in config.PhonemeIdMap.Keys)
            {
                // Punctuation marks are typically single characters (e.g., ".", "¿")
                if (key.Length == 1)
                {
                    _supportedSymbols.Add(key[0]);
                }
            }
        }
    }

    /// <summary>
    /// Normalizes the input text. If a symbol is supported by the model, it is kept.
    /// Otherwise, it falls back to a standard equivalent to ensure proper pauses and intonation.
    /// </summary>
    public string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        var sb = new StringBuilder(input.Length);

        foreach (char c in input)
        {
            // NATIVE SUPPORT CHECK:
            // If the model explicitly knows this symbol, leave it completely untouched
            if (_supportedSymbols.Contains(c))
            {
                sb.Append(c);
                continue;
            }

            // FALLBACK MAPPING FOR UNKNOWN SYMBOLS:
            switch (c)
            {
                // Asian punctuation -> Standard equivalent
                case '。': sb.Append('.'); break;
                case '！': sb.Append('!'); break;
                case '？': sb.Append('?'); break;
                case '，':
                case '、': sb.Append(','); break;
                case '；': sb.Append(';'); break;
                case '：': sb.Append(':'); break;

                // Middle Eastern and other exotic sentence terminators -> Standard
                case ';': sb.Append('?'); break; // Greek question mark (U+037E)
                case '؟': sb.Append('?'); break; // Arabic/Persian question mark (U+061F)
                case '۔': sb.Append('.'); break; // Arabic/Urdu full stop (U+06D4)
                case '։': sb.Append('.'); break; // Armenian full stop (looks like a colon)
                case '…': sb.Append('.'); break; // Ellipsis (single char) -> replaced with a period for a solid pause

                // Inverted Spanish marks: safely ignored, as the ending mark will dictate the intonation
                case '¿':
                case '¡':
                    break;

                // Brackets/Parentheses: converted to commas to force the TTS engine to take a short, natural breath/pause
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

                // Quotation marks: safely ignored/stripped as they do not affect spoken pauses or intonation
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

                // Default case: keep all standard alphanumeric characters and unmapped symbols
                default:
                    sb.Append(c);
                    break;
            }
        }

        // Cleanup: remove any duplicate commas created by sequential bracket replacements (e.g., "word), word")
        return sb.ToString().Replace(",,", ",");
    }
}