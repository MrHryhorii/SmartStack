namespace ONNX_Runner.Models;

/// <summary>
/// Configuration for the NLP (Natural Language Processing) and language detection module.
/// </summary>
public class PhonemizerSettings
{
    /// <summary>
    /// Additional language candidates; the model language is included automatically.
    /// Fewer than two distinct recognized languages use model/script routing without Lingua.
    /// </summary>
    public List<string> SupportedLanguages { get; set; } = [];

    public bool UseLanguageDetector { get; set; } = true;

    // --- Local Evidence Parameters ---

    /// <summary>Minimum raw confidence required for an authoritative local winner (inclusive).</summary>
    public double LocalWinnerProbabilityFloor { get; set; } = 0.50;

    /// <summary>Minimum raw confidence lead over the runner-up for a local winner (inclusive).</summary>
    public double LocalWinnerMarginFloor { get; set; } = 0.08;

    /// <summary>
    /// Minimum unadjusted confidence (strict) for an ambiguous winner to supply neighboring
    /// technical context. Does not change its selected language or authoritative results.
    /// </summary>
    public double ReliabilityProbabilityThreshold { get; set; } = 0.50;

    // --- Dynamic Confidence Bonus Parameters ---
    // Short words are statistically harder for AI to identify. We give a confidence bonus 
    // to the TTS model's native language to prevent it from randomly switching accents on short words (e.g., "OK", "hi").

    /// <summary>Maximum confidence multiplier applied to short words (e.g., 0.50 = +50% bonus).</summary>
    public double MaxBonusMultiplier { get; set; } = 0.60;

    /// <summary>Words with this many letters or fewer receive the maximum bonus.</summary>
    public int BonusMinLetterCount { get; set; } = 8;

    /// <summary>Words longer than this receive 0% bonus, trusting the ML detector completely.</summary>
    public int BonusMaxLetterCount { get; set; } = 32;

    /// <summary>
    /// Foreign-language winners with this many letters or fewer must also beat the model-language
    /// short-text bonus before they can be accepted as authoritative local detections.
    /// Set to 0 to disable this additional validation.
    /// </summary>
    public int ForeignValidationMaxLetters { get; set; } = 5;

    // --- Sentence-Context Override Parameters ---
    // A short/ambiguous word (e.g. a name like "Hermes") can still lose to a rival language 
    // even with the max bonus above. A confident whole-sentence verdict can stabilize an
    // ambiguous phrase after local confidence, margin, and short-foreign validation are checked.

    /// <summary>Sentence-level confidence needed to override an ambiguous compatible-script subphrase.</summary>
    public double MixedLanguageOverrideThreshold { get; set; } = 0.85;

    /// <summary>Minimum letters the whole sentence needs before its context is trusted for the override.</summary>
    public int MinSentenceLengthForOverride { get; set; } = 20;
}
