using System.Text;
using System.Text.RegularExpressions;
using Lingua;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public record TextChunk
{
    public required string Text { get; init; }
    public required string DetectedLanguage { get; init; }
    public double Probability { get; init; }
    public bool IsReliable { get; init; }
    public bool IsPunctuationOrSpace { get; init; }
    public string Script { get; init; } = "None";
    public List<string> RawTop5 { get; init; } = [];
}

public partial class MixedLanguagePhonemizer
{
    [GeneratedRegex(@"([.,\-:!?;""«»()\[\]{}⟨⟩。！？]+)|([\p{L}\p{Nd}\p{M}]+(?:['’][\p{L}\p{Nd}\p{M}]+)*)|([^.,\-:!?;""«»()\[\]{}⟨⟩。！？\p{L}\p{Nd}\p{M}]+)")]
    private static partial Regex TokenizerRegex();

    public enum ScriptType { None, Latin, Cyrillic, Greek, Han, Hiragana, Katakana, Hangul, Arabic, Hebrew, Other }

    private readonly LanguageDetector _detector;
    private readonly EspeakLinguaMapper _mapper;
    private readonly string _modelEspeakCode;
    private readonly Language? _modelLinguaLang;

    // Зберігаємо налаштування бонусів
    private readonly double _maxBonus;
    private readonly int _minLimit;
    private readonly int _maxLimit;

    public MixedLanguagePhonemizer(PhonemizerSettings settings, string modelEspeakCode)
    {
        _mapper = new EspeakLinguaMapper();

        // Повний код (напр., "en-gb-x-rp", "nb", "pt-br") - зберігаємо для e-speak
        _modelEspeakCode = modelEspeakCode.Trim().ToLower();

        // РОБИМО SPLIT ТУТ: Обрізаний код (напр., "en", "nb", "pt") - використовуємо для Lingua
        string baseFamily = _modelEspeakCode.Split('-', '_')[0];

        // Передаємо обрізаний код у мапер Lingua
        _modelLinguaLang = _mapper.GetLinguaLanguage(baseFamily);

        // Зберігаємо параметри з конфігу (або залишаємо дефолтні)
        _maxBonus = settings?.MaxBonusMultiplier ?? 0.50;
        _minLimit = settings?.BonusMinLetterCount ?? 8;
        _maxLimit = settings?.BonusMaxLetterCount ?? 32;

        var codesToSupport = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (settings?.SupportedLanguages != null)
        {
            foreach (var code in settings.SupportedLanguages)
            {
                if (!string.IsNullOrWhiteSpace(code))
                    codesToSupport.Add(code.Trim());
            }
        }

        // Гарантовано додаємо в Lingua БАЗОВУ мову моделі (обрізану)
        codesToSupport.Add(baseFamily);

        var linguaLangs = _mapper.BuildLinguaList(codesToSupport);

        // ФОЛБЕК, ЯКЩО МАПЕР НЕ ВПІЗНАВ ЖОДНОЇ МОВИ
        if (linguaLangs.Length == 0)
        {
            Console.WriteLine($"[WARNING] Мапер не розпізнав жодної мови. Аварійний фолбек.");
            linguaLangs = _modelLinguaLang.HasValue ? [_modelLinguaLang.Value] : [Language.English];
        }

        Console.WriteLine($"[INFO] Preloading Lingua language models for {linguaLangs.Length} language(s)...");

        _detector = LanguageDetectorBuilder
            .FromLanguages(linguaLangs)
            .WithPreloadedLanguageModels()
            .Build();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[INFO] Lingua models loaded successfully.");
    }

    private static ScriptType DetectScript(string word)
    {
        if (MyRegex().IsMatch(word)) return ScriptType.Cyrillic;
        if (MyRegex1().IsMatch(word)) return ScriptType.Latin;
        if (MyRegex2().IsMatch(word)) return ScriptType.Greek;
        if (MyRegex3().IsMatch(word)) return ScriptType.Han;
        if (MyRegex4().IsMatch(word)) return ScriptType.Hiragana;
        if (MyRegex5().IsMatch(word)) return ScriptType.Katakana;
        if (MyRegex6().IsMatch(word)) return ScriptType.Hangul;
        if (MyRegex7().IsMatch(word)) return ScriptType.Arabic;
        if (MyRegex8().IsMatch(word)) return ScriptType.Hebrew;
        return ScriptType.Other;
    }

