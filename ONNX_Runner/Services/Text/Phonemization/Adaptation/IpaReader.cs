using System.Globalization;
using System.Text;

namespace ONNX_Runner.Services.Text.Phonemization.Adaptation;

internal enum IpaModifier
{
    None, Nasal, Palatal, Labial, Velar, Pharyngeal, Aspirated, Breathy,
    Long, HalfLong, Syllabic, NonSyllabic, NasalRelease, LabialNasalRelease, VelarNasalRelease, LateralRelease, Rhotic,
    Voiced, Voiceless, Feature
}

/// <summary>Reads a base with attached marks, tied components and secondary articulations.</summary>
internal static class IpaReader
{
    internal static IpaModifier Modifier(char value) => value switch
    {
        '\u0303' => IpaModifier.Nasal,
        'ʲ' => IpaModifier.Palatal,
        'ʷ' or 'ᶣ' => IpaModifier.Labial,
        'ˠ' => IpaModifier.Velar,
        'ˤ' => IpaModifier.Pharyngeal,
        'ʰ' => IpaModifier.Aspirated,
        'ʱ' => IpaModifier.Breathy,
        'ː' => IpaModifier.Long,
        'ˑ' => IpaModifier.HalfLong,
        '\u0329' => IpaModifier.Syllabic,
        '\u032F' => IpaModifier.NonSyllabic,
        'ⁿ' => IpaModifier.NasalRelease,
        'ᵐ' => IpaModifier.LabialNasalRelease,
        'ᵑ' => IpaModifier.VelarNasalRelease,
        'ˡ' => IpaModifier.LateralRelease,
        '˞' => IpaModifier.Rhotic,
        '\u032C' => IpaModifier.Voiced,
        '\u0325' or '\u030A' => IpaModifier.Voiceless,
        'ʼ' or 'ˀ' or 'ᵊ' => IpaModifier.Feature,
        _ => IsCombining(value) && !IsTie(value) ? IpaModifier.Feature : IpaModifier.None
    };

    internal static bool IsTie(char value) => value is '\u035C' or '\u0361';
    internal static bool IsCombining(char value) => char.GetUnicodeCategory(value) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    internal static bool IsBoundary(char value) => char.IsWhiteSpace(value) || char.IsPunctuation(value) || value is 'ˈ' or 'ˌ';

    internal static int ReadLength(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return 0;
        int length = PhonemeInventory.ScalarLength(text);
        if (IsBoundary(text[0])) return length;

        // Prefix modifiers remain attached to the following base.
        if (Modifier(text[0]) != IpaModifier.None)
        {
            while (length < text.Length && Modifier(text[length]) != IpaModifier.None)
                length += PhonemeInventory.ScalarLength(text[length..]);
            if (length < text.Length && !IsBoundary(text[length]))
                length += PhonemeInventory.ScalarLength(text[length..]);
        }
        while (length < text.Length)
        {
            char value = text[length];
            if (IsTie(value))
            {
                length++;
                if (length < text.Length && !IsBoundary(text[length]))
                    length += PhonemeInventory.ScalarLength(text[length..]);
                continue;
            }
            if (Modifier(value) == IpaModifier.None) break;
            length += PhonemeInventory.ScalarLength(text[length..]);
        }
        return length;
    }

    internal static string Canonical(string source) => source.Normalize(NormalizationForm.FormD)
        .Replace('g', 'ɡ').Replace("\u0361", "", StringComparison.Ordinal).Replace("\u035C", "", StringComparison.Ordinal);

    internal static bool CanSplit(ReadOnlySpan<char> source, int index) => index > 0 && index < source.Length &&
        !char.IsLowSurrogate(source[index]) && !IsTie(source[index - 1]) && !IsTie(source[index]) &&
        Modifier(source[index]) == IpaModifier.None;

    internal static bool HasBase(ReadOnlySpan<char> source)
    {
        foreach (char value in source)
            if (char.IsLetter(value) && Modifier(value) == IpaModifier.None && value is not ('ˈ' or 'ˌ')) return true;
        return false;
    }
}
