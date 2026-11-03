namespace ONNX_Runner.Services.Text.Phonemization;

/// <summary>Shared IPA decomposition rules resolved against each model at startup.</summary>
internal static class PhonemeDecompositionCatalog
{
    // Rules to break down complex IPA symbols into simpler phoneme sequences that a model
    // is more likely to support. These rules are resolved against the actual model inventory
    // once at startup; they are never interpreted dynamically during synthesis.
    internal static readonly IReadOnlyDictionary<string, string> Rules =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Affricates (Ligatures and combined symbols)
            {"t͡s", "ts"}, {"t͡ʃ", "tʃ"}, {"d͡z", "dz"}, {"d͡ʒ", "dʒ"},
            {"ʦ", "ts"},  {"ʧ", "tʃ"},  {"ʣ", "dz"},  {"ʤ", "dʒ"},
            {"p͡f", "pf"}, {"k͡x", "kx"}, {"t͡ɕ", "tɕ"}, {"d͡ʑ", "dʑ"},

            // Palatalization (Soft consonants - critical for Slavic languages)
            // Decomposed into "Hard consonant + Jot [j]"
            {"pʲ", "pj"}, {"bʲ", "bj"}, {"mʲ", "mj"}, {"fʲ", "fj"}, {"vʲ", "vj"},
            {"tʲ", "tj"}, {"dʲ", "dj"}, {"nʲ", "nj"}, {"lʲ", "lj"}, {"rʲ", "rj"},
            {"sʲ", "sj"}, {"zʲ", "zj"}, {"kʲ", "kj"}, {"gʲ", "gj"}, {"xʲ", "xj"},

            // Aspiration (Common in Asian, Indian, and Germanic languages)
            // Decomposed into "Main sound + h"
            {"pʰ", "ph"}, {"tʰ", "th"}, {"kʰ", "kh"}, {"cʰ", "ch"}, {"qʰ", "qh"},
            {"bʱ", "bh"}, {"dʱ", "dh"}, {"gʱ", "gh"},

            // Labialization (Rounded consonants)
            // Decomposed into "Main sound + w"
            {"kʷ", "kw"}, {"gʷ", "gw"}, {"xʷ", "xw"}, {"sʷ", "sw"}, {"zʷ", "zw"},

            // Nasalized Vowels (French, Polish, Portuguese)
            // Mapped to "Vowel + n" as the best approximation for nasal quality.
            {"ã", "an"}, {"ẽ", "en"}, {"ĩ", "in"}, {"õ", "on"}, {"ũ", "un"},
            {"ɛ̃", "ɛn"}, {"ɔ̃", "ɔn"}, {"œ̃", "œn"}, {"æ̃", "æn"},

            // Length marks (Gemination)
            // Doubling the sound if the model doesn't support the duration marker ː.
            {"aː", "aa"}, {"eː", "ee"}, {"iː", "ii"}, {"oː", "oo"}, {"uː", "uu"},
            {"sː", "ss"}, {"mː", "mm"}, {"nː", "nn"},

            // Velar Nasal (e.g., "ng" in "singing")
            {"ŋ", "ng"},

            // Syllabic consonants (e.g., "button", "bottle")
            // Decomposed into Schwa [ə] + consonant.
            {"n̩", "ən"}, {"l̩", "əl"}, {"m̩", "əm"},

            // Rhotic vowels (American "er" as in "bird")
            {"ɚ", "ər"}, {"ɝ", "ɜr"},

            // Diphthongs. These rules are activated only when the source sequence cannot
            // already be emitted directly by the loaded model.
            {"aɪ", "ai"}, {"aʊ", "au"}, {"eɪ", "ei"}, {"oʊ", "ou"}, {"ɔɪ", "ɔi"}
        };
}
