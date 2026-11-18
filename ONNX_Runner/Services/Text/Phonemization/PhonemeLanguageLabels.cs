using System.Text;

namespace ONNX_Runner.Services.Text.Phonemization;

/// <summary>Reads native eSpeak language-switch metadata without selecting a fixed fallback language.</summary>
internal static class PhonemeLanguageLabels
{
    internal static bool TryFind(ReadOnlySpan<char> text, int start, out int codeStart, out int codeLength)
    {
        codeStart = codeLength = 0;
        while (start < text.Length)
        {
            int opening = text[start..].IndexOf('(');
            if (opening < 0) return false;
            opening += start;
            int closing = text[(opening + 1)..].IndexOf(')');
            if (closing < 0) return false;
            closing += opening + 1;
            ReadOnlySpan<char> code = text[(opening + 1)..closing];
            bool valid = code.Length is > 0 and <= 64;
            bool hasLetter = false;
            foreach (char value in code)
            {
                hasLetter |= char.IsAsciiLetter(value);
                valid &= char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '+' or '/';
            }
            if (valid && hasLetter)
            {
                codeStart = opening + 1;
                codeLength = code.Length;
                return true;
            }
            start = closing + 1;
        }
        return false;
    }

    internal static string Remove(string text)
    {
        int opening = text.IndexOf('(');
        if (opening < 0) return text;
        var output = new StringBuilder(text.Length);
        int start = 0;
        while (opening >= 0)
        {
            int closing = text.IndexOf(')', opening + 1);
            if (closing < 0) break;
            output.Append(text.AsSpan(start, opening - start));
            start = closing + 1;
            opening = text.IndexOf('(', start);
        }
        output.Append(text.AsSpan(start));
        return output.ToString().Trim();
    }
}
