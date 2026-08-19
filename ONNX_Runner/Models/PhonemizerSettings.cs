namespace ONNX_Runner.Models;

/// <summary>
/// Configuration for the NLP (Natural Language Processing) and language detection module.
/// </summary>
public class PhonemizerSettings
{
    /// <summary>
    /// A subset of ISO language codes to load into memory. 
    /// Limiting this saves RAM compared to loading all 75+ supported languages.
    /// </summary>
    public List<string> SupportedLanguages { get; set; } = [];

    public bool UseLanguageDetector { get; set; } = true;

    // --- Dynamic Confidence Bonus Parameters ---
    // Short words are statistically harder for AI to identify. We give a confidence bonus 
    // to the TTS model's native language to prevent it from randomly switching accents on short words (e.g., "OK", "hi").

    /// <summary>Maximum confidence multiplier applied to short words (e.g., 0.50 = +50% bonus).</summary>
    public double MaxBonusMultiplier { get; set; } = 0.50;

    /// <summary>Words with this many letters or fewer receive the maximum bonus.</summary>
    public int BonusMinLetterCount { get; set; } = 8;

    /// <summary>Words longer than this receive 0% bonus, trusting the ML detector completely.</summary>
    public int BonusMaxLetterCount { get; set; } = 32;

    // --- Sentence-Context Override Parameters ---
    // A short/ambiguous word (e.g. a name like "Hermes") can still lose to a rival language 
    // even with the max bonus above. If the whole sentence is confidently the model's language, 
    // that verdict overrides the word-level bonus instead of fighting it.

    /// <summary>Sentence-level confidence in the model's language needed to override a risky sub-phrase.</summary>
    public double MixedLanguageOverrideThreshold { get; set; } = 0.85;

    /// <summary>Minimum letters the whole sentence needs before its context is trusted for the override.</summary>
    public int MinSentenceLengthForOverride { get; set; } = 20;
}