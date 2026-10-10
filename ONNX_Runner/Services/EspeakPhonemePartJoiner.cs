using System.Globalization;
using System.Text;

namespace ONNX_Runner.Services;

/// <summary>
/// Preserves word boundaries when eSpeak returns separate native phoneme fragments.
/// Some native builds include leading whitespace in each fragment; others do not.
/// </summary>
internal static class EspeakPhonemePartJoiner
{
    internal static void Append(StringBuilder output, ReadOnlySpan<char> part)
    {
        if (part.IsEmpty)
        {
            return;
        }

        Separate(output, part);
        output.Append(part);
    }

    internal static void Separate(StringBuilder output, ReadOnlySpan<char> part)
    {
        if (!part.IsEmpty && output.Length > 0 &&
            !char.IsWhiteSpace(output[output.Length - 1]) && !char.IsWhiteSpace(part[0]) &&
            CanStartWord(part[0]) && CanEndWordOrClause(output[output.Length - 1]))
            output.Append(' ');
    }

    private static bool CanStartWord(char value)
    {
        UnicodeCategory category = char.GetUnicodeCategory(value);

        return char.IsLetterOrDigit(value) ||
               category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }

    private static bool CanEndWordOrClause(char value)
    {
        return CanStartWord(value) || value is '.' or '!' or '?' or ',' or ';' or ':';
    }
}
