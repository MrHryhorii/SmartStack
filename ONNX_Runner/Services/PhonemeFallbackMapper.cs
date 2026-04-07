using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public class PhonemeFallbackMapper
{
    // Готовий словник: Ключ = будь-який звук у світі, Значення = найближчий звук вашої моделі
    private readonly Dictionary<string, string> _precalculatedMap = [];

    private readonly Dictionary<string, string> _decompositionRules = new()
    {
        // Африкати (Лігатури та злиті символи)
        {"t͡s", "ts"}, {"t͡ʃ", "tʃ"}, {"d͡z", "dz"}, {"d͡ʒ", "dʒ"},
        {"ʦ", "ts"},  {"ʧ", "tʃ"},  {"ʣ", "dz"},  {"ʤ", "dʒ"},
        {"p͡f", "pf"}, {"k͡x", "kx"}, {"t͡ɕ", "tɕ"}, {"d͡ʑ", "dʑ"},

        // Палаталізація (М'які приголосні - важливо для слов'янських мов)
        // Розкладаємо на "твердий звук + йот [j]"
        {"pʲ", "pj"}, {"bʲ", "bj"}, {"mʲ", "mj"}, {"fʲ", "fj"}, {"vʲ", "vj"},
        {"tʲ", "tj"}, {"dʲ", "dj"}, {"nʲ", "nj"}, {"lʲ", "lj"}, {"rʲ", "rj"},
        {"sʲ", "sj"}, {"zʲ", "zj"}, {"kʲ", "kj"}, {"gʲ", "gj"}, {"xʲ", "xj"},

        // Аспірація (Придих - азіатські, індійські, германські мови)
        // Розкладаємо на "звук + h"
        {"pʰ", "ph"}, {"tʰ", "th"}, {"kʰ", "kh"}, {"cʰ", "ch"}, {"qʰ", "qh"},
        {"bʱ", "bh"}, {"dʱ", "dh"}, {"gʱ", "gh"},

        // Лабіалізація (Огублені приголосні)
        // Розкладаємо на "звук + w"
        {"kʷ", "kw"}, {"gʷ", "gw"}, {"xʷ", "xw"}, {"sʷ", "sw"}, {"zʷ", "zw"},

        // Назалізовані голосні (Французька, польська, португальська)
        // Розкладаємо на "голосна + n" (найкращий фолбек для імітації "в ніс")
        {"ã", "an"}, {"ẽ", "en"}, {"ĩ", "in"}, {"õ", "on"}, {"ũ", "un"},
        {"ɛ̃", "ɛn"}, {"ɔ̃", "ɔn"}, {"œ̃", "œn"}, {"æ̃", "æn"},

        // Довгі голосні та приголосні
        // Модель може не розуміти знак довготи ː, тому просто дублюємо звук
        {"aː", "aa"}, {"eː", "ee"}, {"iː", "ii"}, {"oː", "oo"}, {"uː", "uu"},
        {"sː", "ss"}, {"mː", "mm"}, {"nː", "nn"},

        // Велярний носовий (як "ng" у going, sing)
        // Розкладаємо на "n" + "g" (найкраща імітація для слов'янських/європейських моделей)
        {"ŋ", "ng"}, 

        // Складотворчі приголосні (Syllabic consonants, як у button, bottle)
        // Розкладаємо на нейтральний звук "ə" (шва) + приголосний
        {"n̩", "ən"}, {"l̩", "əl"}, {"m̩", "əm"},

        // Ротизовані голосні (Американське "er", як у water, bird)
        // Розкладаємо на нейтральний "ə" + "r"
        {"ɚ", "ər"}, {"ɝ", "ɜr"}, 

        // Злиті дифтонги (якщо eSpeak видав їх як один символ, а модель не знає)
        {"aɪ", "ai"}, {"aʊ", "au"}, {"eɪ", "ei"}, {"oʊ", "ou"}, {"ɔɪ", "ɔi"}
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

        // ПЕРЕВІРКА СКЛАДНИХ ЗВУКІВ (напр. "t͡s")
        if (_decompositionRules.TryGetValue(unknownPhoneme, out string? decomposed))
        {
            var safeResult = new System.Text.StringBuilder();

            // Розбиваємо "ts" на "t" та "s" і перевіряємо кожен окремо
            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(decomposed);
            while (enumerator.MoveNext())
            {
                string part = enumerator.GetTextElement();

                // Якщо модель знає цю частину — додаємо. 
                // Якщо ні — беремо її найближчий аналог із уже прорахованої карти.
                if (_precalculatedMap.TryGetValue(part, out string? partFallback))
                {
                    // Додаємо або саму частину (якщо вона ідеальна), або її заміну
                    safeResult.Append(!string.IsNullOrEmpty(partFallback) ? partFallback : part);
                }
            }
            return safeResult.ToString();
        }

        // ДЛЯ ВСІХ ІНШИХ ЗВУКІВ
        // Просто повертаємо вже готовий результат із кешу, який ми розрахували при старті
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