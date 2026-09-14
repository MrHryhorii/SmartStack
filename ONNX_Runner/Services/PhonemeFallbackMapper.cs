using System.Buffers;
using System.Globalization;
using System.Text;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Intelligent model-specific phonetic fallback system.
/// It maps unsupported IPA phonemes to the closest phonetic relatives available in the
/// currently loaded Piper model and precalculates complex decomposition rules at startup.
/// Runtime synthesis therefore performs only O(1) dictionary lookups and short longest-match scans.
/// </summary>
public sealed class PhonemeFallbackMapper
{
    // Key = a PHOIBLE phoneme, Value = the closest supported phoneme in the loaded model.
    private readonly Dictionary<string, string> _precalculatedMap = new(StringComparer.Ordinal);

    // Key = an explicitly supported complex IPA sequence (e.g., pʲ, t͡s, ɛ̃),
    // Value = a fully resolved sequence that the current model can actually consume.
    // Only rules that are needed by the loaded model are stored here.
    private readonly Dictionary<string, string> _sequenceFallbackMap = new(StringComparer.Ordinal);

    // Cached inventory of the currently loaded Piper model.
    private readonly HashSet<string> _supportedModelPhonemes;
    private readonly int _maxSupportedModelPhonemeLength;

    // Maximum UTF-16 length of an active complex sequence. The runtime validator uses this
    // to bound longest-match scans to only a few characters.
    public int MaxSequenceLength { get; private set; }

    // Rules to break down complex IPA symbols into simpler phoneme sequences that a model
    // is more likely to support. These rules are resolved against the actual model inventory
    // once at startup; they are never interpreted dynamically during synthesis.
    private static readonly IReadOnlyDictionary<string, string> DecompositionRules =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Affricates (Ligatures and combined symbols)
            {"t͡s", "ts"}, {"t͡ʃ", "tʃ"}, {"d͡z", "dz"}, {"d͡ʒ", "dʒ"},
            {"ʦ", "ts"},  {"ʧ", "tʃ"},  {"ʣ", "dz"},  {"ʤ", "dʒ"},
            {"p͡f", "pf"}, {"k͡x", "kx"}, {"t͡ɕ", "tɕ"}, {"d͡ʑ", "dʑ"},

            // Palatalization (Soft consonants - critical for Slavic languages)
            // Decomposed into "Hard consonant + Jot [j]"
            {"pʲ", "pj"}, {"bʲ", "bj"}, {"mʲ", "mj"}, {"fʲ", "fj"}, {"vʲ", "vj"},
            {"tʲ", "tj"}, {"dʲ", "dj"}, {"nʲ", "nj"}, {"lʲ", "lj"}, {"rʲ", "rj"},
            {"sʲ", "sj"}, {"zʲ", "zj"}, {"kʲ", "kj"}, {"gʲ", "gj"}, {"xʲ", "xj"},

            // Aspiration (Common in Asian, Indian, and Germanic languages)
            // Decomposed into "Main sound + h"
            {"pʰ", "ph"}, {"tʰ", "th"}, {"kʰ", "kh"}, {"cʰ", "ch"}, {"qʰ", "qh"},
            {"bʱ", "bh"}, {"dʱ", "dh"}, {"gʱ", "gh"},

            // Labialization (Rounded consonants)
            // Decomposed into "Main sound + w"
            {"kʷ", "kw"}, {"gʷ", "gw"}, {"xʷ", "xw"}, {"sʷ", "sw"}, {"zʷ", "zw"},

            // Nasalized Vowels (French, Polish, Portuguese)
            // Mapped to "Vowel + n" as the best approximation for nasal quality.
            {"ã", "an"}, {"ẽ", "en"}, {"ĩ", "in"}, {"õ", "on"}, {"ũ", "un"},
            {"ɛ̃", "ɛn"}, {"ɔ̃", "ɔn"}, {"œ̃", "œn"}, {"æ̃", "æn"},

            // Length marks (Gemination)
            // Doubling the sound if the model doesn't support the duration marker ː.
            {"aː", "aa"}, {"eː", "ee"}, {"iː", "ii"}, {"oː", "oo"}, {"uː", "uu"},
            {"sː", "ss"}, {"mː", "mm"}, {"nː", "nn"},

            // Velar Nasal (e.g., "ng" in "singing")
            {"ŋ", "ng"},

            // Syllabic consonants (e.g., "button", "bottle")
            // Decomposed into Schwa [ə] + consonant.
            {"n̩", "ən"}, {"l̩", "əl"}, {"m̩", "əm"},

