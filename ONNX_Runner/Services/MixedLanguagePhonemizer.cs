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

    // True if the text is a pre-formatted IPA transcription (e.g., [hɛˈloʊ]).
    // Skips language detection and eSpeak, going straight to fallback validation.
    public bool IsRawPhonemes { get; init; }
}

/// <summary>
/// Advanced NLP (Natural Language Processing) module for handling mixed-language input.
/// It tokenizes text, detects writing scripts (e.g., Latin vs. Cyrillic), and uses 
/// statistical analysis (Lingua) to predict the language of each chunk, allowing the TTS 
/// to switch phonetic rules dynamically (e.g., reading an English quote inside a Ukrainian text).
/// </summary>
public partial class MixedLanguagePhonemizer
{
    // Tokenizer classification is intentionally implemented with a single ReadOnlySpan<char>
    // scan instead of Regex.Matches(). This removes MatchCollection / Match / match.Value
    // allocations from the hot path while preserving the original three logical groups:
    // 1. Punctuation/Dashes
    // 2. Words (letters, decimal digits, marks, internal apostrophes, periods, and lexical hyphens)
    // 3. Unrecognized garbage/symbols
    //
    // Lexical hyphens are accepted only when surrounded by word characters because they are part
    // of the word itself (e.g., "state-of-the-art", "будь-який", Hebrew maqaf compounds).
    // Typographic dashes (figure/en/em/horizontal bar) remain punctuation even without spaces,
    // so constructions such as "word—word" still create a hard phrase boundary.
    private static readonly SearchValues<char> LexicalHyphens =
        SearchValues.Create("-\u2010\u2011\u058A\u05BE\u30A0");

    // Diagnostic collections are immutable-by-contract and cached once for the entire process.
    // Production requests therefore do not allocate tiny List<string> instances for metadata.
    private static readonly IReadOnlyList<string> ForcedLanguageDiagnostic =
        ["Forced by API"];

    private static readonly IReadOnlyList<string> StrongLanguageHintDiagnostic =
        ["strong script-language hint"];

    private static readonly IReadOnlyList<string> ScriptFallbackDiagnostic =
        ["script fallback"];

    private static readonly IReadOnlyList<string> ScriptCapabilityRouteDiagnostic =
        ["script capability route"];

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

    // Strong script-language hints used only after Lingua produced no usable signal.
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

    private readonly LanguageDetector _detector;
    private readonly EspeakLinguaMapper _mapper;
    private readonly string _modelEspeakCode;

    /// <summary>
    /// Pre-split segments of the loaded model's eSpeak code (cached at startup to prevent allocations in hot loops).
    /// </summary>
    private readonly string[] _modelEspeakParts;

    private readonly Language? _modelLinguaLang;
    private readonly string? _modelContextOverrideCode;

    // Primary writing system of the loaded model language. This is cached once and used only
    // as a guard against impossible sentence-context overrides / native-language bonuses.
    // It does not choose the language of a foreign chunk; normal detection and fallbacks still do that.
    private readonly ScriptType _modelScript;

    // Cache for dynamic bonus multiplier settings
    private readonly double _maxBonus;
    private readonly int _minLimit;
    private readonly int _maxLimit;

    // Cache for sentence-context override settings
    private readonly double _overrideThreshold;
    private readonly int _minSentenceLength;

    // logger
    private readonly ILogger<MixedLanguagePhonemizer> _logger;

