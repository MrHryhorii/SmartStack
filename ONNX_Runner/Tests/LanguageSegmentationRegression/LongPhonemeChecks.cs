using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Services.Text;
using ONNX_Runner.Services.Text.Phonemization;

internal static class LongPhonemeChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        var config = new PiperConfig
        {
            Espeak = new EspeakConfig { Voice = "en-us" },
            PhonemeIdMap = new Dictionary<string, int[]> { ["."] = [1], ["?"] = [2], ["!"] = [3], [" "] = [4] }
        };
        var mapper = new DynamicPunctuationMapper(config);
        var detector = new MixedLanguagePhonemizer(new PhonemizerSettings(), "en-us", NullLogger<MixedLanguagePhonemizer>.Instance);
        string[] bodies =
        [
            string.Join(" ", Enumerable.Repeat("həˈloʊ", 50)),
            "ɪ" + new string('a', 300),
            string.Concat(Enumerable.Repeat("t\u0361sa˥˩n\u0329ˈkaʰ", 40)),
            string.Join(", ", Enumerable.Repeat("ɪ?! ə", 40)),
            string.Join("\n", Enumerable.Repeat("həˈloʊ", 40))
        ];
        foreach (var (Open, Close) in new[] { (Open: "[", Close: "]"), (Open: "[[", Close: "]]"), (Open: "/", Close: "/") })
        foreach (string body in bodies)
        foreach (int limit in new[] { 8, 16, 64, 200 })
        foreach (bool early in new[] { false, true })
        foreach (bool useDetector in new[] { false, true })
        {
            yield return ($"Long raw phonemes stay on the raw route: {Open}; payload={body.Length}; cap={limit}; early={early}; detector={useDetector}", () =>
            {
                var bridge = new EspeakWrapper();
                var phonemizer = new UnifiedPhonemizer(bridge, mapper, config, useDetector ? detector : null);
                var chunks = new TextChunker(new ChunkerSettings { MaxChunkLength = limit })
                    .Split(Open + body + Close, early);
                string actual = string.Concat(chunks.Select(c => phonemizer.GetPhonemes(c.Text, "en")));
                Require(chunks.Count > 1 && actual == body, "Phoneme slicing lost content, spacing or raw semantics.");
                Require(bridge.Calls.Count == 0 && bridge.CharacterCalls.Count == 0 && bridge.EmojiCalls.Count == 0,
                    "A split raw-phoneme fragment was sent through native phonemization.");
                Require(chunks.All(c => c.Text.Length <= limit && !c.IsSentenceFinished), "A block exceeded its cap or completed a thought internally.");
            });
        }

        foreach (bool useDetector in new[] { false, true })
        foreach (int limit in new[] { 8, 16, 64, 200 })
        {
            yield return ($"Long explicit ASCII fragments remain phonemes: cap={limit}; detector={useDetector}", () =>
            {
                string body = string.Concat(Enumerable.Repeat("kasa ", 80));
                var bridge = new EspeakWrapper();
                var phonemizer = new UnifiedPhonemizer(bridge, mapper, config, useDetector ? detector : null);
                var chunks = new TextChunker(new ChunkerSettings { MaxChunkLength = limit }).Split("[[" + body + "]]");
                Require(string.Concat(chunks.Select(c => phonemizer.GetPhonemes(c.Text, "en"))) == body && bridge.Calls.Count == 0,
                    "An ASCII-only explicit override lost its pronunciation contract.");
            });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
