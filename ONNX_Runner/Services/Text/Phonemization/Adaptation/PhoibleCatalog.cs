using System.Text;

namespace ONNX_Runner.Services.Text.Phonemization.Adaptation;

internal enum PhonemeKind { Unknown, Vowel, Consonant, Tone }

internal sealed record PhonemeProfile(PhonemeKind Kind, sbyte[][] Features)
{
    internal int Steps { get; } = Features.Length == 0 ? 1 : Features.Max(feature => feature.Length);
}

/// <summary>Startup-only feature data, including all phases of contour segments.</summary>
internal sealed class PhoibleCatalog
{
    internal Dictionary<string, PhonemeProfile> Profiles { get; } = new(StringComparer.Ordinal);
    private string[] _features = [];
    private int[] _weights = [];

    internal PhoibleCatalog(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"[WARNING] PHOIBLE database not found at '{path}'. Structural IPA adaptation remains available.");
            return;
        }
        using var lines = File.ReadLines(path).GetEnumerator();
        if (!lines.MoveNext()) return;
        string[] headers = ParseRow(lines.Current);
        int phoneme = Array.FindIndex(headers, value => value.Equals("Phoneme", StringComparison.OrdinalIgnoreCase));
        int kind = Array.FindIndex(headers, value => value.Equals("SegmentClass", StringComparison.OrdinalIgnoreCase));
        int start = Array.FindIndex(headers, value => value.Equals("tone", StringComparison.OrdinalIgnoreCase));
        if (phoneme < 0 || start < 0) return;
        _features = headers[start..];
        _weights = _features.Select(Weight).ToArray();
        while (lines.MoveNext())
        {
            string[] columns = ParseRow(lines.Current);
            if (columns.Length <= Math.Max(phoneme, start) || string.IsNullOrWhiteSpace(columns[phoneme])) continue;
            string source = IpaReader.Canonical(columns[phoneme]);
            if (Profiles.ContainsKey(source)) continue;
            var values = new sbyte[_features.Length][];
            for (int index = 0; index < values.Length; index++)
                values[index] = start + index < columns.Length
                    ? columns[start + index].Split(',').Select(ParseValue).ToArray() : [0];
            var segmentKind = kind >= 0 && kind < columns.Length ? columns[kind] switch
            {
                "vowel" => PhonemeKind.Vowel,
                "consonant" => PhonemeKind.Consonant,
                "tone" => PhonemeKind.Tone,
                _ => PhonemeKind.Unknown
            } : PhonemeKind.Unknown;
            Profiles[source] = new(segmentKind, values);
        }
    }

    internal bool TryGet(string source, out PhonemeProfile profile) =>
        Profiles.TryGetValue(IpaReader.Canonical(source), out profile!);

    internal bool IsVoiced(string source)
    {
        sbyte value = TryGet(source, out var profile) ? Value(profile, "periodicGlottalSource") : (sbyte)0;
        if (value != 0) return value > 0;
        return source.EnumerateRunes().Any(rune => "aeiouyɑɐɒæəɘɜɞɛɤɨɪɯɵœøɔʊʉʌɶɚɝbdɡgvzʒðʑɣʁɦmnŋɲɳɴrlɹɾɽʎɭjwʋ".Contains(rune.ToString(), StringComparison.Ordinal));
    }

    internal sbyte Value(PhonemeProfile profile, string name)
    {
        int index = Array.IndexOf(_features, name);
        return index >= 0 ? profile.Features[index][0] : (sbyte)0;
    }

    internal PhonemeProfile WithVoicing(PhonemeProfile profile, bool voiced)
    {
        int index = Array.IndexOf(_features, "periodicGlottalSource");
        if (index < 0) return profile;
        sbyte[][] values = profile.Features.ToArray();
        values[index] = [(sbyte)(voiced ? 1 : -1)];
        return new(profile.Kind, values);
    }

    internal int Distance(PhonemeProfile source, PhonemeProfile target, int cutoff)
    {
        int score = source.Kind != PhonemeKind.Unknown && target.Kind != PhonemeKind.Unknown && source.Kind != target.Kind ? 128 : 0;
        int steps = Math.Max(source.Steps, target.Steps);
        for (int step = 0; step < steps; step++)
        {
            for (int index = 0; index < _features.Length; index++)
            {
                sbyte[] left = source.Features[index], right = target.Features[index];
                int a = steps == 1 ? 0 : step * (left.Length - 1) / (steps - 1);
                int b = steps == 1 ? 0 : step * (right.Length - 1) / (steps - 1);
                if (left[a] == 0 || left[a] == right[b]) continue;
                score += right[b] == 0 ? 1 : _weights[index];
                if (score >= cutoff) return score;
            }
        }
        return score + Math.Abs(source.Steps - target.Steps);
    }

    private static int Weight(string name) => name switch
    {
        "periodicGlottalSource" => 8,
        "syllabic" or "consonantal" or "sonorant" or "continuant" or "delayedRelease" or "nasal" => 4,
        "high" or "low" or "front" or "back" or "round" or "labial" or "coronal" or "dorsal" => 2,
        _ => 1
    };

    private static sbyte ParseValue(string value) => value.Trim() switch { "+" => 1, "-" => -1, _ => 0 };

    private static string[] ParseRow(string line)
    {
        var columns = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char symbol = line[index];
            if (symbol == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { value.Append('"'); index++; }
                else quoted = !quoted;
                continue;
            }
            if (symbol == ',' && !quoted) { columns.Add(value.ToString().Trim()); value.Clear(); continue; }
            value.Append(symbol);
        }
        columns.Add(value.ToString().Trim());
        return columns.ToArray();
    }
}
