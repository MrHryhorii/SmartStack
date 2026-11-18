using System.Buffers;

namespace ONNX_Runner.Services.Text;

/// <summary>Shares raw-phoneme delimiters and classification between chunking and phonemization.</summary>
internal static class PhonemeBlockRecognizer
{
    internal readonly record struct Block(int Start, int Length, int DelimiterLength)
    {
        internal int End => Start + Length;
        internal int ContentStart => Start + DelimiterLength;
        internal int ContentEnd => End - DelimiterLength;
    }

    // Slash notation retains its established symbol heuristic. Single brackets remain conservative.
    private static readonly SearchValues<char> SlashIpaSignals = SearchValues.Create(
        "ɑɐɒæɓʙβɔɕçɗɖðʤəɘɚɛɜɝɞɟʄɡɠɢʛɦɧħɥʜɨɪʝɭɬɫɮʟɱɯɰŋɳɲɴøɵɸθœɶʘɹɺɾɻʀʁɽʂʃʈʧʉʊʋⱱʌɣɤʍχʎʏʐʑʒʔʡʕʢǀǁǂǃˈˌːˑʼʴʰʲʷˠˤ");

    internal static bool TryGetAt(ReadOnlySpan<char> text, int start, out Block block)
    {
        return TryReadCandidate(text, start, out block) && IsRecognized(text, block);
    }

    /// <summary>Searches opening positions in the range and validates against the complete source.</summary>
    internal static bool TryFindNext(ReadOnlySpan<char> text, int start, int limit, out Block block)
    {
        while (start < limit)
        {
            int offset = text[start..limit].IndexOfAny('[', '/');
            if (offset < 0) break;
            int candidate = start + offset;
            if (!TryReadCandidate(text, candidate, out block))
            {
                start = candidate + 1;
                continue;
            }
            if (IsRecognized(text, block)) return true;
            start = block.End;
        }
        block = default;
        return false;
    }

    private static bool IsRecognized(ReadOnlySpan<char> text, Block block)
    {
        if (block.DelimiterLength == 2) return true;
        if (text[block.Start] == '/')
            return text[block.ContentStart..block.ContentEnd].IndexOfAny(SlashIpaSignals) >= 0;
        return SingleBracketPhonemeRecognizer.TryGetSpanLength(text, block.Start, out int length) && length == block.Length;
    }

    private static bool TryReadCandidate(ReadOnlySpan<char> text, int start, out Block block)
    {
        block = default;
        if ((uint)start >= (uint)text.Length || start + 1 >= text.Length) return false;
        if (text[start] == '/')
        {
            int offset = text[(start + 1)..].IndexOf('/');
            if (offset <= 0) return false;
            int numericEnd = start + 1;
            while (numericEnd < start + 1 + offset && char.IsDigit(text[numericEnd])) numericEnd++;
            if (numericEnd == start + 1 + offset) return false;
            block = new Block(start, offset + 2, 1);
            return true;
        }
        if (text[start] != '[') return false;
        if (text[start + 1] == '[')
        {
            int offset = text[(start + 2)..].IndexOf(']');
            int closing = start + 2 + offset;
            if (offset <= 0 || closing + 1 >= text.Length || text[closing + 1] != ']') return false;
            block = new Block(start, closing + 2 - start, 2);
            return true;
        }

        int singleOffset = text[(start + 1)..].IndexOfAny('[', ']');
        if (singleOffset <= 0 || text[start + 1 + singleOffset] != ']') return false;
        block = new Block(start, singleOffset + 2, 1);
        return true;
    }
}
