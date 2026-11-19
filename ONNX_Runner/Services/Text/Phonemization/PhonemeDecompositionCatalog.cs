namespace ONNX_Runner.Services.Text.Phonemization;

/// <summary>IPA symbol definitions, independent of languages and model inventories.</summary>
internal static class PhonemeDecompositionCatalog
{
    internal static readonly IReadOnlyDictionary<char, string> Symbols = new Dictionary<char, string>
    {
        ['ʦ'] = "ts", ['ʧ'] = "tʃ", ['ʣ'] = "dz", ['ʤ'] = "dʒ", ['ʨ'] = "tɕ", ['ʥ'] = "dʑ",
        ['ɚ'] = "ə˞", ['ɝ'] = "ɜ˞"
    };

    internal static readonly string[] Nasals = ["n", "m", "ŋ", "ɲ"];
    internal static readonly string[] LabialNasals = ["m", "ɱ", "n", "ŋ"];
    internal static readonly string[] VelarNasals = ["ŋ", "ɴ", "ɲ", "n", "m"];
    internal static readonly string[] Palatals = ["j", "ʝ"];
    internal static readonly string[] Labials = ["w", "ʋ"];
    internal static readonly string[] Velars = ["ɰ", "ɣ"];
    internal static readonly string[] Pharyngeals = ["ʕ", "ħ"];
    internal static readonly string[] VoicelessAspiration = ["h", "ɦ"];
    internal static readonly string[] VoicedAspiration = ["ɦ", "h"];
    internal static readonly string[] Laterals = ["l", "ɭ", "ʎ"];
    internal static readonly string[] Rhotics = ["ɹ", "r", "ɾ"];
    internal static readonly string[] Schwas = ["ə", "ɘ", "ɜ"];
}