    public MixedLanguagePhonemizer(PhonemizerSettings settings, string modelEspeakCode, ILogger<MixedLanguagePhonemizer> logger)
    {
        _logger = logger;

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
        _modelContextOverrideCode = _modelLinguaLang.HasValue
            ? _mapper.MapBackToEspeak(_modelLinguaLang.Value, _modelEspeakCode)
            : null;
        _modelScript = GetPrimaryScriptForLanguage(_modelLinguaLang);

        // Load bonus configuration (or fallback to safe defaults)
        _maxBonus = settings?.MaxBonusMultiplier ?? 0.60;
        _minLimit = settings?.BonusMinLetterCount ?? 8;
        _maxLimit = settings?.BonusMaxLetterCount ?? 32;
        _overrideThreshold = settings?.MixedLanguageOverrideThreshold ?? 0.85;
        _minSentenceLength = settings?.MinSentenceLengthForOverride ?? 20;


        var finalCodesToSupport = new List<string>();
        var seenBaseFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Guarantee that the base language of the loaded TTS model is ALWAYS supported.
        // It occupies the slot for its base family first, blocking any user-defined dialect duplicates.
        finalCodesToSupport.Add(_modelEspeakCode);
        seenBaseFamilies.Add(baseFamily);

        // Load user-defined supported languages to optimize memory.
        // Lingua takes a lot of RAM if loading all 75 languages.
        if (settings?.SupportedLanguages != null)
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
    private static bool ShouldSplitAtLexicalHyphen(ReadOnlySpan<char> word, int hyphenIndex)
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
    private static ScriptType FindNearestLetterScriptLeft(ReadOnlySpan<char> word, int hyphenIndex)
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
    private static ScriptType FindNearestLetterScriptRight(ReadOnlySpan<char> word, int hyphenIndex)
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

    // Returns true for hyphens that may participate in a word token.
    private static bool IsLexicalHyphen(char value)
    {
        return value is '-'
            or '\u2010'
            or '\u2011'
            or '\u058A'
            or '\u05BE'
            or '\u30A0';
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

    // Checks whether a character is an apostrophe, period, or lexical hyphen allowed inside a word.
    private static bool IsWordConnector(char value)
    {
        return value is '\'' or '’' or '.'
            || IsLexicalHyphen(value);
    }

    // Returns true for punctuation that forms a hard phrase boundary.
    private static bool IsHardPunctuation(Rune rune)
    {
        // After DynamicPunctuationMapper normalizes an orthographic segment, the selected
        // model-native punctuation may come from any writing system. Classifying by Unicode
        // category keeps tokenization symmetric for Western, CJK, Arabic, Indic, and other
        // punctuation without maintaining a second hard-coded symbol catalog here.
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
    /// Chunks the text into segments based on punctuation and script changes, 
    /// returning a sequence of tokens ready for language prediction.
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
        // We only run the heavy Lingua statistical analysis if we ARE NOT forcing a language.
        double? sentenceConfidence = null;
        if (resolvedForcedCode == null &&
            _modelLinguaLang.HasValue &&
            CountLetters(text.AsSpan()) >= _minSentenceLength)
        {
            _detector
                .ComputeLanguageConfidenceValues(text)
                .TryGetValue(_modelLinguaLang.Value, out double conf);

            sentenceConfidence = conf;

            // Log only failed sentence overrides to avoid duplicating the existing
            // [SENTENCE OVERRIDE] diagnostics for successful cases.
            if (_logger.IsEnabled(LogLevel.Debug) &&
                conf < _overrideThreshold)
            {
                _logger.LogDebug(
                    "[LANG-DEBUG] Sentence context → {Code} | conf={Conf:0.0000} < threshold={Threshold:0.0000}; using chunk detection",
                    _modelContextOverrideCode ?? _modelEspeakCode,
                    conf,
                    _overrideThreshold);
            }
        }

        // TEXT TOKENIZATION
        // A single span scan replaces Regex.Matches() here. It preserves the original logical
        // token groups while avoiding MatchCollection, Match, and match.Value allocations.
        var currentSubPhrase = new StringBuilder(Math.Min(Math.Max(text.Length, 16), 256));
        ScriptType currentScript = ScriptType.None;
        bool hasLetters = false;
        bool hasSpeakableContent = false;

        // Helper function to process accumulated content before moving to punctuation or script changes
        void FlushPhrase()
        {
            if (currentSubPhrase.Length == 0)
            {
                return;
            }

            string phrase = currentSubPhrase.ToString();

            if (hasLetters)
            {
                // LANGUAGE ASSIGNMENT
                if (resolvedForcedCode != null)
                {
                    // FAST PATH: Language is forced. Bypass Lingua and assign the resolved code directly.
                    result.Add(new TextChunk
                    {
                        Text = phrase,
                        DetectedLanguage = resolvedForcedCode,
                        Probability = 1.0,
                        IsReliable = true,
                        IsPunctuationOrSpace = false,
                        Script = GetScriptName(currentScript),
                        RawTop5 = ForcedLanguageDiagnostic
                    });
                }
                else
                {
                    // SLOW PATH: Use ML detector and fallback algorithms to guess the language.
                    ProcessSubPhrase(
                        phrase,
                        currentScript,
                        result,
                        sentenceConfidence);
                }
            }
            else if (hasSpeakableContent)
            {
                // Digits are language-neutral for detection but still need a language for pronunciation.
                // Standalone numeric content therefore bypasses Lingua and inherits the forced language
                // when present, otherwise the loaded model's native eSpeak language.
                result.Add(new TextChunk
                {
                    Text = phrase,
                    DetectedLanguage = resolvedForcedCode ?? _modelEspeakCode,
                    Probability = 1.0,
                    IsReliable = true,
                    IsPunctuationOrSpace = false,
                    Script = "None"
                });
            }
            else
            {
                // Punctuation, spaces, and non-pronounceable symbols are universal.
                result.Add(new TextChunk
                {
                    Text = phrase,
                    DetectedLanguage = "universal",
                    Probability = 1.0,
                    IsReliable = true,
                    IsPunctuationOrSpace = true,
                    Script = "None"
                });
            }

            // Reset for the next chunk
            currentSubPhrase.Clear();
            currentScript = ScriptType.None;
            hasLetters = false;
            hasSpeakableContent = false;
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

        // Preserves the original tokenizer behavior for punctuation that becomes a real boundary.
        // The separator itself remains a universal punctuation token between language runs.
        void AppendUniversalPunctuation(ReadOnlySpan<char> punctuation)
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
            if (word.IndexOfAny(LexicalHyphens) < 0)
            {
                AppendWord(word);
                return;
            }

            int fragmentStart = 0;
            int searchStart = 0;

            while (searchStart < word.Length)
            {
                int relativeIndex = word[searchStart..].IndexOfAny(LexicalHyphens);
                if (relativeIndex < 0)
                {
                    break;
                }

                int hyphenIndex = searchStart + relativeIndex;

                if (ShouldSplitAtLexicalHyphen(word, hyphenIndex))
                {
                    AppendWord(word[fragmentStart..hyphenIndex]);
                    AppendUniversalPunctuation(word.Slice(hyphenIndex, 1));
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

            // GROUP 1: Punctuation and dashes.
            if (IsHardPunctuation(currentRune))
            {
                int start = index;
                index += currentLength;

                while (index < input.Length)
                {
                    int punctuationLength = DecodeRune(
                        input[index..],
                        out Rune punctuationRune);

                    if (!IsHardPunctuation(punctuationRune))
                    {
                        break;
                    }

                    index += punctuationLength;
                }

                AppendUniversalPunctuation(input[start..index]);
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

                bool isHardPunctuation = IsHardPunctuation(nextRune);

                if (isHardPunctuation || IsWordCoreRune(nextRune))
                {
                    break;
                }

                index += nextLength;
            }

            currentSubPhrase.Append(input[garbageStart..index]);
        }

        // Flush anything left at the end of the text
        FlushPhrase();
        return result;
    }

    /// <summary>
    /// Analyzes a chunk of text to determine its language, applying statistical confidence 
    /// weighting to prevent random language switching on short, ambiguous words.
    /// </summary>
    private void ProcessSubPhrase(string text, ScriptType script, List<TextChunk> result, double? sentenceConfidence)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

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
            result.Add(new TextChunk
            {
                Text = text,
                DetectedLanguage = capabilityCode,
                Probability = 1.0,
                IsReliable = true,
                IsPunctuationOrSpace = false,
                Script = GetScriptName(script),
                RawTop5 = ScriptCapabilityRouteDiagnostic
            });

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "[LANG-DEBUG] \"{Text}\" — script capability route → {Code}",
                    text,
                    capabilityCode);
            }

            return;
        }

        bool canFavorModelLanguage = IsModelScriptCompatible(script);

        // SENTENCE-CONTEXT OVERRIDE: a confidently single-language sentence may win only when
        // the chunk uses a writing system compatible with the loaded model language. A clear
        // script mismatch (e.g. Cyrillic inside an English/Latin sentence) must continue through
        // normal detection and fallback instead of being forcibly assigned to the model language.
        if (canFavorModelLanguage &&
            letterCount < _maxLimit &&
            sentenceConfidence >= _overrideThreshold &&
            _modelContextOverrideCode != null)
        {
            string overrideCode = _modelContextOverrideCode;

            result.Add(new TextChunk
            {
                Text = text,
                DetectedLanguage = overrideCode,
                Probability = sentenceConfidence.Value,
                IsReliable = true,
                IsPunctuationOrSpace = false,
                Script = GetScriptName(script)
            });

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[LANG-DEBUG] \"{Text}\" ({LetterCount} letters) → {Code} [SENTENCE OVERRIDE, conf={Conf:0.0000}]",
                    text, letterCount, overrideCode, sentenceConfidence.Value);
            }

