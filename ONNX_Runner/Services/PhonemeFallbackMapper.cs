using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Intelligent phonetic fallback system. 
/// It maps unknown phonemes (not supported by the current Piper model) to the closest 
/// available phonetic relatives using the PHOIBLE database and Hamming distance logic.
/// This prevents "silent" gaps or errors when the TTS encounters rare or foreign sounds.
/// </summary>
public class PhonemeFallbackMapper
{
    // High-speed lookup map: Key = any global phoneme (IPA), Value = closest supported model phoneme
    private readonly Dictionary<string, string> _precalculatedMap = [];

    // Rules to break down complex IPA symbols into simpler characters that the model is more likely to understand.
    private readonly Dictionary<string, string> _decompositionRules = new()
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
        // Mapped to "Vowel + n" as the best approximation for "nasal" quality
        {"ã", "an"}, {"ẽ", "en"}, {"ĩ", "in"}, {"õ", "on"}, {"ũ", "un"},
        {"ɛ̃", "ɛn"}, {"ɔ̃", "ɔn"}, {"œ̃", "œn"}, {"æ̃", "æn"},

        // Length marks (Gemination)
        // Doubling the sound if the model doesn't support the duration marker ː
        {"aː", "aa"}, {"eː", "ee"}, {"iː", "ii"}, {"oː", "oo"}, {"uː", "uu"},
        {"sː", "ss"}, {"mː", "mm"}, {"nː", "nn"},

        // Velar Nasal (e.g., "ng" in "singing")
        {"ŋ", "ng"}, 

        // Syllabic consonants (e.g., "button", "bottle")
        // Decomposed into Schwa [ə] + consonant
        {"n̩", "ən"}, {"l̩", "əl"}, {"m̩", "əm"},

        // Rhotic vowels (American "er" as in "bird")
        {"ɚ", "ər"}, {"ɝ", "ɜr"}, 

