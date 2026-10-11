using System.Buffers;
using System.Globalization;
using System.Text;
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
    public IReadOnlyList<string> RawTop5 { get; init; } = Array.Empty<string>();

    // True if the text is a pre-formatted raw phoneme block. Explicit [[...]] blocks always
    // qualify; ambiguous [...] and /.../ forms are admitted only after IPA validation upstream.
    // Skips language detection and eSpeak, going straight to fallback validation.
    public bool IsRawPhonemes { get; init; }

    // Structural syntax is preserved until native character-name pronunciation.
    public bool IsTechnical { get; init; }
}

/// <summary>
/// Advanced NLP (Natural Language Processing) module for handling mixed-language input.
/// It tokenizes text, detects writing scripts (e.g., Latin vs. Cyrillic), and uses 
/// statistical analysis (Lingua) to predict the language of each chunk, allowing the TTS 
/// to switch phonetic rules dynamically (e.g., reading an English quote inside a Ukrainian text).
/// </summary>
public partial class MixedLanguagePhonemizer
{
    /// <summary>Whether this instance loaded Lingua for statistical comparison.</summary>
    public bool UsesStatisticalDetection => _detector != null;

    /// <summary>Distinct recognized candidates after model inclusion and alias deduplication.</summary>
    public IReadOnlyList<string> DetectionLanguages { get; private set; } = Array.Empty<string>();

    // Tokenizer classification is intentionally implemented with a single ReadOnlySpan<char>
    // scan instead of Regex.Matches(). This removes MatchCollection / Match / match.Value
    // allocations from the hot path while keeping structural roles explicit: protected technical
    // spans, silent quote boundaries, soft punctuation, lexical words, and neutral symbols/spaces.
    //
    // Lexical hyphens are accepted only when surrounded by word characters because they are part
    // of the word itself (e.g., "state-of-the-art", "будь-який", Hebrew maqaf compounds).
    // Typographic dashes (figure/en/em/horizontal bar) remain punctuation even without spaces,
    // so constructions such as "word—word" still create a soft language-boundary candidate.

    // Diagnostic collections are immutable-by-contract and cached once for the entire process.
    // Production requests therefore do not allocate tiny List<string> instances for metadata.
    private static readonly IReadOnlyList<string> ForcedLanguageDiagnostic =
        ["Forced by API"];

    private static readonly IReadOnlyList<string> StrongLanguageHintDiagnostic =
        ["strong script-language hint"];

    private static readonly IReadOnlyList<string> ScriptFallbackDiagnostic =
        ["script fallback"];

    private static readonly IReadOnlyList<string> ModelLanguageFallbackDiagnostic =
        ["model language fallback"];

    private static readonly IReadOnlyList<string> ScriptCapabilityRouteDiagnostic =
        ["script capability route"];

    private static readonly IReadOnlyList<string> TechnicalContextDiagnostic =
        ["technical context"];

    public enum ScriptType
    {
        None,
        Latin,
        Cyrillic,
        Greek,
        Han,
        Hiragana,
        Katakana,
        Hangul,
        Arabic,
        Hebrew,
        Armenian,
        Bengali,
        Devanagari,
        Georgian,
        Gujarati,
        Gurmukhi,
        Odia,
        Tamil,
        Telugu,
        Kannada,
        Malayalam,
        Sinhala,
        Thai,
        Myanmar,
        Other
    }

    // One sentence-wide language verdict used only as context for short sub-phrases.
    // The statistical detector is still allowed to fall back to chunk-level analysis when this
    // verdict is weak, while incompatible writing systems always bypass it.
    private readonly record struct SentenceLanguageContext(
        Language Language,
        string EspeakCode,
        double Confidence);

    // Ultimate script-level emergency fallback for when both Lingua and strong script-language hints
    // find ZERO signal (e.g. Cyrillic text on a server with only Latin models loaded). 
    // Direct eSpeak codes, not Lingua enums, so this works even if the fallback language 
    // isn't in SupportedLanguages. None/Other stay unmapped.
    private static readonly Dictionary<ScriptType, string> ScriptFallbackLanguage = new()
    {
        { ScriptType.Latin, "en" },
        { ScriptType.Cyrillic, "uk" },
        { ScriptType.Greek, "el" },
        { ScriptType.Han, "cmn" },
        { ScriptType.Hiragana, "ja" },
        { ScriptType.Katakana, "ja" },
        { ScriptType.Hangul, "ko" },
        { ScriptType.Arabic, "ar" },
        { ScriptType.Hebrew, "he" },
        { ScriptType.Armenian, "hy" },
        { ScriptType.Bengali, "bn" },
        { ScriptType.Devanagari, "hi" },
        { ScriptType.Georgian, "ka" },
        { ScriptType.Gujarati, "gu" },
        { ScriptType.Gurmukhi, "pa" },
        { ScriptType.Odia, "or" },
        { ScriptType.Tamil, "ta" },
        { ScriptType.Telugu, "te" },
        { ScriptType.Kannada, "kn" },
        { ScriptType.Malayalam, "ml" },
        { ScriptType.Sinhala, "si" },
        { ScriptType.Thai, "th" },
        { ScriptType.Myanmar, "my" },
    };