            // Rhotic vowels (American "er" as in "bird")
            {"ɚ", "ər"}, {"ɝ", "ɜr"},

            // Diphthongs. These rules are activated only when the source sequence cannot
            // already be emitted directly by the loaded model.
            {"aɪ", "ai"}, {"aʊ", "au"}, {"eɪ", "ei"}, {"oʊ", "ou"}, {"ɔɪ", "ɔi"}
        };

    public PhonemeFallbackMapper(string csvPath, PiperConfig piperConfig)
    {
        _supportedModelPhonemes = piperConfig?.PhonemeIdMap != null
            ? new HashSet<string>(piperConfig.PhonemeIdMap.Keys, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        int maxSupportedLength = 1;
        foreach (string phoneme in _supportedModelPhonemes)
        {
            if (phoneme.Length > maxSupportedLength)
            {
                maxSupportedLength = phoneme.Length;
            }
        }

        _maxSupportedModelPhonemeLength = maxSupportedLength;

        if (!File.Exists(csvPath))
        {
            Console.WriteLine($"[WARNING] PHOIBLE database not found at '{csvPath}'. Phoneme fallback disabled.");
            return;
        }

        // 1. Load the phonetic feature database (PHOIBLE).
        var phoibleDb = LoadPhoibleDatabase(csvPath);

        // 2. Cross-reference it with the phonemes currently supported by the loaded Piper model.
        var supportedModelPhonemes = GetSupportedPhonemes(phoibleDb);

        // 3. Precalculate all ordinary nearest-neighbor fallbacks once at startup.
        PrecalculateAllFallbacks(phoibleDb, supportedModelPhonemes);

        // 4. Resolve complex IPA decomposition rules against the exact model inventory.
        // Rules that the model can already represent directly are deliberately skipped.
        PrecalculateSequenceFallbacks();
    }

    /// <summary>
    /// Attempts to resolve an active complex IPA sequence such as t͡s, pʲ, or ɛ̃.
    /// The returned value is already reduced to phonemes supported by the current model.
    /// </summary>
    public bool TryGetSequenceFallback(ReadOnlySpan<char> sequence, out string fallback)
    {
        var lookup = _sequenceFallbackMap.GetAlternateLookup<ReadOnlySpan<char>>();
        return lookup.TryGetValue(sequence, out fallback!);
    }

    /// <summary>
    /// Attempts to resolve one IPA phoneme through the precalculated PHOIBLE nearest-neighbor map.
    /// </summary>
    public bool TryGetClosestPhoneme(ReadOnlySpan<char> phoneme, out string fallback)
    {
        var lookup = _precalculatedMap.GetAlternateLookup<ReadOnlySpan<char>>();
        return lookup.TryGetValue(phoneme, out fallback!);
    }


    /// <summary>
    /// Loads phonemes and their corresponding feature vectors from the PHOIBLE CSV file.
    /// Feature vectors describe articulatory/phonological characteristics such as voicing,
    /// place, manner, nasality, and related properties.
    /// </summary>
    private static Dictionary<string, char[]> LoadPhoibleDatabase(string csvPath)
    {
        var db = new Dictionary<string, char[]>(StringComparer.Ordinal);

        using var enumerator = File.ReadLines(csvPath).GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return db;
        }

        string[] headers = ParseCsvLine(enumerator.Current);
        int phonemeIndex = Array.FindIndex(
            headers,
            h => h.Equals("phoneme", StringComparison.OrdinalIgnoreCase));

        int featuresStartIndex = Array.FindIndex(
            headers,
            h => h.Equals("tone", StringComparison.OrdinalIgnoreCase));

        if (phonemeIndex < 0 || featuresStartIndex < 0)
        {
            return db;
        }

        int featureCount = headers.Length - featuresStartIndex;

        while (enumerator.MoveNext())
        {
            string[] columns = ParseCsvLine(enumerator.Current);
            if (columns.Length <= phonemeIndex || columns.Length <= featuresStartIndex)
            {
                continue;
            }

            string phoneme = columns[phonemeIndex];
            if (string.IsNullOrWhiteSpace(phoneme) || db.ContainsKey(phoneme))
            {
                continue;
            }

            char[] featureVector = new char[featureCount];

            for (int i = 0; i < featureCount; i++)
            {
                int columnIndex = featuresStartIndex + i;

                if (columnIndex < columns.Length && columns[columnIndex].Length > 0)
                {
                    featureVector[i] = columns[columnIndex][0];
                    continue;
                }

                featureVector[i] = '0';
            }

            db[phoneme] = featureVector;
        }

        return db;
    }

    // Intersects PHOIBLE entries with the loaded Piper model inventory.
    private Dictionary<string, char[]> GetSupportedPhonemes(
        Dictionary<string, char[]> phoibleDb)
    {
        var supported = new Dictionary<string, char[]>(StringComparer.Ordinal);

        foreach (string phoneme in _supportedModelPhonemes)
        {
            if (phoibleDb.TryGetValue(phoneme, out char[]? featureVector))
            {
                supported[phoneme] = featureVector;
            }
        }

        return supported;
    }

    /// <summary>
    /// Performs a nearest-neighbor search for every phoneme in the PHOIBLE database.
    /// Uses Hamming distance over PHOIBLE feature vectors to find the supported model phoneme
    /// with the smallest phonetic-feature difference.
    /// </summary>
    private void PrecalculateAllFallbacks(
        Dictionary<string, char[]> phoibleDb,
        Dictionary<string, char[]> supportedModelPhonemes)
    {
        if (supportedModelPhonemes.Count == 0)
        {
            return;
        }

        Console.WriteLine("[INFO] Precalculating phoneme fallback map. This may take a moment...");

        foreach (var phoibleKvp in phoibleDb)
        {
            string unknownPhoneme = phoibleKvp.Key;
            char[] unknownVector = phoibleKvp.Value;

            string? bestMatch = null;
            int minDistance = int.MaxValue;

            foreach (var supportedKvp in supportedModelPhonemes)
            {
                int distance = CalculateHammingDistance(
                    unknownVector,
                    supportedKvp.Value,
                    minDistance);

                if (distance >= minDistance)
                {
                    continue;
                }

                minDistance = distance;
                bestMatch = supportedKvp.Key;

                if (minDistance == 0)
                {
                    break;
                }
            }

            if (!string.IsNullOrEmpty(bestMatch))
            {
                _precalculatedMap[unknownPhoneme] = bestMatch;

                // PHOIBLE also contains complex segments that span multiple Unicode units.
                // Register them for longest-match only when the model cannot already emit the
                // original components directly. Explicit decomposition rules are applied later
                // and may deliberately override this generic nearest-neighbor sequence fallback.
                ReadOnlySpan<char> source = unknownPhoneme.AsSpan();
                int firstUnitLength = GetPhonemeUnitLength(source);

                if (firstUnitLength < source.Length && !CanEmitDirectly(source))
                {
                    _sequenceFallbackMap[unknownPhoneme] = bestMatch;

                    if (unknownPhoneme.Length > MaxSequenceLength)
                    {
                        MaxSequenceLength = unknownPhoneme.Length;
                    }
                }
            }
        }

        Console.WriteLine($"[INFO] Successfully precalculated fallbacks for {_precalculatedMap.Count} phonemes.");
    }

    /// <summary>
    /// Resolves explicit complex-phoneme rules once at startup.
    /// A rule is activated only when the source sequence cannot already be emitted directly
    /// by the current model, preventing unnecessary degradation of supported IPA sequences.
    /// </summary>
    private void PrecalculateSequenceFallbacks()
    {
        if (_supportedModelPhonemes.Count == 0)
        {
            return;
        }

        foreach (var rule in DecompositionRules)
        {
            string source = rule.Key;

            // If every semantic unit in the source is already supported, preserve the original IPA.
            // This is particularly important for diphthongs such as aɪ and aʊ.
            if (CanEmitDirectly(source.AsSpan()))
            {
                continue;
            }

            if (!TryResolveToSupportedSequence(rule.Value.AsSpan(), out string resolved))
            {
                continue;
            }

            _sequenceFallbackMap[source] = resolved;

            if (source.Length > MaxSequenceLength)
            {
                MaxSequenceLength = source.Length;
            }
        }
    }

    // Checks whether a phoneme sequence can be emitted entirely from native model tokens.
    private bool CanEmitDirectly(ReadOnlySpan<char> sequence)
    {
        var supportedLookup = _supportedModelPhonemes.GetAlternateLookup<ReadOnlySpan<char>>();

        // Some Piper models expose genuine multi-character phoneme tokens. Preserve those
        // exactly instead of unnecessarily decomposing them into smaller IPA units.
        if (supportedLookup.Contains(sequence))
        {
            return true;
        }

        int index = 0;

        while (index < sequence.Length)
        {
            ReadOnlySpan<char> remaining = sequence[index..];

            if (TryFindLongestSupported(
                remaining,
                supportedLookup,
                out int supportedLength))
            {
                index += supportedLength;
                continue;
            }

            int unitLength = GetPhonemeUnitLength(remaining);
            ReadOnlySpan<char> unit = remaining[..unitLength];

            if (!supportedLookup.Contains(unit))
            {
                return false;
            }

            index += unitLength;
        }

        return true;
    }

    // Resolves a decomposition recursively until every emitted token is model-supported.
    private bool TryResolveToSupportedSequence(
        ReadOnlySpan<char> sequence,
        out string resolved)
    {
        var supportedLookup = _supportedModelPhonemes.GetAlternateLookup<ReadOnlySpan<char>>();
        var fallbackLookup = _precalculatedMap.GetAlternateLookup<ReadOnlySpan<char>>();
        var builder = new StringBuilder(sequence.Length + 4);
        int index = 0;

        while (index < sequence.Length)
        {
            ReadOnlySpan<char> remaining = sequence[index..];

            // Prefer the longest exact model token before reducing the sequence to IPA units.
            if (TryFindLongestSupported(
                remaining,
                supportedLookup,
                out int supportedLength))
            {
                builder.Append(remaining[..supportedLength]);
                index += supportedLength;
                continue;
            }

            int unitLength = GetPhonemeUnitLength(remaining);
            ReadOnlySpan<char> unit = remaining[..unitLength];

            if (supportedLookup.Contains(unit))
            {
                builder.Append(unit);
                index += unitLength;
                continue;
            }

            if (fallbackLookup.TryGetValue(unit, out string? fallback) &&
                !string.IsNullOrEmpty(fallback))
            {
                builder.Append(fallback);
                index += unitLength;
                continue;
            }

            resolved = string.Empty;
            return false;
        }

        resolved = builder.ToString();
        return resolved.Length > 0;
    }

    // Finds the longest supported model token at the beginning of a span.
    private bool TryFindLongestSupported(
        ReadOnlySpan<char> input,
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> supportedLookup,
        out int matchedLength)
    {
        int maxLength = Math.Min(
            _maxSupportedModelPhonemeLength,
            input.Length);

        for (int length = maxLength; length > 1; length--)
        {
            if (SplitsSurrogatePair(input, length))
            {
                continue;
            }

            if (!supportedLookup.Contains(input[..length]))
            {
                continue;
            }

            matchedLength = length;
            return true;
        }

        matchedLength = 0;
        return false;
    }

    // Checks whether a candidate UTF-16 length would split a surrogate pair.
    private static bool SplitsSurrogatePair(
        ReadOnlySpan<char> input,
        int length)
    {
        return length > 0 &&
               length < input.Length &&
               char.IsHighSurrogate(input[length - 1]) &&
               char.IsLowSurrogate(input[length]);
    }

    /// <summary>
    /// Calculates the Hamming distance between two PHOIBLE feature vectors.
    /// A lower value means fewer encoded phonetic-feature differences.
    /// </summary>
    private static int CalculateHammingDistance(
        char[] vec1,
        char[] vec2,
        int stopAt)
    {
        int distance = 0;
        int length = Math.Min(vec1.Length, vec2.Length);

        for (int i = 0; i < length; i++)
        {
            if (vec1[i] == vec2[i])
            {
                continue;
            }

            distance++;

            // Startup optimization: once this candidate cannot beat the current best match,
            // there is no reason to inspect the rest of its feature vector.
            if (distance >= stopAt)
            {
                return distance;
            }
        }

        distance += Math.Abs(vec1.Length - vec2.Length);
        return distance;
    }

    // Returns one IPA unit: one Unicode scalar plus any immediately following combining marks.
    // Complex modifier-letter sequences such as pʲ are intentionally NOT merged here;
    // they are handled by the explicit longest-match sequence map above.
    internal static int GetPhonemeUnitLength(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return 0;
        }

        OperationStatus firstStatus = Rune.DecodeFromUtf16(
            text,
            out _,
            out int length);

        if (firstStatus != OperationStatus.Done)
        {
            return 1;
        }

        while (length < text.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                text[length..],
                out Rune rune,
                out int consumed);

            if (status != OperationStatus.Done)
            {
                break;
            }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is not UnicodeCategory.NonSpacingMark and
                not UnicodeCategory.SpacingCombiningMark and
                not UnicodeCategory.EnclosingMark)
            {
                break;
            }

            length += consumed;
        }

        return length;
    }

    // Parses one PHOIBLE CSV row while preserving quoted fields.
    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var currentToken = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                // Escaped quote inside a quoted CSV field.
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    currentToken.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(currentToken.ToString().Trim());
                currentToken.Clear();
                continue;
            }

            currentToken.Append(c);
        }

        result.Add(currentToken.ToString().Trim());
        return [.. result];
    }
}
