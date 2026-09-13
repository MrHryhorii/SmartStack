using System.Buffers;
using System.Globalization;
using System.Text;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Dynamically normalizes punctuation against the phoneme inventory of the currently loaded Piper model.
/// Unsupported exotic punctuation is mapped to safe model-supported equivalents where possible, while
/// purely visual or unsafe symbols are removed before they can reach the final phoneme stream.
/// </summary>
public sealed class DynamicPunctuationMapper
{
    // Stores every single Unicode scalar value explicitly supported by the loaded Piper model.
    // Rune is used instead of char so supplementary-plane symbols are handled correctly.
    private readonly HashSet<Rune> _supportedSymbols = [];

    // Cached model-safe fallback punctuation. A null character means that the model has no usable
    // equivalent and the source punctuation should be stripped rather than forwarded unsupported.
    private readonly char _periodFallback;
    private readonly char _questionFallback;
    private readonly char _exclamationFallback;
    private readonly char _commaFallback;
    private readonly char _semicolonFallback;
    private readonly char _colonFallback;

    public DynamicPunctuationMapper(PiperConfig config)
    {
        if (config?.PhonemeIdMap != null)
        {
            foreach (string key in config.PhonemeIdMap.Keys)
            {
                if (TryGetSingleRune(key, out Rune rune))
                {
                    _supportedSymbols.Add(rune);
                }
            }
        }

        // Prefer the semantically closest punctuation that the current model actually supports.
        // If the ideal mark is unavailable, degrade to a weaker pause instead of emitting an
        // unsupported symbol that could cause silence, artifacts, or skipped text.
        _periodFallback = SelectSupported('.', ',');
        _questionFallback = SelectSupported('?', '.', ',');
        _exclamationFallback = SelectSupported('!', '.', ',');
        _commaFallback = SelectSupported(',', '.');
        _semicolonFallback = SelectSupported(';', ',', '.');
        _colonFallback = SelectSupported(':', ';', ',', '.');
    }

    /// <summary>
    /// Normalizes punctuation while preserving text that is safe for eSpeak.
    /// Returns the original string instance when no normalization is required.
    /// </summary>
    public string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        // ELLIPSIS FALLBACK:
        // Preserve a native Unicode ellipsis when the loaded model supports it.
        // Otherwise, emit two model-native space tokens followed by the safest
        // supported sentence/pause boundary. ASCII "..." remains untouched.
        if (input == "…" && !_supportedSymbols.Contains(new Rune('…')))
        {
            return _periodFallback switch
            {
                '.' => "  .",
                ',' => "  ,",
                _ => "  "
            };
        }

        ReadOnlySpan<char> text = input.AsSpan();

        // COMMON FAST PATH:
        // Most ordinary text already contains only supported punctuation and safe text characters.
        // Avoid allocating StringBuilder + result string when normalization would be a no-op.
        if (!NeedsNormalization(text))
        {
            return input;
        }

        var builder = new StringBuilder(input.Length);
        int index = 0;

        while (index < text.Length)
        {
            int consumed = DecodeRune(text[index..], out Rune rune);

            // SILENT VISUAL PUNCTUATION STRIPPING:
            // Quotes carry structure for humans but are not useful phonetic content here.
            // Apostrophes are intentionally handled separately because they may be lexical.
            if (IsVisualQuote(rune))
            {
                index += consumed;
                continue;
            }

            // NATIVE SUPPORT:
            // Preserve punctuation/symbols explicitly known by this Piper model.
            if (_supportedSymbols.Contains(rune))
            {
                AppendRune(builder, rune, collapseComma: true);
                index += consumed;
                continue;
            }

            // KNOWN UNSUPPORTED PUNCTUATION:
            // Map only to punctuation that the current model actually supports.
            if (TryMapUnsupportedPunctuation(rune, out char mapped))
            {
                AppendMapped(builder, mapped);
                index += consumed;
                continue;
            }

            // Inverted Spanish opening marks are intentionally silent. The closing punctuation
            // carries the intonation and forwarding an unsupported opening mark is unnecessary.
            if (rune.Value is '¿' or '¡')
            {
                index += consumed;
                continue;
            }

            // Lexical apostrophes and hyphens must survive when they occur inside a word.
            // eSpeak can then interpret them as orthographic structure instead of Piper seeing
            // a standalone unsupported punctuation symbol.
            if (IsLexicalConnector(text, index, consumed, rune))
            {
                AppendRune(builder, rune, collapseComma: false);
                index += consumed;
                continue;
            }

            // Ordinary text content remains untouched. Unsupported punctuation, emoji, dingbats,
            // and miscellaneous symbols fall through and are deliberately stripped.
            if (IsSafeTextRune(rune))
            {
                AppendRune(builder, rune, collapseComma: false);
            }

            index += consumed;
        }