    // Strong script-language hints used when Lingua is absent or produced no usable signal.
    // Each subphrase has already been classified by Unicode script, so this table performs one
    // cheap IndexOfAny over a small script-specific set. The markers are deliberately conservative:
    // they need not be mathematically unique, but must strongly narrow the most likely language.
    // If no marker is present, ScriptFallbackLanguage provides the final coarse degradation.
    //
    // Turkish: dotless "ı" uppercases to plain "I", which would misfire on virtually every
    // capitalized Latin word — deliberately NOT mapped by itself. Its dotted capital "İ" (U+0130)
    // and other strong Turkish letters remain useful hints. Arabic-script letters have no case.
    private static readonly Dictionary<ScriptType, (SearchValues<char> Chars, Dictionary<char, string> Map)> StrongLanguageHintsByScript = new()
    {
        [ScriptType.Cyrillic] = (
            SearchValues.Create("їЇєЄґҐ" + "ўЎ" + "эЭ" + "ѝЍ" + "әӘғҒқҚңҢұҰһҺ" + "ѓЃѕЅќЌ" + "ђЂћЋ" + "јЈљЉњЊџЏ"),
            new Dictionary<char, string>
            {
                ['ї'] = "uk",
                ['Ї'] = "uk",
                ['є'] = "uk",
                ['Є'] = "uk",
                ['ґ'] = "uk",
                ['Ґ'] = "uk",
                ['ў'] = "be",
                ['Ў'] = "be",
                // Russian-specific strong signal. Hard sign (ъ) is deliberately excluded
                // because it is also a very common Bulgarian vowel letter.
                ['э'] = "ru",
                ['Э'] = "ru",
                // Bulgarian-specific I with grave.
                ['ѝ'] = "bg",
                ['Ѝ'] = "bg",
                // Strong Kazakh letters among the languages supported by the detector.
                // ө/ү are deliberately excluded because Mongolian shares them.
                ['ә'] = "kk",
                ['Ә'] = "kk",
                ['ғ'] = "kk",
                ['Ғ'] = "kk",
                ['қ'] = "kk",
                ['Қ'] = "kk",
                ['ң'] = "kk",
                ['Ң'] = "kk",
                ['ұ'] = "kk",
                ['Ұ'] = "kk",
                ['һ'] = "kk",
                ['Һ'] = "kk",
                // Exclusive to Macedonian (not in Serbian):
                ['ѓ'] = "mk",
                ['Ѓ'] = "mk",
                ['ѕ'] = "mk",
                ['Ѕ'] = "mk",
                ['ќ'] = "mk",
                ['Ќ'] = "mk",
                // Exclusive to Serbian (not in Macedonian):
                ['ђ'] = "sr",
                ['Ђ'] = "sr",
                ['ћ'] = "sr",
                ['Ћ'] = "sr",
                // Shared by Serbian AND Macedonian (not in ru/uk/be) — no single-language
                // signal, so default to "sr" (larger population) absent one of the exclusive
                // letters above also being present in the same word.
                ['ј'] = "sr",
                ['Ј'] = "sr",
                ['љ'] = "sr",
                ['Љ'] = "sr",
                ['њ'] = "sr",
                ['Њ'] = "sr",
                ['џ'] = "sr",
                ['Џ'] = "sr",
            }
        ),
        [ScriptType.Latin] = (
            SearchValues.Create(
                "øØåÅ" +
                "ßẞ" +
                "łŁąĄęĘśŚźŹżŻ" +
                "řŘěĚůŮ" +
                "ľĽĺĹŕŔ" +
                "ģĢķĶļĻņŅ" +
                "ėĖįĮųŲ" +
                "őŐűŰ" +
                "șȘțȚ" +
                "ơƠưƯ" +
                "ĉĈĝĜĥĤĵĴŝŜŭŬ" +
                "ŵŴŷŶ" +
                "ığİĞ" +
                "þÞðÐ" +
                "œŒ" +
                "ħĦ"),
            new Dictionary<char, string>
            {
                // Norwegian/Danish overlap; keep the existing Bokmål-biased degradation.
                ['ø'] = "nb",
                ['Ø'] = "nb",
                ['å'] = "nb",
                ['Å'] = "nb",
                ['ß'] = "de",
                ['ẞ'] = "de",

                ['ł'] = "pl",
                ['Ł'] = "pl",
                ['ą'] = "pl",
                ['Ą'] = "pl",
                ['ę'] = "pl",
                ['Ę'] = "pl",
                ['ś'] = "pl",
                ['Ś'] = "pl",
                ['ź'] = "pl",
                ['Ź'] = "pl",
                ['ż'] = "pl",
                ['Ż'] = "pl",

                ['ř'] = "cs",
                ['Ř'] = "cs",
                ['ě'] = "cs",
                ['Ě'] = "cs",
                ['ů'] = "cs",
                ['Ů'] = "cs",

                ['ľ'] = "sk",
                ['Ľ'] = "sk",
                ['ĺ'] = "sk",
                ['Ĺ'] = "sk",
                ['ŕ'] = "sk",
                ['Ŕ'] = "sk",

                ['ģ'] = "lv",
                ['Ģ'] = "lv",
                ['ķ'] = "lv",
                ['Ķ'] = "lv",
                ['ļ'] = "lv",
                ['Ļ'] = "lv",
                ['ņ'] = "lv",
                ['Ņ'] = "lv",

                ['ė'] = "lt",
                ['Ė'] = "lt",
                ['į'] = "lt",
                ['Į'] = "lt",
                ['ų'] = "lt",
                ['Ų'] = "lt",

                ['ő'] = "hu",
                ['Ő'] = "hu",
                ['ű'] = "hu",
                ['Ű'] = "hu",
                ['ș'] = "ro",
                ['Ș'] = "ro",
                ['ț'] = "ro",
                ['Ț'] = "ro",
                ['ơ'] = "vi",
                ['Ơ'] = "vi",
                ['ư'] = "vi",
                ['Ư'] = "vi",

                ['ĉ'] = "eo",
                ['Ĉ'] = "eo",
                ['ĝ'] = "eo",
                ['Ĝ'] = "eo",
                ['ĥ'] = "eo",
                ['Ĥ'] = "eo",
                ['ĵ'] = "eo",
                ['Ĵ'] = "eo",
                ['ŝ'] = "eo",
                ['Ŝ'] = "eo",
                ['ŭ'] = "eo",
                ['Ŭ'] = "eo",

                ['ŵ'] = "cy",
                ['Ŵ'] = "cy",
                ['ŷ'] = "cy",
                ['Ŷ'] = "cy",

                ['ı'] = "tr",
                ['İ'] = "tr",
                ['ğ'] = "tr",
                ['Ğ'] = "tr",
                ['þ'] = "is",
                ['Þ'] = "is",
                ['ð'] = "is",
                ['Ð'] = "is",
                ['œ'] = "fr",
                ['Œ'] = "fr",

                // Maltese is not a Lingua language in the current mapper, but eSpeak supports it.
                ['ħ'] = "mt",
                ['Ħ'] = "mt",
            }
        ),
        [ScriptType.Arabic] = (
            // پ/چ/گ are shared by Persian and Urdu, so they are intentionally not used as
            // single-character language decisions. Keep only substantially stronger hints.
            SearchValues.Create("ژۀٹڈڑںھہےۂۓ"),
            new Dictionary<char, string>
            {
                ['ژ'] = "fa",
                ['ۀ'] = "fa",

                ['ٹ'] = "ur",
                ['ڈ'] = "ur",
                ['ڑ'] = "ur",
                ['ں'] = "ur",
                ['ھ'] = "ur",
                ['ہ'] = "ur",
                ['ے'] = "ur",
                ['ۂ'] = "ur",
                ['ۓ'] = "ur",
            }
        ),
        [ScriptType.Devanagari] = (
            // Marathi strongly favors retroflex lateral /ळ/ and /ऱ/ compared with Hindi.
            SearchValues.Create("ळऱ"),
            new Dictionary<char, string>
            {
                ['ळ'] = "mr",
                ['ऱ'] = "mr",
            }
        ),
        [ScriptType.Bengali] = (
            // Assamese shares the Bengali script, but these two letters are strong Assamese signals.
            // eSpeak supports Assamese directly even though Lingua does not participate in this tier.
            SearchValues.Create("ৰৱ"),
            new Dictionary<char, string>
            {
                ['ৰ'] = "as",
                ['ৱ'] = "as",
            }
        ),
    };

    private readonly LanguageDetector? _detector;
    private readonly EspeakLinguaMapper _mapper;
    private readonly string _modelEspeakCode;

    /// <summary>
    /// Pre-split segments of the loaded model's eSpeak code (cached at startup to prevent allocations in hot loops).
    /// </summary>
    private readonly string[] _modelEspeakParts;

    private readonly Language? _modelLinguaLang;

    // Primary writing system of the loaded model language. This is cached once and used only
    // to gate the short-text bonus toward the model language. Sentence-wide context now carries
    // its own detected language and performs an independent script-compatibility check.
    private readonly ScriptType _modelScript;

    // Cache for dynamic bonus multiplier settings
    private readonly double _maxBonus;
    private readonly int _minLimit;
    private readonly int _maxLimit;
    private readonly int _foreignValidationMaxLetters;

    // Cache for sentence-context override settings
    private readonly double _overrideThreshold;
    private readonly int _minSentenceLength;

    // Local Lingua evidence is authoritative only when the winner meets the configured
    // confidence and runner-up margin. Ambiguous tiny fragments can still
    // inherit sentence/model context; convincing short foreign inserts are protected from it.
    private readonly double _localWinnerProbabilityFloor;
    private readonly double _localWinnerMarginFloor;
    private readonly double _reliabilityProbabilityThreshold;

    private readonly TextChunker _textChunker;
    private readonly TextChunkerRules _rules;

    // logger
    private readonly ILogger<MixedLanguagePhonemizer> _logger;

    public MixedLanguagePhonemizer(
        PhonemizerSettings settings,
        string modelEspeakCode,
        ILogger<MixedLanguagePhonemizer> logger,
        TextChunker? textChunker = null)
    {
        settings ??= new PhonemizerSettings();
        _logger = logger;
        _textChunker = textChunker ?? new TextChunker(new ChunkerSettings());
        _rules = _textChunker.Rules;

        _mapper = new EspeakLinguaMapper();

        // Store the full eSpeak dialect code (e.g., "en-gb-x-rp", "pt-br")
        _modelEspeakCode = modelEspeakCode.Trim().ToLowerInvariant();

        // Pre-cache model code segments once to optimize performance in hot execution paths
        _modelEspeakParts = _modelEspeakCode.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);

        // SPLIT: Extract the base language family (e.g., "en", "pt") to use with the Lingua library
        // from the already cached segments instead of splitting the same code twice.
        string baseFamily = _modelEspeakParts.Length > 0 ? _modelEspeakParts[0] : _modelEspeakCode;

        // Pass the base code to the mapper to get the strongly-typed Lingua Enum
        _modelLinguaLang = _mapper.GetLinguaLanguage(baseFamily);
        _modelScript = GetPrimaryScriptForLanguage(_modelLinguaLang);

        // Cache tuning once; omitted JSON values use the PhonemizerSettings defaults.
        _maxBonus = settings.MaxBonusMultiplier;
        _minLimit = settings.BonusMinLetterCount;
        _maxLimit = settings.BonusMaxLetterCount;
        _foreignValidationMaxLetters = Math.Max(0, settings.ForeignValidationMaxLetters);
        _overrideThreshold = settings.MixedLanguageOverrideThreshold;
        _minSentenceLength = settings.MinSentenceLengthForOverride;
        _localWinnerProbabilityFloor = settings.LocalWinnerProbabilityFloor;
        _localWinnerMarginFloor = settings.LocalWinnerMarginFloor;
        _reliabilityProbabilityThreshold = settings.ReliabilityProbabilityThreshold;


        var finalCodesToSupport = new List<string>();
        var seenBaseFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Guarantee that the base language of the loaded TTS model is ALWAYS supported.
        // It occupies the slot for its base family first, blocking any user-defined dialect duplicates.
        finalCodesToSupport.Add(_modelEspeakCode);
        seenBaseFamilies.Add(baseFamily);

        // Load user-defined supported languages to optimize memory.
        // Lingua takes a lot of RAM if loading all 75 languages.
        if (settings.SupportedLanguages != null)
        {
            foreach (var code in settings.SupportedLanguages)
            {
                if (string.IsNullOrWhiteSpace(code)) continue;

                string cleanCode = code.Trim();
                string currentBaseFamily = GetBaseFamily(cleanCode);

                // Add the dialect only if its base language hasn't been claimed yet
                if (seenBaseFamilies.Add(currentBaseFamily))
                {
                    finalCodesToSupport.Add(cleanCode);
                }
            }
        }

