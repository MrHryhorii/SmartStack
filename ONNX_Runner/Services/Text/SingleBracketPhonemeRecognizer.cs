using System.Buffers;
using System.Globalization;
using System.Text;

namespace ONNX_Runner.Services.Text;

/// <summary>Recognizes conservative IPA overrides in single square brackets without allocating per lookup.</summary>
internal static class SingleBracketPhonemeRecognizer
{
    private const string IpaLetters = "ɑɐɒæɓʙβɔɕçɗɖðʤəɘɚɛɜɝɞɟʄɡɠɢʛɦɧħɥʜɨɪʝɭɬɫɮʟɱɯɰŋɳɲɴøɵɸθœɶʘɹɺɾɻʀʁɽʂʃʈʧʉʊʋⱱʌɣɤʍχʎʏʐʑʒʔʡʕʢǀǁǂǃ";
    private const string SharedLetters = "æøœçðŋħ";
    private const string CombiningMarks = "\u0300\u0301\u0302\u0303\u0304\u0306\u0308\u030A\u030B\u030C\u030F\u0318\u0319\u031A\u031C\u031D\u031E\u031F\u0320\u0324\u0325\u0329\u032A\u032C\u032F\u0330\u0334\u0339\u033A\u033B\u033C\u033D\u035C\u0361";
    private const string StrongCombiningMarks = "\u0318\u0319\u031A\u031C\u031D\u031E\u031F\u0320\u0324\u0325\u0329\u032A\u032C\u032F\u0330\u0334\u0339\u033A\u033B\u033C\u033D\u035C\u0361";
    private static readonly (SearchValues<char> All, SearchValues<char> Strong) Alphabet = CreateAlphabet();
    private static readonly SearchValues<char> Marks = SearchValues.Create(CombiningMarks);
    private static readonly SearchValues<char> StrongMarks = SearchValues.Create(StrongCombiningMarks);
    private static readonly SearchValues<char> PostfixMarks = SearchValues.Create("ːˑ˞");
    private static readonly SearchValues<char> Modifiers = SearchValues.Create("ʰʱʲʳʴʵʶʷʸˀˁˠˡˢˣˤⁿᵐᵑᵊ");

    internal static bool IsPrefixMark(char value) => value is 'ˈ' or 'ˌ' or '↑' or '↓' or '↗' or '↘';
    internal static bool IsModifier(char value) => Modifiers.Contains(value);
    internal static bool IsAttachedMark(char value) => PostfixMarks.Contains(value) ||
        Modifiers.Contains(value) || value is >= '˥' and <= '˩' or 'ʼ';

    /// <summary>Returns the complete bracket span only when both its contents and prose context support an IPA override.</summary>
    internal static bool TryGetSpanLength(ReadOnlySpan<char> text, int start, out int length)
    {
        length = 0;
        if ((uint)start >= (uint)text.Length || text[start] != '[' ||
            start + 1 >= text.Length || text[start + 1] == '[' ||
            (start > 0 && text[start - 1] == '[')) return false;

        int offset = text[(start + 1)..].IndexOfAny('[', ']');
        if (offset < 0) return false;
        int closing = start + 1 + offset;
        if (text[closing] != ']' || !HasProseContext(text, start, closing + 1) ||
            !IsConfidentIpa(text[(start + 1)..closing])) return false;

        length = closing - start + 1;
        return true;
    }

    private static bool HasProseContext(ReadOnlySpan<char> text, int start, int end)
    {
        if (start > 0)
        {
            char before = text[start - 1];
            if (before is '\\' or '/' or '_' or '@' or '$' or '=' or '`' or '!' ||
                IsWordAt(text[..start], fromEnd: true)) return false;
        }
        if (end >= text.Length) return true;

        char after = text[end];
        if (after is '\\' or '/' or '_' or '@' or '=' or '`' or '(' ||
            IsWordAt(text[end..], fromEnd: false)) return false;
        return after != '.' || end + 1 >= text.Length || !IsWordAt(text[(end + 1)..], fromEnd: false);
    }

