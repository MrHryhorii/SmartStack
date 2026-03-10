namespace ONNX_Runner;

public class PhonemizerSettings
{
    public List<string> SupportedLanguages { get; set; } = [];
    public bool UseLanguageDetector { get; set; } = true;
}