        var linguaLangs = _mapper.BuildLinguaList(finalCodesToSupport);
        DetectionLanguages = Array.AsReadOnly(linguaLangs
            .Select(language => _mapper.MapBackToEspeak(language, _modelEspeakCode))
            .ToArray());

        // Statistical comparison needs at least two distinct recognized languages. Script routing
        // and model-language fallback remain available without loading any Lingua models.
        if (linguaLangs.Length < 2)
        {
            Console.WriteLine($"[INFO] {linguaLangs.Length} recognized language candidate(s); using model/script routing without Lingua.");
            return;
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
    /// Detects the Unicode script of a word using Unicode scalar values rather than UTF-16
    /// code units. This preserves the allocation-free span path while correctly handling
    /// supplementary-plane letters such as CJK Extension characters.
    /// </summary>
    private static ScriptType DetectScript(ReadOnlySpan<char> word)
    {
        foreach (Rune rune in word.EnumerateRunes())
        {
            if (!IsLetterRune(rune))
            {
                continue;
            }

            ScriptType script = DetectScript(rune);
            if (script != ScriptType.Other)
            {
                return script;
            }
        }

        return ScriptType.Other;
    }

    // Returns the Unicode script associated with a single decoded rune.
    private static ScriptType DetectScript(Rune rune)
    {
        int value = rune.Value;

        if (IsInRange(value, 0x0041, 0x005A) ||
            IsInRange(value, 0x0061, 0x007A) ||
            IsInRange(value, 0x00C0, 0x024F) ||
            IsInRange(value, 0x1E00, 0x1EFF) ||
            IsInRange(value, 0xA720, 0xA7FF) ||
            IsInRange(value, 0xAB30, 0xAB6F) ||
            IsInRange(value, 0x10780, 0x107BF) ||
            IsInRange(value, 0x1DF00, 0x1DFFF))
        {
            return ScriptType.Latin;
        }

        if (IsInRange(value, 0x0400, 0x052F) ||
            IsInRange(value, 0x1C80, 0x1C8F) ||
            IsInRange(value, 0x2DE0, 0x2DFF) ||
            IsInRange(value, 0xA640, 0xA69F))
        {
            return ScriptType.Cyrillic;
        }

        if (IsInRange(value, 0x0370, 0x03FF) ||
            IsInRange(value, 0x1F00, 0x1FFF))
        {
            return ScriptType.Greek;
        }

        if (IsInRange(value, 0x0530, 0x058F))
        {
            return ScriptType.Armenian;
        }

        if (IsInRange(value, 0x0900, 0x097F) ||
            IsInRange(value, 0xA8E0, 0xA8FF))
        {
            return ScriptType.Devanagari;
        }

        if (IsInRange(value, 0x0980, 0x09FF))
        {
            return ScriptType.Bengali;
        }

        if (IsInRange(value, 0x0A00, 0x0A7F))
        {
            return ScriptType.Gurmukhi;
        }

        if (IsInRange(value, 0x0A80, 0x0AFF))
        {
            return ScriptType.Gujarati;
        }

        if (IsInRange(value, 0x0B00, 0x0B7F))
        {
            return ScriptType.Odia;
        }

        if (IsInRange(value, 0x0B80, 0x0BFF))
        {
            return ScriptType.Tamil;
        }

        if (IsInRange(value, 0x0C00, 0x0C7F))
        {
            return ScriptType.Telugu;
        }

        if (IsInRange(value, 0x0C80, 0x0CFF))
        {
            return ScriptType.Kannada;
        }

        if (IsInRange(value, 0x0D00, 0x0D7F))
        {
            return ScriptType.Malayalam;
        }

        if (IsInRange(value, 0x0D80, 0x0DFF))
        {
            return ScriptType.Sinhala;
        }

        if (IsInRange(value, 0x0E00, 0x0E7F))
        {
            return ScriptType.Thai;
        }

        if (IsInRange(value, 0x1000, 0x109F) ||
            IsInRange(value, 0xA9E0, 0xA9FF) ||
            IsInRange(value, 0xAA60, 0xAA7F))
        {
            return ScriptType.Myanmar;
        }

        if (IsInRange(value, 0x10A0, 0x10FF) ||
            IsInRange(value, 0x1C90, 0x1CBF) ||
            IsInRange(value, 0x2D00, 0x2D2F))
        {
            return ScriptType.Georgian;
        }

        if (IsInRange(value, 0x3400, 0x4DBF) ||
            IsInRange(value, 0x4E00, 0x9FFF) ||
            IsInRange(value, 0xF900, 0xFAFF) ||
            IsInRange(value, 0x20000, 0x2A6DF) ||
            IsInRange(value, 0x2A700, 0x2B73F) ||
            IsInRange(value, 0x2B740, 0x2B81F) ||
            IsInRange(value, 0x2B820, 0x2CEAF) ||
            IsInRange(value, 0x2CEB0, 0x2EBEF) ||
            IsInRange(value, 0x2F800, 0x2FA1F) ||
            IsInRange(value, 0x30000, 0x323AF))
        {
            return ScriptType.Han;
        }

        if (IsInRange(value, 0x3040, 0x309F) ||
            IsInRange(value, 0x1B001, 0x1B11F))
        {
            return ScriptType.Hiragana;
        }

        if (IsInRange(value, 0x30A0, 0x30FF) ||
            IsInRange(value, 0x31F0, 0x31FF) ||
            IsInRange(value, 0xFF65, 0xFF9F) ||
            value == 0x1B000 ||
            IsInRange(value, 0x1B120, 0x1B12F) ||
            IsInRange(value, 0x1AFF0, 0x1AFFF))
        {
            return ScriptType.Katakana;
        }

        if (IsInRange(value, 0x1100, 0x11FF) ||
            IsInRange(value, 0x3130, 0x318F) ||
            IsInRange(value, 0xA960, 0xA97F) ||
            IsInRange(value, 0xAC00, 0xD7AF) ||
            IsInRange(value, 0xD7B0, 0xD7FF))
        {
            return ScriptType.Hangul;
        }

        if (IsInRange(value, 0x0600, 0x06FF) ||
            IsInRange(value, 0x0750, 0x077F) ||
            IsInRange(value, 0x0870, 0x089F) ||
            IsInRange(value, 0x08A0, 0x08FF) ||
            IsInRange(value, 0xFB50, 0xFDFF) ||
            IsInRange(value, 0xFE70, 0xFEFF) ||
            IsInRange(value, 0x1EE00, 0x1EEFF))
        {
            return ScriptType.Arabic;
        }

        if (IsInRange(value, 0x0590, 0x05FF) ||
            IsInRange(value, 0xFB1D, 0xFB4F))
        {
            return ScriptType.Hebrew;
        }

        return ScriptType.Other;
    }

    // Checks whether a Unicode scalar falls inside an inclusive range.
    private static bool IsInRange(int value, int min, int max)
    {
        return value >= min && value <= max;
    }

    /// <summary>
    /// Determines whether a lexical hyphen is joining two incompatible writing systems.
    /// Letterless fragments are treated as neutral and skipped while looking for the nearest
    /// real scripts, preserving compounds such as COVID-19, B-52, and date-like tokens while
    /// still splitting constructions such as English-123-українська at the script boundary.
    /// </summary>
    private bool ShouldSplitAtLexicalHyphen(ReadOnlySpan<char> word, int hyphenIndex)
    {
        ScriptType leftScript = FindNearestLetterScriptLeft(word, hyphenIndex);
        ScriptType rightScript = FindNearestLetterScriptRight(word, hyphenIndex);

        if (leftScript == ScriptType.None || rightScript == ScriptType.None)
        {
            return false;
        }

        return !AreScriptsCompatible(leftScript, rightScript);
    }

    // Finds the nearest letter script before a lexical hyphen.
    private ScriptType FindNearestLetterScriptLeft(ReadOnlySpan<char> word, int hyphenIndex)
    {
        int segmentEnd = hyphenIndex;

        while (segmentEnd > 0)
        {
            int segmentStart = segmentEnd - 1;

            while (segmentStart >= 0 && !IsLexicalHyphen(word[segmentStart]))
            {
                segmentStart--;
            }

            ReadOnlySpan<char> segment = word[(segmentStart + 1)..segmentEnd];
            if (ContainsLetter(segment))
            {
                return DetectScript(segment);
            }

            segmentEnd = segmentStart;
        }

        return ScriptType.None;
    }

    // Finds the nearest letter script after a lexical hyphen.
    private ScriptType FindNearestLetterScriptRight(ReadOnlySpan<char> word, int hyphenIndex)
    {
        int segmentStart = hyphenIndex + 1;

        while (segmentStart < word.Length)
        {
            int segmentEnd = segmentStart;

            while (segmentEnd < word.Length && !IsLexicalHyphen(word[segmentEnd]))
            {
                segmentEnd++;
            }

            ReadOnlySpan<char> segment = word[segmentStart..segmentEnd];
            if (ContainsLetter(segment))
            {
                return DetectScript(segment);
            }

            segmentStart = segmentEnd + 1;
        }

        return ScriptType.None;
    }

    // Determines whether adjacent scripts may remain in the same lexical run.
    private static bool AreScriptsCompatible(ScriptType left, ScriptType right)
    {
        if (left == right)
        {
            return true;
        }

        // Hiragana and Katakana are both handled by the Japanese eSpeak frontend and can safely
        // remain in one run. Han is intentionally NOT compatible here: eSpeak's Japanese frontend
        // cannot resolve Kanji readings reliably, so Han must be routed separately through Mandarin.
        return IsJapaneseKanaScript(left) && IsJapaneseKanaScript(right);
    }

    /// <summary>
    /// Returns the primary script associated with the loaded model language.
    /// Languages whose scripts are not represented by ScriptType remain Other, which deliberately
    /// disables script gating instead of making an unsafe assumption.
    /// </summary>
    private static ScriptType GetPrimaryScriptForLanguage(Language? language)
    {
        if (!language.HasValue)
        {
            return ScriptType.None;
        }

        return language.Value switch
        {
            Language.Belarusian or
            Language.Bulgarian or
            Language.Kazakh or
            Language.Macedonian or
            Language.Mongolian or
            Language.Russian or
            Language.Serbian or
            Language.Ukrainian => ScriptType.Cyrillic,

            Language.Greek => ScriptType.Greek,
            Language.Chinese or Language.Japanese => ScriptType.Han,
            Language.Korean => ScriptType.Hangul,
            Language.Arabic or Language.Persian or Language.Urdu => ScriptType.Arabic,
            Language.Hebrew => ScriptType.Hebrew,
            Language.Armenian => ScriptType.Armenian,
            Language.Bengali => ScriptType.Bengali,
            Language.Georgian => ScriptType.Georgian,
            Language.Gujarati => ScriptType.Gujarati,
            Language.Hindi or Language.Marathi => ScriptType.Devanagari,
            Language.Punjabi => ScriptType.Gurmukhi,
            Language.Tamil => ScriptType.Tamil,
            Language.Telugu => ScriptType.Telugu,
            Language.Thai => ScriptType.Thai,

            // All remaining Lingua languages currently supported by EspeakLinguaMapper use Latin
            // as their primary script.
            _ => ScriptType.Latin
        };
    }

    /// <summary>
    /// Checks only whether it is safe to favor the loaded model language for this chunk.
    /// A mismatch blocks sentence override and the short-text model-language bonus, then lets the
    /// ordinary detector / letter fallback / script fallback choose the actual language.
    /// </summary>
    private bool IsModelScriptCompatible(ScriptType chunkScript)
    {
        // Unknown/unclassified scripts cannot be compared safely, so preserve existing behavior.
        if (_modelScript is ScriptType.None or ScriptType.Other ||
            chunkScript is ScriptType.None or ScriptType.Other)
        {
            return true;
        }

        // Only Kana can safely use the Japanese eSpeak frontend without an external Kanji G2P
        // dictionary. Han is capability-routed to Mandarin before statistical language detection.
        if (_modelLinguaLang == Language.Japanese)
        {
            return IsJapaneseKanaScript(chunkScript);
        }

        // Serbian is routinely written in both Cyrillic and Latin.
        if (_modelLinguaLang == Language.Serbian)
        {
            return chunkScript is ScriptType.Cyrillic or ScriptType.Latin;
        }

        return chunkScript == _modelScript;
    }

    // Returns true for Hiragana or Katakana.
    private static bool IsJapaneseKanaScript(ScriptType script)
    {
        return script is ScriptType.Hiragana or ScriptType.Katakana;
    }

    // Checks whether the span contains at least one Unicode letter.
    private static bool ContainsLetter(ReadOnlySpan<char> value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (IsLetterRune(rune))
            {
                return true;
            }
        }

        return false;
    }