    private static bool IsWordAt(ReadOnlySpan<char> text, bool fromEnd)
    {
        OperationStatus status = fromEnd
            ? Rune.DecodeLastFromUtf16(text, out Rune value, out _)
            : Rune.DecodeFromUtf16(text, out value, out _);
        return status == OperationStatus.Done && (Rune.IsLetterOrDigit(value) ||
            Rune.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);
    }

    private static bool IsConfidentIpa(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text.IsEmpty) return false;
        bool wordHasBase = false, wordHasSignal = false, canAttach = false, needsBase = false, hadWord = false;
        char previousBase = default;

        foreach (char value in text)
        {
            if (Alphabet.All.Contains(value))
            {
                wordHasBase = canAttach = true;
                needsBase = false;
                wordHasSignal |= Alphabet.Strong.Contains(value);
                previousBase = value;
                continue;
            }
            if (Marks.Contains(value) || (value == '\u0327' && previousBase == 'c'))
            {
                if (!canAttach) return false;
                wordHasSignal |= StrongMarks.Contains(value) || (value == '\u030A' && IpaLetters.Contains(previousBase));
                if (value is '\u035C' or '\u0361')
                {
                    needsBase = true;
                    canAttach = false;
                }
                continue;
            }
            if (IsPrefixMark(value))
            {
                if (needsBase) return false;
                wordHasSignal = needsBase = true;
                canAttach = false;
                continue;
            }
            if (PostfixMarks.Contains(value) || value is >= '˥' and <= '˩')
            {
                if (!canAttach) return false;
                wordHasSignal = true;
                continue;
            }
            if (Modifiers.Contains(value))
            {
                wordHasSignal = true;
                if (!canAttach) needsBase = true;
                continue;
            }
            if (value is '.' or ':' or '‿')
            {
                if (!canAttach) return false;
                if (value == '‿') needsBase = true;
                canAttach = false;
                continue;
            }
            if (!char.IsWhiteSpace(value) && value is not (',' or ';' or '!' or '?' or '|' or '‖')) return false;
            if (needsBase || (wordHasBase && !wordHasSignal)) return false;
            hadWord |= wordHasBase;
            wordHasBase = wordHasSignal = canAttach = false;
            previousBase = default;
        }
        return !needsBase && (wordHasBase ? wordHasSignal : hadWord);
    }

    /// <summary>Builds canonical Latin IPA spellings once; request handling never normalizes or copies source text.</summary>
    private static (SearchValues<char> All, SearchValues<char> Strong) CreateAlphabet()
    {
        var letters = new StringBuilder("abcdefghijklmnopqrstuvwxyz" + IpaLetters);
        string strongBases = string.Concat(IpaLetters.Where(value => !SharedLetters.Contains(value)));
        var strongLetters = new StringBuilder(strongBases);
        foreach (var (Start, End) in new[] { (Start: 0x00C0, End: 0x024F), (Start: 0x1E00, End: 0x1EFF) })
        {
            for (int code = Start; code <= End; code++)
            {
                char value = (char)code;
                if (!char.IsLower(value)) continue;
                string decomposition = value.ToString().Normalize(NormalizationForm.FormD);
                if (decomposition.Length <= 1 ||
                    !(char.IsAsciiLetterLower(decomposition[0]) || IpaLetters.Contains(decomposition[0]))) continue;
                bool valid = true;
                bool signal = strongBases.Contains(decomposition[0]);
                foreach (char mark in decomposition.AsSpan(1))
                {
                    if (CombiningMarks.Contains(mark) || (mark == '\u0327' && decomposition[0] == 'c'))
                    {
                        signal |= StrongCombiningMarks.Contains(mark) || (mark == '\u030A' && IpaLetters.Contains(decomposition[0]));
                        continue;
                    }
                    valid = false;
                    break;
                }
                if (!valid) continue;
                letters.Append(value);
                if (signal) strongLetters.Append(value);
            }
        }
        return (SearchValues.Create(letters.ToString()), SearchValues.Create(strongLetters.ToString()));
    }
}