    public List<TextChunk> ProcessTextToLanguageTokens(string text)
    {
        var result = new List<TextChunk>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var matches = TokenizerRegex().Matches(text);
        var currentSubPhrase = new StringBuilder();
        ScriptType currentScript = ScriptType.None;
        bool hasWords = false;

        void FlushPhrase()
        {
            if (currentSubPhrase.Length > 0)
            {
                if (hasWords)
                {
                    ProcessSubPhrase(currentSubPhrase.ToString(), currentScript, result);
                }
                else
                {
                    result.Add(new TextChunk { Text = currentSubPhrase.ToString(), DetectedLanguage = "universal", Probability = 1.0, IsReliable = true, IsPunctuationOrSpace = true, Script = "None" });
                }
                currentSubPhrase.Clear();
                currentScript = ScriptType.None;
                hasWords = false;
            }
        }

        foreach (Match match in matches)
        {
            string val = match.Value;

            if (match.Groups[1].Success)
            {
                FlushPhrase();
                result.Add(new TextChunk { Text = val, DetectedLanguage = "universal", Probability = 1.0, IsReliable = true, IsPunctuationOrSpace = true, Script = "None" });
            }
            else if (match.Groups[2].Success)
            {
                ScriptType wordScript = DetectScript(val);
                if (currentScript != ScriptType.None && currentScript != wordScript)
                {
                    FlushPhrase();
                }

                currentScript = wordScript;
                hasWords = true;
                currentSubPhrase.Append(val);
            }
            else if (match.Groups[3].Success)
            {
                currentSubPhrase.Append(val);
            }
        }

        FlushPhrase();
        return result;
    }

    private void ProcessSubPhrase(string text, ScriptType script, List<TextChunk> result)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        string cleanText = text.Trim();

        // --- ДИНАМІЧНИЙ МНОЖНИК З КОНФІГУ ---
        int letterCount = cleanText.Count(char.IsLetter);
        double currentMultiplier = 1.0;

        if (letterCount <= _minLimit)
        {
            currentMultiplier = 1.0 + _maxBonus;
        }
        else if (letterCount < _maxLimit)
        {
            // Лінійна інтерполяція від (1.0 + MaxBonus) до 1.0
            double ratio = (double)(_maxLimit - letterCount) / (_maxLimit - _minLimit);
            currentMultiplier = 1.0 + (_maxBonus * ratio);
        }
        else
        {
            currentMultiplier = 1.0;
        }

        var confidences = _detector.ComputeLanguageConfidenceValues(cleanText);

        var rawTop5 = confidences
            .Take(5)
            .Select(kvp => $"{kvp.Key}: {kvp.Value:0.0000}")
            .ToList();

        Language bestLinguaLang = Language.Unknown;
        double bestAdjustedScore = -1;
        double originalProbabilityOfBest = 0;

        foreach (var kvp in confidences)
        {
            Language lang = kvp.Key;
            double score = kvp.Value;

            // БОНУС: Збільшуємо шанс мови моделі за плавною шкалою
            if (_modelLinguaLang.HasValue && lang == _modelLinguaLang.Value)
            {
                score *= currentMultiplier;
            }

            if (score > bestAdjustedScore)
            {
                bestAdjustedScore = score;
                bestLinguaLang = lang;
                originalProbabilityOfBest = kvp.Value;
            }
        }

        // ЗВОРОТНИЙ МАПІНГ: Lingua -> e-speak
        string finalEspeakCode = _modelEspeakCode;
        if (bestLinguaLang != Language.Unknown)
        {
            finalEspeakCode = _mapper.MapBackToEspeak(bestLinguaLang, _modelEspeakCode);
        }

        result.Add(new TextChunk
        {
            Text = text,
            DetectedLanguage = finalEspeakCode,
            Probability = originalProbabilityOfBest,
            IsReliable = originalProbabilityOfBest > 0.5,
            IsPunctuationOrSpace = false,
            Script = script.ToString(),
            RawTop5 = rawTop5
        });
    }

    [GeneratedRegex(@"\p{IsCyrillic}")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"[a-zA-Z\u00C0-\u024F\u1E00-\u1EFF]")]
    private static partial Regex MyRegex1();
    [GeneratedRegex(@"\p{IsGreek}")]
    private static partial Regex MyRegex2();
    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}")]
    private static partial Regex MyRegex3();
    [GeneratedRegex(@"\p{IsHiragana}")]
    private static partial Regex MyRegex4();
    [GeneratedRegex(@"\p{IsKatakana}")]
    private static partial Regex MyRegex5();
    [GeneratedRegex(@"\p{IsHangulSyllables}")]
    private static partial Regex MyRegex6();
    [GeneratedRegex(@"\p{IsArabic}")]
    private static partial Regex MyRegex7();
    [GeneratedRegex(@"\p{IsHebrew}")]
    private static partial Regex MyRegex8();
}