    // Uses the shared catalog for hyphens that may participate in a word token.
    private bool IsLexicalHyphen(char value)
    {
        return _rules.LexicalHyphens.Contains(value);
    }

    // Extracts the base language family from an eSpeak language code.
    private static string GetBaseFamily(string languageCode)
    {
        int separator = languageCode.AsSpan().IndexOfAny('-', '_');

        return separator >= 0
            ? languageCode[..separator]
            : languageCode;
    }

    // Returns the diagnostic name for a detected script.
    private static string GetScriptName(ScriptType script)
    {
        // Enum.ToString() may allocate. The set is fixed, so return cached string literals instead.
        return script switch
        {
            ScriptType.Latin => "Latin",
            ScriptType.Cyrillic => "Cyrillic",
            ScriptType.Greek => "Greek",
            ScriptType.Han => "Han",
            ScriptType.Hiragana => "Hiragana",
            ScriptType.Katakana => "Katakana",
            ScriptType.Hangul => "Hangul",
            ScriptType.Arabic => "Arabic",
            ScriptType.Hebrew => "Hebrew",
            ScriptType.Armenian => "Armenian",
            ScriptType.Bengali => "Bengali",
            ScriptType.Devanagari => "Devanagari",
            ScriptType.Georgian => "Georgian",
            ScriptType.Gujarati => "Gujarati",
            ScriptType.Gurmukhi => "Gurmukhi",
            ScriptType.Odia => "Odia",
            ScriptType.Tamil => "Tamil",
            ScriptType.Telugu => "Telugu",
            ScriptType.Kannada => "Kannada",
            ScriptType.Malayalam => "Malayalam",
            ScriptType.Sinhala => "Sinhala",
            ScriptType.Thai => "Thai",
            ScriptType.Myanmar => "Myanmar",
            ScriptType.Other => "Other",
            _ => "None"
        };
    }

    // Counts Unicode letters without allocating a normalized copy.
    private static int CountLetters(ReadOnlySpan<char> value)
    {
        int count = 0;

        foreach (Rune rune in value.EnumerateRunes())
        {
            if (IsLetterRune(rune))
            {
                count++;
            }
        }

        return count;
    }

    // Counts speakable letters and records the dominant script for a span.
    private static void AnalyzeSpeakableContent(
        ReadOnlySpan<char> value,
        out bool hasLetters,
        out bool hasDecimalDigits)
    {
        hasLetters = false;
        hasDecimalDigits = false;

        foreach (Rune rune in value.EnumerateRunes())
        {
            if (IsLetterRune(rune))
            {
                hasLetters = true;

                if (hasDecimalDigits)
                {
                    return;
                }

                continue;
            }

            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
            {
                hasDecimalDigits = true;

                if (hasLetters)
                {
                    return;
                }
            }
        }
    }

    // Returns true when the rune is a Unicode letter.
    private static bool IsLetterRune(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter;
    }

