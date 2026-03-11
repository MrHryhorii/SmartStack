namespace ONNX_Runner;

public class PhonemizerSettings
{
    public List<string> SupportedLanguages { get; set; } = [];
    public bool UseLanguageDetector { get; set; } = true;

    // Параметри для налаштування бонусів мови моделі
    public double MaxBonusMultiplier { get; set; } = 0.50; // +50%
    public int BonusMinLetterCount { get; set; } = 8;      // До цієї довжини бонус максимальний
    public int BonusMaxLetterCount { get; set; } = 32;     // Після цієї довжини бонус 0%
}