        return builder.ToString();
    }

    private bool NeedsNormalization(ReadOnlySpan<char> text)
    {
        int index = 0;

        while (index < text.Length)
        {
            int consumed = DecodeRune(text[index..], out Rune rune);

            if (IsVisualQuote(rune))
            {
                return true;
            }

            if (_supportedSymbols.Contains(rune))
            {
                index += consumed;
                continue;
            }

            if (IsKnownFallbackSource(rune) ||
                rune.Value is '¿' or '¡')
            {
                return true;
            }

            if (IsLexicalConnector(text, index, consumed, rune) ||
                IsSafeTextRune(rune))
            {
                index += consumed;
                continue;
            }

            // Unknown unsupported punctuation/symbol would be stripped.
            return true;
        }

        return false;
    }

    private bool TryMapUnsupportedPunctuation(Rune rune, out char mapped)
    {
        mapped = rune.Value switch
        {
            // Asian punctuation
            '。' => _periodFallback,
            '！' => _exclamationFallback,
            '？' => _questionFallback,
            '，' or '、' => _commaFallback,
            '；' => _semicolonFallback,
            '：' => _colonFallback,

            // Middle Eastern and other sentence terminators
            ';' => _questionFallback, // Greek question mark (U+037E)
            '؟' => _questionFallback, // Arabic/Persian question mark (U+061F)
            '۔' => _periodFallback,   // Arabic/Urdu full stop (U+06D4)
            '։' => _periodFallback,   // Armenian full stop

            // Brackets/parentheses become a short pause when unsupported.
            '(' or ')' or
            '[' or ']' or
            '{' or '}' or
            '⟨' or '⟩' => _commaFallback,

            _ => '\0'
        };

        return mapped != '\0';
    }

    private static bool IsKnownFallbackSource(Rune rune)
    {
        return rune.Value is
            '。' or '！' or '？' or '，' or '、' or '；' or '：' or
            ';' or '؟' or '۔' or '։' or
            '(' or ')' or '[' or ']' or '{' or '}' or '⟨' or '⟩';
    }

    private static bool IsVisualQuote(Rune rune)
    {
        return rune.Value is
            '"' or
            '“' or '”' or '„' or
            '«' or '»' or
            '‹' or '›' or
            '「' or '」' or
            '『' or '』';
    }

    private static bool IsLexicalConnector(
        ReadOnlySpan<char> text,
        int index,
        int consumed,
        Rune rune)
    {
        if (!IsConnectorRune(rune))
        {
            return false;
        }

        if (index == 0 || index + consumed >= text.Length)
        {
            return false;
        }

        if (!TryDecodePreviousRune(text[..index], out Rune previous) ||
            !TryDecodeNextRune(text[(index + consumed)..], out Rune next))
        {
            return false;
        }

        return IsWordRune(previous) && IsWordRune(next);
    }

    private static bool IsConnectorRune(Rune rune)
    {
        return rune.Value is
            '\'' or '’' or 'ʼ' or
            '-' or '\u2010' or '\u2011' or
            '\u058A' or '\u05BE' or '\u30A0';
    }

    private static bool IsWordRune(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;
    }

    private static bool IsSafeTextRune(Rune rune)
    {
        if (Rune.IsWhiteSpace(rune))
        {
            return true;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;
    }

    private char SelectSupported(params char[] candidates)
    {
        foreach (char candidate in candidates)
        {
            if (_supportedSymbols.Contains(new Rune(candidate)))
            {
                return candidate;
            }
        }

        return '\0';
    }

    private static void AppendMapped(StringBuilder builder, char mapped)
    {
        if (mapped == '\0')
        {
            return;
        }

        if (mapped == ',' &&
            builder.Length > 0 &&
            builder[^1] == ',')
        {
            return;
        }

        builder.Append(mapped);
    }

    private static void AppendRune(
        StringBuilder builder,
        Rune rune,
        bool collapseComma)
    {
        if (collapseComma &&
            rune.Value == ',' &&
            builder.Length > 0 &&
            builder[^1] == ',')
        {
            return;
        }

        Span<char> buffer = stackalloc char[2];
        int written = rune.EncodeToUtf16(buffer);
        builder.Append(buffer[..written]);
    }

    private static bool TryGetSingleRune(
        string value,
        out Rune rune)
    {
        rune = default;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        OperationStatus status = Rune.DecodeFromUtf16(
            value.AsSpan(),
            out rune,
            out int consumed);

        return status == OperationStatus.Done &&
               consumed == value.Length;
    }

    private static int DecodeRune(
        ReadOnlySpan<char> text,
        out Rune rune)
    {
        OperationStatus status = Rune.DecodeFromUtf16(
            text,
            out rune,
            out int consumed);

        if (status == OperationStatus.Done)
        {
            return consumed;
        }

        rune = Rune.ReplacementChar;
        return 1;
    }

    private static bool TryDecodePreviousRune(
        ReadOnlySpan<char> text,
        out Rune rune)
    {
        OperationStatus status = Rune.DecodeLastFromUtf16(
            text,
            out rune,
            out _);

        return status == OperationStatus.Done;
    }

    private static bool TryDecodeNextRune(
        ReadOnlySpan<char> text,
        out Rune rune)
    {
        OperationStatus status = Rune.DecodeFromUtf16(
            text,
            out rune,
            out _);

        return status == OperationStatus.Done;
    }
}