        // Diphthongs (Simplified if the single-symbol ligature isn't supported)
        {"aɪ", "ai"}, {"aʊ", "au"}, {"eɪ", "ei"}, {"oʊ", "ou"}, {"ɔɪ", "ɔi"}
    };

    public PhonemeFallbackMapper(string csvPath, PiperConfig piperConfig)
    {
        if (!File.Exists(csvPath))
        {
            Console.WriteLine($"[WARNING] PHOIBLE database not found at '{csvPath}'. Phoneme fallback disabled.");
            return;
        }

        // 1. Load the phonetic feature database (PHOIBLE)
        var phoibleDb = LoadPhoibleDatabase(csvPath);

        // 2. Cross-reference it with the phonemes currently supported by the loaded Piper model
        var supportedModelPhonemes = GetSupportedPhonemes(piperConfig, phoibleDb);

        // 3. Precalculate all possible fallbacks at startup to ensure O(1) performance during synthesis
        PrecalculateAllFallbacks(phoibleDb, supportedModelPhonemes);
    }

    /// <summary>
    /// Loads phonemes and their corresponding binary feature vectors from the PHOIBLE CSV file.
    /// Feature vectors describe articulatory characteristics (voiced, labial, nasal, etc.).
    /// </summary>
    private static Dictionary<string, char[]> LoadPhoibleDatabase(string csvPath)
    {
        var db = new Dictionary<string, char[]>();
        string[] lines = File.ReadAllLines(csvPath);
        if (lines.Length < 2) return db;

        string[] headers = ParseCsvLine(lines[0]);
        int phonemeIndex = Array.FindIndex(headers, h => h.Equals("phoneme", StringComparison.OrdinalIgnoreCase));
        int featuresStartIndex = Array.FindIndex(headers, h => h.Equals("tone", StringComparison.OrdinalIgnoreCase));

        if (phonemeIndex == -1 || featuresStartIndex == -1) return db;

        for (int i = 1; i < lines.Length; i++)
        {
            string[] columns = ParseCsvLine(lines[i]);
            if (columns.Length <= featuresStartIndex) continue;

            string phoneme = columns[phonemeIndex];
            if (string.IsNullOrWhiteSpace(phoneme) || db.ContainsKey(phoneme)) continue;

            int featureCount = headers.Length - featuresStartIndex;
            char[] featureVector = new char[featureCount];

            for (int j = 0; j < featureCount; j++)
            {
                if (featuresStartIndex + j < columns.Length)
                {
                    string val = columns[featuresStartIndex + j];
                    featureVector[j] = val.Length > 0 ? val[0] : '0';
                }
                else
                {
                    featureVector[j] = '0';
                }
            }

            db[phoneme] = featureVector;
        }
        return db;
    }

    private static Dictionary<string, char[]> GetSupportedPhonemes(PiperConfig config, Dictionary<string, char[]> phoibleDb)
    {
        var supported = new Dictionary<string, char[]>();
        if (config?.PhonemeIdMap == null) return supported;

        foreach (var key in config.PhonemeIdMap.Keys)
        {
            if (phoibleDb.TryGetValue(key, out var featureVector))
            {
                supported[key] = featureVector;
            }
        }
        return supported;
    }

    /// <summary>
    /// Performs a nearest-neighbor search for every phoneme in the PHOIBLE database.
    /// Uses Hamming distance to find which supported model phoneme is the most "acoustically similar".
    /// </summary>
    private void PrecalculateAllFallbacks(Dictionary<string, char[]> phoibleDb, Dictionary<string, char[]> supportedModelPhonemes)
    {
        if (supportedModelPhonemes.Count == 0) return;

        Console.WriteLine("[INFO] Precalculating phoneme fallback map. This may take a moment...");

        foreach (var phoibleKvp in phoibleDb)
        {
            string unknownPhoneme = phoibleKvp.Key;
            char[] unknownVector = phoibleKvp.Value;

            string bestMatch = "";
            int minDistance = int.MaxValue;

            foreach (var supportedKvp in supportedModelPhonemes)
            {
                int distance = CalculateHammingDistance(unknownVector, supportedKvp.Value);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    bestMatch = supportedKvp.Key;
                }
            }

            _precalculatedMap[unknownPhoneme] = bestMatch;
        }

        Console.WriteLine($"[INFO] Successfully precalculated fallbacks for {_precalculatedMap.Count} phonemes.");
    }

    /// <summary>
    /// Returns the closest supported phoneme for any given IPA input.
    /// This method is O(1) as it uses the precalculated cache.
    /// </summary>
    public string GetClosestPhoneme(string unknownPhoneme)
    {
        if (string.IsNullOrEmpty(unknownPhoneme)) return "";

        // CHECK DECOMPOSITION RULES (e.g., "t͡s")
        if (_decompositionRules.TryGetValue(unknownPhoneme, out string? decomposed))
        {
            var safeResult = new System.Text.StringBuilder();

            // Break "ts" into "t" and "s", then verify each separately
            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(decomposed);
            while (enumerator.MoveNext())
            {
                string part = enumerator.GetTextElement();

                // If the model knows this component, use it. 
                // Otherwise, take the closest match from the precalculated map.
                if (_precalculatedMap.TryGetValue(part, out string? partFallback))
                {
                    safeResult.Append(!string.IsNullOrEmpty(partFallback) ? partFallback : part);
                }
            }
            return safeResult.ToString();
        }

        // STANDARD LOOKUP (Cached)
        if (_precalculatedMap.TryGetValue(unknownPhoneme, out string? fallback))
        {
            return fallback ?? "";
        }

        return "";
    }

    /// <summary>
    /// Calculates the Hamming distance between two binary feature vectors.
    /// A lower distance represents higher phonetic similarity.
    /// </summary>
    private static int CalculateHammingDistance(char[] vec1, char[] vec2)
    {
        int distance = 0;
        int length = Math.Min(vec1.Length, vec2.Length);
        for (int i = 0; i < length; i++)
        {
            if (vec1[i] != vec2[i]) distance++;
        }
        distance += Math.Abs(vec1.Length - vec2.Length);
        return distance;
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var currentToken = new System.Text.StringBuilder();

        foreach (char c in line)
        {
            if (c == '\"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                result.Add(currentToken.ToString().Trim());
                currentToken.Clear();
            }
            else currentToken.Append(c);
        }
        result.Add(currentToken.ToString().Trim());
        return [.. result];
    }
}