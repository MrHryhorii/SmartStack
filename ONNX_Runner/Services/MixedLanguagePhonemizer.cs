using System.Text;
using System.Text.RegularExpressions;
using Lingua;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Represents an isolated segment of text along with its predicted language and metadata.
/// </summary>
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

/// <summary>
/// Advanced NLP (Natural Language Processing) module for handling mixed-language input.
/// It tokenizes text, detects writing scripts (e.g., Latin vs. Cyrillic), and uses 
/// statistical analysis (Lingua) to predict the language of each chunk, allowing the TTS 
/// to switch phonetic rules dynamically (e.g., reading an English quote inside a Ukrainian text).
/// </summary>
public partial class MixedLanguagePhonemizer
{
    // Regex splits the input into three groups: 
    // 1. Punctuation/Spaces
    // 2. Words (including letters, numbers, marks, and internal apostrophes)
    // 3. Unrecognized garbage/symbols
    [GeneratedRegex(@"([.,\-:!?;""«»()\[\]{}⟨⟩。！？]+)|([\p{L}\p{Nd}\p{M}]+(?:['’][\p{L}\p{Nd}\p{M}]+)*)|([^.,\-:!?;""«»()\[\]{}⟨⟩。！？\p{L}\p{Nd}\p{M}]+)")]
    private static partial Regex TokenizerRegex();

    public enum ScriptType { None, Latin, Cyrillic, Greek, Han, Hiragana, Katakana, Hangul, Arabic, Hebrew, Other }

    private readonly LanguageDetector _detector;
    private readonly EspeakLinguaMapper _mapper;
    private readonly string _modelEspeakCode;
    private readonly Language? _modelLinguaLang;

    // Cache for dynamic bonus multiplier settings
    private readonly double _maxBonus;
    private readonly int _minLimit;
    private readonly int _maxLimit;

    public MixedLanguagePhonemizer(PhonemizerSettings settings, string modelEspeakCode)
    {
        _mapper = new EspeakLinguaMapper();

        // Store the full eSpeak dialect code (e.g., "en-gb-x-rp", "pt-br")
        _modelEspeakCode = modelEspeakCode.Trim().ToLower();

        // SPLIT: Extract the base language family (e.g., "en", "pt") to use with the Lingua library
        string baseFamily = _modelEspeakCode.Split('-', '_')[0];

        // Pass the base code to the mapper to get the strongly-typed Lingua Enum
        _modelLinguaLang = _mapper.GetLinguaLanguage(baseFamily);

        // Load bonus configuration (or fallback to safe defaults)
        _maxBonus = settings?.MaxBonusMultiplier ?? 0.50;
        _minLimit = settings?.BonusMinLetterCount ?? 8;
        _maxLimit = settings?.BonusMaxLetterCount ?? 32;

        var codesToSupport = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Load user-defined supported languages to optimize memory 
        // (Lingua takes a lot of RAM if loading all 75 languages)
        if (settings?.SupportedLanguages != null)
        {
            foreach (var code in settings.SupportedLanguages)
            {
                if (!string.IsNullOrWhiteSpace(code))
                    codesToSupport.Add(code.Trim());
            }
        }

        // Guarantee that the base language of the loaded TTS model is ALWAYS supported
        codesToSupport.Add(baseFamily);

        var linguaLangs = _mapper.BuildLinguaList(codesToSupport);

        // FALLBACK: If the mapper failed to recognize any languages, default to the model's base or English
        if (linguaLangs.Length == 0)
        {
            Console.WriteLine($"[WARNING] Mapper could not recognize any languages. Using emergency fallback.");
            linguaLangs = _modelLinguaLang.HasValue ? [_modelLinguaLang.Value] : [Language.English];
        }

        Console.WriteLine($"[INFO] Preloading Lingua language models for {linguaLangs.Length} language(s)...");

        _detector = LanguageDetectorBuilder
            .FromLanguages(linguaLangs)
            .WithPreloadedLanguageModels()
            .Build();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[INFO] Lingua models loaded successfully.");
        Console.ResetColor();
    }

    /// <summary>
    /// Detects the Unicode script of a word. A change in script (e.g., from Latin to Cyrillic) 
    /// is a guaranteed hard boundary indicating a language switch.
    /// </summary>
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

    /// <summary>
    /// Chunks the text into segments based on punctuation and script changes, 
    /// returning a sequence of tokens ready for language prediction.
    /// </summary>
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
                    // Punctuation and spaces are universal, they don't have a specific language
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
                // Hard boundary: flush if the writing system changes
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

    /// <summary>
    /// Analyzes a chunk of text to determine its language, applying statistical confidence 
    /// weighting to prevent random language switching on short, ambiguous words.
    /// </summary>
    private void ProcessSubPhrase(string text, ScriptType script, List<TextChunk> result)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        string cleanText = text.Trim();

        // --- DYNAMIC CONFIDENCE MULTIPLIER ---
        // Problem: Short words (e.g., "no", "да", "hi") are statistically ambiguous and often misidentified by Lingua.
        // Solution: We apply an artificial confidence bonus to the base TTS model's language depending on the word length.
        // Short text gets max bonus. Long text gets zero bonus (trusting the detector completely).
        int letterCount = cleanText.Count(char.IsLetter);
        double currentMultiplier = 1.0;

        if (letterCount <= _minLimit)
        {
            currentMultiplier = 1.0 + _maxBonus;
        }
        else if (letterCount < _maxLimit)
        {
            // Linear interpolation from (1.0 + MaxBonus) down to 1.0
            double ratio = (double)(_maxLimit - letterCount) / (_maxLimit - _minLimit);
            currentMultiplier = 1.0 + (_maxBonus * ratio);
        }
        else
        {
            currentMultiplier = 1.0;
        }

        // Get statistical confidences from the ML detector
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

            // BONUS: Apply the calculated multiplier to the model's native language to prevent false language hopping
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

        // REVERSE MAPPING: Convert the detected Lingua Enum back to an eSpeak string code
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

    // --- Script Detection Regexes ---
    [GeneratedRegex(@"\p{IsCyrillic}")]
    private static partial Regex MyRegex();         // Cyrillic
    [GeneratedRegex(@"[a-zA-Z\u00C0-\u024F\u1E00-\u1EFF]")]
    private static partial Regex MyRegex1();        // Latin (including diacritics/accents)
    [GeneratedRegex(@"\p{IsGreek}")]
    private static partial Regex MyRegex2();        // Greek
    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}")]
    private static partial Regex MyRegex3();        // Han (Chinese Characters)
    [GeneratedRegex(@"\p{IsHiragana}")]
    private static partial Regex MyRegex4();        // Hiragana (Japanese)
    [GeneratedRegex(@"\p{IsKatakana}")]
    private static partial Regex MyRegex5();        // Katakana (Japanese)
    [GeneratedRegex(@"\p{IsHangulSyllables}")]
    private static partial Regex MyRegex6();        // Hangul (Korean)
    [GeneratedRegex(@"\p{IsArabic}")]
    private static partial Regex MyRegex7();        // Arabic
    [GeneratedRegex(@"\p{IsHebrew}")]
    private static partial Regex MyRegex8();        // Hebrew
}