            return;
        }

        // --- DYNAMIC CONFIDENCE MULTIPLIER ---
        // Problem: Short words (e.g., "no", "да", "hi") are statistically ambiguous and often misidentified by Lingua.
        // Solution: We apply an artificial confidence bonus to the base TTS model's language depending on the word length.
        // Short text gets max bonus. Long text gets zero bonus (trusting the detector completely).
        double currentMultiplier = 1.0;

        // Do not bias a short chunk toward the model language when its script is clearly
        // incompatible. This lets the detector return zero naturally and activates the existing
        // diagnostic-letter / script fallback path when no configured language can match it.
        if (canFavorModelLanguage)
        {
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
        }
        // Get statistical confidences from the ML detector
        var confidences = _detector.ComputeLanguageConfidenceValues(cleanText);

        bool debugEnabled = _logger.IsEnabled(LogLevel.Debug);
        IReadOnlyList<string> rawTop5 = debugEnabled
            ? BuildTopDiagnostics(confidences)
            : Array.Empty<string>();

        Language bestLinguaLang = Language.Unknown;
        double bestAdjustedScore = -1;
        double originalProbabilityOfBest = 0;

        foreach (var kvp in confidences)
        {
            Language lang = kvp.Key;
            double score = kvp.Value;

            // BONUS: Favor the model's native language only when the chunk script is compatible.
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

        // EMERGENCY FALLBACK: every candidate scored zero, so "the winner" above is arbitrary
        // dictionary order, not a real detection. Try a strong script-language hint first
        // (narrower, e.g. Ukrainian over generic Cyrillic), then the coarse script default.
        if (bestAdjustedScore <= 0)
        {
            string? fallbackCode = null;
            string tier = "script";

            if (StrongLanguageHintsByScript.TryGetValue(script, out var hints))
            {
                int hitIndex = cleanText.AsSpan().IndexOfAny(hints.Chars);
                if (hitIndex >= 0 && hints.Map.TryGetValue(cleanText[hitIndex], out var byChar))
                {
                    fallbackCode = byChar;
                    tier = "hint";
                }
            }

            fallbackCode ??= ScriptFallbackLanguage.TryGetValue(script, out var byScript) ? byScript : null;

            if (fallbackCode != null)
            {
                result.Add(new TextChunk
                {
                    Text = text,
                    DetectedLanguage = fallbackCode,
                    Probability = 0,
                    IsReliable = false,
                    IsPunctuationOrSpace = false,
                    Script = GetScriptName(script),
                    RawTop5 = tier == "hint"
                        ? StrongLanguageHintDiagnostic
                        : ScriptFallbackDiagnostic
                });

                if (debugEnabled)
                {
                    _logger.LogDebug("[LANG-DEBUG] \"{Text}\" — no language matched, using {Tier} fallback → {Code}",
                        text, tier, fallbackCode);
                }

                return;
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
            Script = GetScriptName(script),
            RawTop5 = rawTop5
        });

        if (debugEnabled)
        {
            _logger.LogDebug("[LANG-DEBUG] \"{Text}\" ({LetterCount} letters, x{Multiplier:0.000}) → {Code} | raw: {Raw}",
                text, letterCount, currentMultiplier, finalEspeakCode, string.Join(", ", rawTop5));
        }
    }

}