    // Returns true when the rune can participate in the core of a word token.
    private static bool IsWordCoreRune(Rune rune)
    {
        int value = rune.Value;

        // ASCII dominates many API payloads. Keep the common path branch-only.
        if ((value >= 'A' && value <= 'Z') ||
            (value >= 'a' && value <= 'z') ||
            (value >= '0' && value <= '9'))
        {
            return true;
        }

        if (value <= 0x7F)
        {
            return false;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    // Decodes the first Unicode scalar and returns its UTF-16 width.
    private static int DecodeRune(ReadOnlySpan<char> value, out Rune rune)
    {
        OperationStatus status = Rune.DecodeFromUtf16(
            value,
            out rune,
            out int consumed);

        if (status == OperationStatus.Done)
        {
            return consumed;
        }

        // Invalid UTF-16 must never stall the tokenizer. Preserve one code unit as garbage
        // and continue; valid text remains entirely on the normal Rune path.
        rune = Rune.ReplacementChar;
        return 1;
    }

    // Returns true for period-like forms that may terminate a written abbreviation.
    private bool IsPeriodLike(char value)
    {
        return _rules.PeriodLikeMarks.Contains(value);
    }

    // Protects titles, initials, and dotted abbreviations from becoming independent language chunks.
    // Delegate period ambiguity to the same classifier used for audio sentence boundaries.
    private bool ShouldKeepAbbreviationPeriod(ReadOnlySpan<char> text, int periodIndex)
    {
        return _textChunker.IsIntraSentencePeriod(text, periodIndex);
    }

    // Quotes are analysis boundaries but never output punctuation.
    private bool IsVisualQuote(Rune rune)
    {
        return rune.Value <= char.MaxValue &&
               _rules.VisualQuotes.Contains((char)rune.Value);
    }

    // Checks whether a sentence-wide language verdict can safely apply to this script.
    private static bool IsLanguageScriptCompatible(Language language, ScriptType chunkScript)
    {
        if (chunkScript is ScriptType.None or ScriptType.Other)
        {
            return true;
        }

        if (language == Language.Japanese)
        {
            return chunkScript is ScriptType.Han or ScriptType.Hiragana or ScriptType.Katakana;
        }

        if (language == Language.Serbian)
        {
            return chunkScript is ScriptType.Cyrillic or ScriptType.Latin;
        }

        ScriptType languageScript = GetPrimaryScriptForLanguage(language);
        if (languageScript is ScriptType.None or ScriptType.Other)
        {
            return true;
        }

        return languageScript == chunkScript ||
               (IsJapaneseKanaScript(languageScript) && IsJapaneseKanaScript(chunkScript));
    }

    // Checks whether a character is an apostrophe, period, or lexical hyphen allowed inside a word.
    private bool IsWordConnector(char value)
    {
        return _rules.LexicalApostrophes.Contains(value) ||
            _rules.PeriodLikeMarks.Contains(value) || IsLexicalHyphen(value);
    }

    // Returns true for punctuation that may form a soft language-detection boundary.
    private bool IsBoundaryPunctuation(Rune rune)
    {
        // Quotes are handled separately as silent hard analysis boundaries. All remaining
        // punctuation stays audible/structural and becomes a soft language-detection boundary.
        if (IsVisualQuote(rune))
        {
            return false;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    // Estimates the initial token-list capacity for an input length.
    private static int EstimateResultCapacity(int textLength)
    {
        // Most TTS chunks contain several words per TextChunk. A small bounded estimate
        // avoids repeated List<T> growth without reserving excessive memory for long input.
        return Math.Clamp((textLength / 32) + 4, 4, 64);
    }

    // Formats the highest-confidence language candidates for debug diagnostics.
    private static IReadOnlyList<string> BuildTopDiagnostics(
        IEnumerable<KeyValuePair<Language, double>> confidences)
    {
        var rawTop5 = new List<string>(5);

        foreach (var confidence in confidences)
        {
            rawTop5.Add($"{confidence.Key}: {confidence.Value:0.0000}");

            if (rawTop5.Count == 5)
            {
                break;
            }
        }

        return rawTop5;
    }

    /// <summary>
    /// Resolves a client-supplied forced language code against a model's eSpeak dialect code,
    /// applying "Smart Inheritance": a generic/broader code (e.g. "en") that is a prefix of the
    /// model's dialect (e.g. "en-us") inherits the model's full specific dialect string.
    /// </summary>
    internal static string ResolveForcedLanguageCode(
        string forcedLanguage,
        string modelEspeakCode,
        string[] modelEspeakParts)
    {
        ReadOnlySpan<char> forced = forcedLanguage.AsSpan().Trim();

        if (forced.IsEmpty)
        {
            return modelEspeakCode;
        }

        int partIndex = 0;
        int segmentStart = 0;
        bool isGenericPrefix = true;

        for (int i = 0; i <= forced.Length; i++)
        {
            bool isEnd = i == forced.Length;
            bool isSeparator = !isEnd && forced[i] is '-' or '_';

            if (!isEnd && !isSeparator)
            {
                continue;
            }

            ReadOnlySpan<char> segment = forced[segmentStart..i];
            segmentStart = i + 1;

            // Mirrors StringSplitOptions.RemoveEmptyEntries from the previous implementation.
            if (segment.IsEmpty)
            {
                continue;
            }

            if (partIndex >= modelEspeakParts.Length ||
                !segment.Equals(modelEspeakParts[partIndex], StringComparison.OrdinalIgnoreCase))
            {
                isGenericPrefix = false;
                break;
            }

            partIndex++;
        }

        isGenericPrefix &= partIndex > 0 && partIndex <= modelEspeakParts.Length;

        // If the request is a broader version of the model's language, inherit model specifics.
        // Otherwise, respect the client's explicit dialect choice. The common inheritance path
        // returns the cached model string without allocating a normalized forced-language copy.
        return isGenericPrefix
            ? modelEspeakCode
            : forced.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Chunks one semantic sentence into language-detection candidates. Script changes are hard
    /// boundaries; punctuation is a soft candidate unless protected as lexical/technical syntax.
    /// 
    /// If a forced language is provided:
    /// - Bypasses the heavy Lingua statistical detector entirely to save CPU resources.
    /// - Applies "Smart Inheritance": if the requested language is a broader prefix of the loaded 
    ///   model's dialect (e.g. requesting "en" on an "en-us" model), it inherits the model's specific 
    ///   details. If a completely different dialect or language is requested, it trusts the API request directly.
    /// - Preserves full tokenization so punctuation boundaries and spacing remain intact for downstream phonemization.
    /// </summary>
    public List<TextChunk> ProcessTextToLanguageTokens(
        string text,
        string? forcedLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<TextChunk>(EstimateResultCapacity(text.Length));

        // FORCED LANGUAGE RESOLUTION WITH SMART INHERITANCE
        string? resolvedForcedCode = null;
        if (!string.IsNullOrWhiteSpace(forcedLanguage))
        {
            resolvedForcedCode = ResolveForcedLanguageCode(
                forcedLanguage,
                _modelEspeakCode,
                _modelEspeakParts);
        }

        // SENTENCE-LEVEL CONTEXT DETECTOR
        // One heavy Lingua pass determines the strongest language of the complete semantic sentence.
        // A confident verdict stabilizes ambiguous same-script phrases after their local evidence
        // has been checked; weak sentences fall through to phrase-level analysis.
        SentenceLanguageContext? sentenceContext = null;
        string sentenceAnalysis = resolvedForcedCode == null && _detector != null
            ? GetSentenceAnalysisText(text)
            : text;
        if (resolvedForcedCode == null && _detector != null &&
            CountLetters(sentenceAnalysis.AsSpan()) >= _minSentenceLength)
        {
            var sentenceConfidences = _detector.ComputeLanguageConfidenceValues(sentenceAnalysis);
            Language bestSentenceLanguage = Language.Unknown;
            double bestSentenceConfidence = -1;

            foreach (var confidence in sentenceConfidences)
            {
                if (confidence.Value <= bestSentenceConfidence)
                {
                    continue;
                }

                bestSentenceLanguage = confidence.Key;
                bestSentenceConfidence = confidence.Value;
            }

            if (bestSentenceLanguage != Language.Unknown && bestSentenceConfidence >= 0)
            {
                sentenceContext = new SentenceLanguageContext(
                    bestSentenceLanguage,
                    _mapper.MapBackToEspeak(bestSentenceLanguage, _modelEspeakCode),
                    bestSentenceConfidence);

                if (_logger.IsEnabled(LogLevel.Debug) &&
                    bestSentenceConfidence < _overrideThreshold)
                {
                    _logger.LogDebug(
                        "[LANG-DEBUG] Sentence context → {Code} | conf={Conf:0.0000} < threshold={Threshold:0.0000}; using chunk detection",
                        sentenceContext.Value.EspeakCode,
                        bestSentenceConfidence,
                        _overrideThreshold);
                }
            }
        }

        // TEXT TOKENIZATION
        // A single span scan replaces Regex.Matches() here. It preserves the original logical
        // token groups while avoiding MatchCollection, Match, and match.Value allocations.
        var currentSubPhrase = new StringBuilder(Math.Min(Math.Max(text.Length, 16), 256));
        ScriptType currentScript = ScriptType.None;
        bool hasLetters = false;
        bool hasSpeakableContent = false;
        bool insideQuote = false;
        int analysisBoundaryIndex = 0;
        List<(int Offset, string Text)>? technicalParts = null;

        // Technical parts occupy neutral spaces in the analysis phrase. Their original text is
        // retained separately for speech, so they neither vote in Lingua nor split surrounding prose.
        // Only punctuation, quote boundaries, and incompatible scripts finish an analysis phrase.
        void FlushPhrase()
        {
            if (currentSubPhrase.Length == 0)
            {
                return;
            }

            string phrase = currentSubPhrase.ToString();
            TextChunk decision;

            if (hasLetters)
            {
                // LANGUAGE ASSIGNMENT
                if (resolvedForcedCode != null)
                {
                    // FAST PATH: Language is forced. Bypass Lingua and assign the resolved code directly.
                    decision = new TextChunk
                    {
                        Text = phrase,
                        DetectedLanguage = resolvedForcedCode,
                        Probability = 1.0,
                        IsReliable = true,
                        IsPunctuationOrSpace = false,
                        Script = GetScriptName(currentScript),
                        RawTop5 = ForcedLanguageDiagnostic
                    };
                }
                else
                {
                    // AUTOMATIC PATH: Use available local evidence and model/script fallback.
                    decision = ProcessSubPhrase(
                        phrase,
                        currentScript,
                        insideQuote ? null : sentenceContext);
                }
            }
            else if (technicalParts is { Count: > 0 })
            {
                decision = ResolveTechnicalContext(technicalParts[0].Text, currentScript);
            }
            else if (hasSpeakableContent)
            {
                // Digits are language-neutral for detection but still need a language for pronunciation.
                // Standalone numeric content therefore bypasses Lingua and inherits the forced language
                // when present, otherwise the loaded model's native eSpeak language.
                decision = new TextChunk
                {
                    Text = phrase,
                    DetectedLanguage = resolvedForcedCode ?? _modelEspeakCode,
                    Probability = 1.0,
                    IsReliable = true,
                    IsPunctuationOrSpace = false,
                    Script = "None"
                };
            }
            else
            {
                // Punctuation, spaces, and non-pronounceable symbols are universal.
                decision = new TextChunk
                {
                    Text = phrase,
                    DetectedLanguage = "universal",
                    Probability = 1.0,
                    IsReliable = true,
                    IsPunctuationOrSpace = true,
                    Script = "None"
                };
            }

            if (technicalParts is not { Count: > 0 })
            {
                result.Add(decision);
            }
            else
            {
                int start = 0;
                foreach (var part in technicalParts)
                {
                    AppendOrdinarySpeechPart(phrase.AsSpan(start, part.Offset - start), decision);
                    result.Add(decision with
                    {
                        Text = part.Text,
                        IsTechnical = true,
                        IsPunctuationOrSpace = false,
                        RawTop5 = TechnicalContextDiagnostic
                    });

                    LogTechnicalContext(part.Text, decision.DetectedLanguage);

                    start = part.Offset + 1;
                }

                AppendOrdinarySpeechPart(phrase.AsSpan(start), decision);
            }

            // Reset for the next chunk
            currentSubPhrase.Clear();
            technicalParts?.Clear();
            currentScript = ScriptType.None;
            hasLetters = false;
            hasSpeakableContent = false;
        }

        void AppendOrdinarySpeechPart(ReadOnlySpan<char> part, TextChunk decision)
        {
            if (part.IsEmpty)
            {
                return;
            }

            AnalyzeSpeakableContent(part, out bool partHasLetters, out bool partHasDigits);
            if (partHasLetters || partHasDigits)
            {
                result.Add(decision with { Text = part.ToString(), IsTechnical = false });
                return;
            }

            result.Add(new TextChunk
            {
                Text = part.ToString(),
                DetectedLanguage = "universal",
                Probability = 1.0,
                IsReliable = true,
                IsPunctuationOrSpace = true,
                Script = "None"
            });
        }

        // Appends one already-classified script run to the current phrase.
        // Hiragana/Katakana may share a run; all other script transitions are hard boundaries.
        void AppendScriptRun(ReadOnlySpan<char> run, ScriptType runScript)
        {
            if (run.IsEmpty)
            {
                return;
            }

            if (currentScript != ScriptType.None &&
                !AreScriptsCompatible(currentScript, runScript))
            {
                FlushPhrase();
            }

            currentScript = runScript;
            hasLetters = true;
            hasSpeakableContent = true;
            currentSubPhrase.Append(run);
        }

        // Splits a word into Unicode-script runs without allocating substrings.
        // Neutral digits/combining marks stay attached to the surrounding run. This is especially
        // important for Japanese: 日本語のテスト becomes Han "日本語" + Kana "のテスト",
        // allowing Han to use Mandarin while Hiragana/Katakana use Japanese.
        void AppendWord(ReadOnlySpan<char> word)
        {
            if (word.IsEmpty)
            {
                return;
            }

            AnalyzeSpeakableContent(
                word,
                out bool fragmentHasLetters,
                out bool fragmentHasDecimalDigits);

            // Numbers and combining marks are language-neutral. Keep numeric content inside the
            // surrounding phrase, but do not let them choose or change a writing system.
            if (!fragmentHasLetters)
            {
                currentSubPhrase.Append(word);
                hasSpeakableContent |= fragmentHasDecimalDigits;
                return;
            }

            int runStart = 0;
            int scanIndex = 0;
            ScriptType runScript = ScriptType.None;

            while (scanIndex < word.Length)
            {
                int runeLength = DecodeRune(word[scanIndex..], out Rune rune);

                if (IsLetterRune(rune))
                {
                    ScriptType script = DetectScript(rune);

                    if (script != ScriptType.Other)
                    {
                        if (runScript == ScriptType.None)
                        {
                            runScript = script;
                        }
                        else if (!AreScriptsCompatible(runScript, script))
                        {
                            AppendScriptRun(word[runStart..scanIndex], runScript);
                            runStart = scanIndex;
                            runScript = script;
                        }
                    }
                }

                scanIndex += runeLength;
            }

            if (runScript == ScriptType.None)
            {
                // Unicode letters outside our known script table keep the previous generic behavior.
                AppendScriptRun(word, ScriptType.Other);
                return;
            }

            AppendScriptRun(word[runStart..], runScript);
        }

        // Retain speech boundaries while allowing ordinary words on both sides to share one vote.
        // Neutral symbols inherit the current script; real script transitions still end the phrase.
        void AppendTechnicalRun(ReadOnlySpan<char> run, ScriptType runScript)
        {
            if (run.IsEmpty)
            {
                return;
            }

            // An explicit language needs no shared analysis buffer or pending speech parts.
            if (resolvedForcedCode != null)
            {
                FlushPhrase();
                TextChunk forced = ResolveTechnicalContext(run.ToString(), runScript);
                result.Add(forced);
                LogTechnicalContext(forced.Text, forced.DetectedLanguage);
                return;
            }

            if (runScript != ScriptType.None)
            {
                if (currentScript != ScriptType.None &&
                    !AreScriptsCompatible(currentScript, runScript))
                {
                    FlushPhrase();
                }

                currentScript = runScript;
            }

            technicalParts ??= new List<(int Offset, string Text)>(4);
            technicalParts.Add((currentSubPhrase.Length, run.ToString()));
            currentSubPhrase.Append(' ');
        }

        void LogTechnicalContext(string run, string code)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "[LANG-DEBUG] \"{Text}\" — technical context → {Code}",
                    run,
                    code);
            }
        }

        // A phrase containing only technical syntax has no lexical evidence for Lingua.
        // Reuse sentence/neighbor context or the existing script/model fallback in that case.
        TextChunk ResolveTechnicalContext(string run, ScriptType runScript)
        {
            string code;
            double probability;
            bool reliable;

            if (resolvedForcedCode != null)
            {
                code = resolvedForcedCode;
                probability = 1.0;
                reliable = true;
            }
            else
            {
                string? capabilityCode = runScript switch
                {
                    ScriptType.Han => "cmn",
                    ScriptType.Hiragana or ScriptType.Katakana => "ja",
                    _ => null
                };

                if (capabilityCode != null)
                {
                    code = capabilityCode;
                    probability = 1.0;
                    reliable = true;
                }
                else if (!insideQuote &&
                         sentenceContext is { } context &&
                         context.Confidence >= _overrideThreshold &&
                         IsLanguageScriptCompatible(context.Language, runScript))
                {
                    code = context.EspeakCode;
                    probability = context.Confidence;
                    reliable = true;
                }
                else if (TryGetPreviousTechnicalContext(runScript, out TextChunk previous))
                {
                    code = previous.DetectedLanguage;
                    probability = previous.Probability;
                    reliable = previous.IsReliable;
                }
                else if (IsModelScriptCompatible(runScript) &&
                         (_detector != null || _modelScript is not (ScriptType.None or ScriptType.Other)))
                {
                    code = _modelEspeakCode;
                    probability = 1.0;
                    reliable = true;
                }
                else
                {
                    var (fallbackCode, _) = ResolveScriptFallback(run.AsSpan(), runScript);
                    code = fallbackCode ?? _modelEspeakCode;
                    probability = 0;
                    reliable = false;
                }
            }

            return new TextChunk
            {
                Text = run,
                DetectedLanguage = code,
                Probability = probability,
                IsReliable = reliable,
                IsPunctuationOrSpace = false,
                Script = GetScriptName(runScript),
                RawTop5 = TechnicalContextDiagnostic,
                IsTechnical = true
            };
        }

        bool TryGetPreviousTechnicalContext(ScriptType runScript, out TextChunk previous)
        {
            string scriptName = GetScriptName(runScript);

            for (int i = result.Count - 1; i >= analysisBoundaryIndex; i--)
            {
                TextChunk candidate = result[i];

                if (candidate.IsPunctuationOrSpace)
                {
                    continue;
                }

                if (candidate.IsRawPhonemes ||
                    !candidate.Script.Equals(scriptName, StringComparison.Ordinal))
                {
                    break;
                }

                if (candidate.IsReliable &&
                    !candidate.DetectedLanguage.Equals("universal", StringComparison.OrdinalIgnoreCase) &&
                    !candidate.DetectedLanguage.Equals("raw", StringComparison.OrdinalIgnoreCase))
                {
                    previous = candidate;
                    return true;
                }

                break;
            }

            previous = null!;
            return false;
        }

        void AppendTechnicalSpan(ReadOnlySpan<char> span)
        {
            if (span.IsEmpty)
            {
                return;
            }

            AnalyzeSpeakableContent(
                span,
                out bool fragmentHasLetters,
                out _);

            if (!fragmentHasLetters)
            {
                AppendTechnicalRun(span, ScriptType.None);
                return;
            }

            int runStart = 0;
            int scanIndex = 0;
            ScriptType runScript = ScriptType.None;

            while (scanIndex < span.Length)
            {
                int runeLength = DecodeRune(span[scanIndex..], out Rune rune);

                if (IsLetterRune(rune))
                {
                    ScriptType script = DetectScript(rune);

                    if (script != ScriptType.Other)
                    {
                        if (runScript == ScriptType.None)
                        {
                            runScript = script;
                        }
                        else if (!AreScriptsCompatible(runScript, script))
                        {
                            AppendTechnicalRun(span[runStart..scanIndex], runScript);
                            runStart = scanIndex;
                            runScript = script;
                        }
                    }
                }

                scanIndex += runeLength;
            }

            AppendTechnicalRun(
                span[runStart..],
                runScript == ScriptType.None ? ScriptType.Other : runScript);
        }

        // Quotes are hard boundaries for language analysis but silent in the phoneme stream.
        // If removing one would directly glue two word cores together, preserve exactly one
        // ordinary separator; otherwise do not invent any pause.
        void AppendSilentQuoteBoundary(ReadOnlySpan<char> fullText, int quoteIndex, int quoteLength)
        {
            FlushPhrase();

            bool needsSeparator = false;
            int nextIndex = quoteIndex + quoteLength;

            if (quoteIndex > 0 && nextIndex < fullText.Length)
            {
                OperationStatus leftStatus = Rune.DecodeLastFromUtf16(
                    fullText[..quoteIndex],
                    out Rune leftRune,
                    out _);

                int rightLength = DecodeRune(fullText[nextIndex..], out Rune rightRune);
                _ = rightLength;

                needsSeparator = leftStatus == OperationStatus.Done &&
                                 IsWordCoreRune(leftRune) &&
                                 IsWordCoreRune(rightRune);
            }

            if (needsSeparator)
            {
                result.Add(new TextChunk
                {
                    Text = " ",
                    DetectedLanguage = "universal",
                    Probability = 1.0,
                    IsReliable = true,
                    IsPunctuationOrSpace = true,
                    Script = "None"
                });
            }

            analysisBoundaryIndex = result.Count;
            insideQuote = !insideQuote;
        }

        // Punctuation is a SOFT language-boundary candidate: it ends the current detection phrase
        // but remains a universal token. Script transitions are the only unconditional language
        // boundaries; sentence context can later assign the same language on both sides.
        void AppendSoftPunctuation(ReadOnlySpan<char> punctuation)
        {
            FlushPhrase();

            result.Add(new TextChunk
            {
                Text = punctuation.ToString(),
                DetectedLanguage = "universal",
                Probability = 1.0,
                IsReliable = true,
                IsPunctuationOrSpace = true,
                Script = "None"
            });
        }

        // Fast path: most words contain no lexical hyphen and go directly through AppendWord.
        // Hyphenated words only pay the extra script-boundary checks when a lexical hyphen exists.
        void AppendWordToken(ReadOnlySpan<char> word)
        {
            if (word.IndexOfAny(_rules.LexicalHyphens) < 0)
            {
                AppendWord(word);
                return;
            }

            int fragmentStart = 0;
            int searchStart = 0;

            while (searchStart < word.Length)
            {
                int relativeIndex = word[searchStart..].IndexOfAny(_rules.LexicalHyphens);
                if (relativeIndex < 0)
                {
                    break;
                }

                int hyphenIndex = searchStart + relativeIndex;

                if (ShouldSplitAtLexicalHyphen(word, hyphenIndex))
                {
                    AppendWord(word[fragmentStart..hyphenIndex]);
                    AppendSoftPunctuation(word.Slice(hyphenIndex, 1));
                    fragmentStart = hyphenIndex + 1;
                }

                searchStart = hyphenIndex + 1;
            }

            AppendWord(word[fragmentStart..]);
        }

        ReadOnlySpan<char> input = text.AsSpan();
        int index = 0;

        while (index < input.Length)
        {
            int currentLength = DecodeRune(input[index..], out Rune currentRune);

            // PROTECTED TECHNICAL SPAN: preserve URL/email/path/query/code punctuation as one
            // structural token, but do not let code-like text cast an independent Lingua vote.
            if (TechnicalTextRecognizer.TryGetSpanLength(input, index, _rules, out int technicalLength))
            {
                AppendTechnicalSpan(input.Slice(index, technicalLength));
                index += technicalLength;
                continue;
            }

            // Visual quotes split language-analysis context but are not phonetic punctuation.
            if (IsVisualQuote(currentRune))
            {
                AppendSilentQuoteBoundary(input, index, currentLength);
                index += currentLength;
                continue;
            }

            // A period after a recognized abbreviation/initial belongs to the surrounding language
            // phrase instead of creating a tiny independent chunk such as "Dr" + "." + "Smith".
            if (currentRune.Value <= char.MaxValue &&
                IsPeriodLike((char)currentRune.Value) &&
                ShouldKeepAbbreviationPeriod(input, index))
            {
                currentSubPhrase.Append(input.Slice(index, currentLength));
                index += currentLength;
                continue;
            }

            // GROUP 1: punctuation/dashes are soft language-boundary candidates.
            if (IsBoundaryPunctuation(currentRune))
            {
                int start = index;
                index += currentLength;

                while (index < input.Length)
                {
                    int punctuationLength = DecodeRune(
                        input[index..],
                        out Rune punctuationRune);

                    if (!IsBoundaryPunctuation(punctuationRune))
                    {
                        break;
                    }

                    index += punctuationLength;
                }

                AppendSoftPunctuation(input[start..index]);
                continue;
            }

            // GROUP 2: Words with optional internal connectors.
            if (IsWordCoreRune(currentRune))
            {
                int start = index;
                index += currentLength;

                while (index < input.Length)
                {
                    int nextLength = DecodeRune(
                        input[index..],
                        out Rune nextRune);

                    if (IsWordCoreRune(nextRune))
                    {
                        index += nextLength;
                        continue;
                    }

                    if (nextRune.Value <= char.MaxValue &&
                        IsWordConnector((char)nextRune.Value))
                    {
                        int afterConnector = index + nextLength;

                        if (afterConnector < input.Length)
                        {
                            int followingLength = DecodeRune(
                                input[afterConnector..],
                                out Rune followingRune);

                            if (IsWordCoreRune(followingRune))
                            {
                                index = afterConnector + followingLength;
                                continue;
                            }
                        }
                    }

                    break;
                }

                AppendWordToken(input[start..index]);
                continue;
            }

            // GROUP 3: Spaces and unrecognized symbols. They stay attached to the current
            // phrase exactly like the previous fallback group, preserving text layout.
            int garbageStart = index;
            index += currentLength;

            while (index < input.Length)
            {
                int nextLength = DecodeRune(
                    input[index..],
                    out Rune nextRune);

                bool isBoundaryPunctuation = IsBoundaryPunctuation(nextRune);

                if (isBoundaryPunctuation || IsVisualQuote(nextRune) || IsWordCoreRune(nextRune) ||
                    TechnicalTextRecognizer.TryGetSpanLength(input, index, _rules, out _))
                {
                    break;
                }

                index += nextLength;
            }

            currentSubPhrase.Append(input[garbageStart..index]);
        }

        // Flush anything left at the end of the text. Punctuation intentionally remains as
        // separate universal tokens; voice caching downstream removes redundant SetVoice calls
        // without sacrificing pauses or sentence-level prosody.
        FlushPhrase();
        return result;
    }

    // Keep code/URL/file identifiers neutral in sentence context just as they are in local phrases.
    // Ordinary text is returned by reference; a buffer is created only for the first technical span.
    private string GetSentenceAnalysisText(string text)
    {
        StringBuilder? analysis = null;
        int start = 0;
        int index = 0;
        while (index < text.Length)
        {
            if (!TechnicalTextRecognizer.TryGetSpanLength(text.AsSpan(), index, _rules, out int length))
            {
                index += DecodeRune(text.AsSpan(index), out _);
                continue;
            }

            analysis ??= new StringBuilder(Math.Min(text.Length, 256));
            analysis.Append(text.AsSpan(start, index - start));
            analysis.Append(' ');
            index += length;
            start = index;
        }

        if (analysis == null)
        {
            return text;
        }

        analysis.Append(text.AsSpan(start));
        return analysis.ToString();
    }

    /// <summary>
    /// Analyzes a chunk of text with local detector evidence first. Sentence context and the
    /// loaded-model bias are used only to stabilize short fragments whose local verdict is ambiguous.
    /// </summary>
    private TextChunk ProcessSubPhrase(
        string text,
        ScriptType script,
        SentenceLanguageContext? sentenceContext)
    {
        string cleanText = text.Trim();
        int letterCount = CountLetters(cleanText.AsSpan());

        // SCRIPT CAPABILITY ROUTING:
        // eSpeak can pronounce Han through Mandarin, but its Japanese frontend does not resolve
        // Kanji readings without an external dictionary. Keep the engine dependency-free by routing
        // Han to Mandarin and reserving Japanese for Hiragana/Katakana. This applies only to automatic
        // detection; an API-forced language is handled earlier in FlushPhrase and remains authoritative.
        string? capabilityCode = script switch
        {
            ScriptType.Han => "cmn",
            ScriptType.Hiragana or ScriptType.Katakana => "ja",
            _ => null
        };

        if (capabilityCode != null)
        {
            var capabilityChunk = new TextChunk
            {
                Text = text,
                DetectedLanguage = capabilityCode,
                Probability = 1.0,
                IsReliable = true,
                IsPunctuationOrSpace = false,
                Script = GetScriptName(script),
                RawTop5 = ScriptCapabilityRouteDiagnostic
            };

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "[LANG-DEBUG] \"{Text}\" — script capability route → {Code}",
                    text,
                    capabilityCode);
            }

            return capabilityChunk;
        }

        bool canFavorModelLanguage = IsModelScriptCompatible(script);
        bool debugEnabled = _logger.IsEnabled(LogLevel.Debug);

        if (_detector == null)
        {
            // An unmapped model language has no known script; it must not absorb every known script.
            bool useModelLanguage = _modelScript is not (ScriptType.None or ScriptType.Other) &&
                canFavorModelLanguage;
            var (code, diagnostic) = useModelLanguage
                ? (_modelEspeakCode, ModelLanguageFallbackDiagnostic)
                : ResolveScriptFallback(cleanText.AsSpan(), script);

            return CreateFallbackChunk(text, script, code ?? _modelEspeakCode,
                code == null ? ModelLanguageFallbackDiagnostic : diagnostic);
        }

        // LOCAL DETECTOR FIRST:
        // Always inspect the actual chunk before applying sentence/model context. Keeping the
        // runner-up matters: 0.53 / 0.44 is qualitatively different from a near tie such as
        // 0.51 / 0.49 even though both winners are above 0.5.
        var confidences = _detector.ComputeLanguageConfidenceValues(cleanText);

        IReadOnlyList<string> rawTop5 = debugEnabled
            ? BuildTopDiagnostics(confidences)
            : Array.Empty<string>();

        Language rawBestLanguage = Language.Unknown;
        double rawBestProbability = -1;
        double rawSecondProbability = -1;
        double rawModelProbability = 0;

        foreach (var kvp in confidences)
        {
            double probability = kvp.Value;

            if (_modelLinguaLang.HasValue && kvp.Key == _modelLinguaLang.Value)
            {
                rawModelProbability = probability;
            }

            if (probability > rawBestProbability)
            {
                rawSecondProbability = rawBestProbability;
                rawBestProbability = probability;
                rawBestLanguage = kvp.Key;
            }
            else if (probability > rawSecondProbability)
            {
                rawSecondProbability = probability;
            }
        }

        // Compute the existing native-language prior before accepting a short foreign winner.
        // This does not replace confidence/margin; it only prevents tiny fragments from bypassing
        // the configured short-text bonus before that bonus gets a chance to participate.
        double currentMultiplier = 1.0;

        if (canFavorModelLanguage)
        {
            if (letterCount <= _minLimit)
            {
                currentMultiplier = 1.0 + _maxBonus;
            }
            else if (letterCount < _maxLimit)
            {
                double ratio = (double)(_maxLimit - letterCount) / (_maxLimit - _minLimit);
                currentMultiplier = 1.0 + (_maxBonus * ratio);
            }
        }

        double rawMargin = rawBestProbability - Math.Max(0.0, rawSecondProbability);

        bool requiresForeignValidation =
            canFavorModelLanguage &&
            _foreignValidationMaxLetters > 0 &&
            letterCount <= _foreignValidationMaxLetters &&
            _modelLinguaLang.HasValue &&
            rawBestLanguage != Language.Unknown &&
            rawBestLanguage != _modelLinguaLang.Value;

        bool survivesModelPrior =
            !requiresForeignValidation ||
            rawBestProbability > rawModelProbability * currentMultiplier;

        bool hasConvincingLocalWinner =
            rawBestLanguage != Language.Unknown &&
            rawBestProbability >= _localWinnerProbabilityFloor &&
            rawMargin >= _localWinnerMarginFloor &&
            survivesModelPrior;

        // A convincing local verdict is authoritative. Sentence context and the loaded model
        // are not allowed to swallow a real short code-switch such as "pero" or "d'accord".
        // Very short foreign winners must additionally survive the configured native-language prior.
        if (hasConvincingLocalWinner)
        {
            string localCode = _mapper.MapBackToEspeak(rawBestLanguage, _modelEspeakCode);

            var localChunk = new TextChunk
            {
                Text = text,
                DetectedLanguage = localCode,
                Probability = rawBestProbability,
                IsReliable = true,
                IsPunctuationOrSpace = false,
                Script = GetScriptName(script),
                RawTop5 = rawTop5
            };

            if (debugEnabled)
            {
                _logger.LogDebug(
                    "[LANG-DEBUG] \"{Text}\" ({LetterCount} letters) → {Code} [LOCAL DETECTOR, conf={Conf:0.0000}, margin={Margin:0.0000}] | raw: {Raw}",
                    text,
                    letterCount,
                    localCode,
                    rawBestProbability,
                    rawMargin,
                    string.Join(", ", rawTop5));
            }

            return localChunk;
        }

        // SENTENCE-CONTEXT OVERRIDE:
        // Short-fragment inheritance still works, but only after local evidence has been inspected
        // and found ambiguous. This keeps the original stabilization behaviour without suppressing
        // a convincing foreign insert in an otherwise dominant sentence.
        if (sentenceContext is { } context &&
            letterCount < _maxLimit &&
            context.Confidence >= _overrideThreshold &&
            IsLanguageScriptCompatible(context.Language, script))
        {
            var contextChunk = new TextChunk
            {
                Text = text,
                DetectedLanguage = context.EspeakCode,
                Probability = context.Confidence,
                IsReliable = true,
                IsPunctuationOrSpace = false,
                Script = GetScriptName(script),
                RawTop5 = rawTop5
            };

            if (debugEnabled)
            {
                _logger.LogDebug(
                    "[LANG-DEBUG] \"{Text}\" ({LetterCount} letters) → {Code} [SENTENCE OVERRIDE after local, conf={Conf:0.0000}, local={LocalConf:0.0000}, margin={Margin:0.0000}] | raw: {Raw}",
                    text,
                    letterCount,
                    context.EspeakCode,
                    context.Confidence,
                    Math.Max(0.0, rawBestProbability),
                    Math.Max(0.0, rawMargin),
                    string.Join(", ", rawTop5));
            }

            return contextChunk;
        }

        // DYNAMIC MODEL-LANGUAGE BONUS:
        // Only ambiguous local results reach this point. Reuse the multiplier already calculated
        // above so short foreign validation and final scoring share exactly the same prior.
        Language bestLinguaLang = Language.Unknown;
        double bestAdjustedScore = -1;
        double originalProbabilityOfBest = 0;

        foreach (var kvp in confidences)
        {
            Language lang = kvp.Key;
            double score = kvp.Value;

            if (canFavorModelLanguage &&
                _modelLinguaLang.HasValue &&
                lang == _modelLinguaLang.Value)
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

        // EMERGENCY FALLBACK: every candidate scored zero, so the adjusted winner is arbitrary.
        // Try a strong script-language hint first, then the coarse script default.
        if (bestAdjustedScore <= 0)
        {
            var (fallbackCode, diagnostic) = ResolveScriptFallback(cleanText.AsSpan(), script);

            if (fallbackCode != null)
            {
                return CreateFallbackChunk(text, script, fallbackCode, diagnostic);
            }
        }

        string finalEspeakCode = _modelEspeakCode;
        if (bestLinguaLang != Language.Unknown)
        {
            finalEspeakCode = _mapper.MapBackToEspeak(bestLinguaLang, _modelEspeakCode);
        }

        var finalChunk = new TextChunk
        {
            Text = text,
            DetectedLanguage = finalEspeakCode,
            Probability = originalProbabilityOfBest,
            IsReliable = originalProbabilityOfBest > _reliabilityProbabilityThreshold,
            IsPunctuationOrSpace = false,
            Script = GetScriptName(script),
            RawTop5 = rawTop5
        };

        if (debugEnabled)
        {
            _logger.LogDebug(
                "[LANG-DEBUG] \"{Text}\" ({LetterCount} letters, x{Multiplier:0.000}) → {Code} [AMBIGUOUS LOCAL] | raw: {Raw}",
                text,
                letterCount,
                currentMultiplier,
                finalEspeakCode,
                string.Join(", ", rawTop5));
        }

        return finalChunk;
    }

    private static (string? Code, IReadOnlyList<string> Diagnostic) ResolveScriptFallback(
        ReadOnlySpan<char> text,
        ScriptType script)
    {
        if (StrongLanguageHintsByScript.TryGetValue(script, out var hints))
        {
            int hitIndex = text.IndexOfAny(hints.Chars);
            if (hitIndex >= 0 && hints.Map.TryGetValue(text[hitIndex], out string? code))
            {
                return (code, StrongLanguageHintDiagnostic);
            }
        }

        return (ScriptFallbackLanguage.GetValueOrDefault(script), ScriptFallbackDiagnostic);
    }

    private TextChunk CreateFallbackChunk(
        string text,
        ScriptType script,
        string code,
        IReadOnlyList<string> diagnostic)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[LANG-DEBUG] \"{Text}\" — {Fallback} → {Code}",
                text, diagnostic[0], code);
        }

        return new TextChunk
        {
            Text = text,
            DetectedLanguage = code,
            Probability = 0,
            IsReliable = false,
            IsPunctuationOrSpace = false,
            Script = GetScriptName(script),
            RawTop5 = diagnostic
        };
    }

}
