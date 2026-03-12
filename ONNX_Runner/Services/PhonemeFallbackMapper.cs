using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public class PhonemeFallbackMapper
{
    // Готовий словник: Ключ = будь-який звук у світі, Значення = найближчий звук вашої моделі
    private readonly Dictionary<string, string> _precalculatedMap = new();

    private readonly Dictionary<string, string> _decompositionRules = new()
    {
        // Африкати з лігатурою (дужкою зверху)
        {"t͡s", "ts"}, {"t͡ʃ", "tʃ"}, {"d͡z", "dz"}, {"d͡ʒ", "dʒ"},
        // Африкати злиті (один символ Unicode)
        {"ʦ", "ts"},  {"ʧ", "tʃ"},  {"ʣ", "dz"},  {"ʤ", "dʒ"}
    };

    public PhonemeFallbackMapper(string csvPath, PiperConfig piperConfig)
    {
        if (!File.Exists(csvPath))
        {
            Console.WriteLine($"[WARNING] PHOIBLE database not found at '{csvPath}'. Phoneme fallback disabled.");
            return;
        }

        // Тимчасові словники для розрахунків при старті
        var phoibleDb = LoadPhoibleDatabase(csvPath);
        var supportedModelPhonemes = GetSupportedPhonemes(piperConfig, phoibleDb);

        // ОДРАЗУ РАХУЄМО ВСІ МОЖЛИВІ ЗАМІНИ
        PrecalculateAllFallbacks(phoibleDb, supportedModelPhonemes);
    }

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

    private void PrecalculateAllFallbacks(Dictionary<string, char[]> phoibleDb, Dictionary<string, char[]> supportedModelPhonemes)
    {
        if (supportedModelPhonemes.Count == 0) return;

        Console.WriteLine("[INFO] Precalculating phoneme fallback map. This may take a moment...");

        // Проходимо по ВСІХ 3142 звуках PHOIBLE
        foreach (var phoibleKvp in phoibleDb)
        {
            string unknownPhoneme = phoibleKvp.Key;
            char[] unknownVector = phoibleKvp.Value;

            string bestMatch = "";
            int minDistance = int.MaxValue;

            // Шукаємо для нього найближчого родича з доступних
            foreach (var supportedKvp in supportedModelPhonemes)
            {
                int distance = CalculateHammingDistance(unknownVector, supportedKvp.Value);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    bestMatch = supportedKvp.Key;
                }
            }

            // Зберігаємо готовий результат!
            _precalculatedMap[unknownPhoneme] = bestMatch;
        }

        Console.WriteLine($"[INFO] Successfully precalculated fallbacks for {_precalculatedMap.Count} phonemes.");
    }

    // Тепер цей метод миттєвий (O(1) завжди) і не містить математики!
    public string GetClosestPhoneme(string unknownPhoneme)
    {
        if (string.IsNullOrEmpty(unknownPhoneme)) return "";

        // Перевірка на розбиття (Африкати/Дифтонги)
        if (_decompositionRules.TryGetValue(unknownPhoneme, out string? decomposed))
        {
            return decomposed;
        }

        // Віддаємо готовий прорахований звук
        if (_precalculatedMap.TryGetValue(unknownPhoneme, out string? fallback))
        {
            return fallback ?? "";
        }

        return "";
    }

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
        return result.ToArray();
    }
}