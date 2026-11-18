using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Services.Text;
using ONNX_Runner.Services.Text.Phonemization;

internal static class SingleBracketPhonemeChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        var config = Config();
        var mapper = new DynamicPunctuationMapper(config);
        var detector = new MixedLanguagePhonemizer(new PhonemizerSettings { SupportedLanguages = ["en", "uk"] },
            "en-us", NullLogger<MixedLanguagePhonemizer>.Instance);

        string[] phonemes =
        [
            "həˈloʊ", "n\u0329", "t\u0361s", "t\u035Cs", "t\u032A", "e\u032F", "t\u031A",
            "a˥˩", "pʰa", "ɐ\u0303", "ɪː", "ˈkat", "æː", "øː", "çʰ", "ðː", "ŋ\u0329",
            "həˈloʊ wɜːld", "həˈloʊ, wɜːld", "ə. baʊt", "həˈloʊ\nwɜːld", "ɪ\tɐ",
            "ẽː", "e\u0303ː", "ïː", "i\u0308ː", "ḛ", "e\u0330", "c\u0327ʰ", "bʱa", "ʰp",
            "ˈʰpa", "ⁿd", "ŋ\u030A"
        ];
        string[] ordinary =
        [
            "hello", "kat", "test", "123", "før", "sø", "cœur", "ça", "blåbær", "það",
            "ə notes", "use ə here", "please use ə here", "həˈloʊ report", "Фонема ə",
            "Note: use θ here", "\u0329n", "ːn", "ˈ", "həˈ", "ɪ_ə", "ɪ/ə", "ɪ\\ə",
            "ɪ=ə", "ɪ@ə", "ɪ2", "hə\U0001F642", "a\u0303", "ã", "a\u0301", "á", "ʃ\uD800", "t\u0361", "t\u035C"
        ];
        foreach (bool useDetector in new[] { false, true })
        {
            foreach (string body in phonemes)
            {
                yield return ($"Single bracket IPA accepted: {body}; detector={useDetector}", () =>
                {
                    var bridge = new EspeakWrapper();
                    var phonemizer = new UnifiedPhonemizer(bridge, mapper, config, useDetector ? detector : null);
                    string actual = phonemizer.GetPhonemes("[" + body + "]", "en");
                    Require(actual == body && bridge.Calls.Count == 0 && bridge.CharacterCalls.Count == 0 && bridge.EmojiCalls.Count == 0,
                        "A confident IPA block reached ordinary phonemization or changed its content.");
                }
                );
            }
            foreach (string body in ordinary)
            {
                yield return ($"Single bracket text retained: {body}; detector={useDetector}", () =>
                {
                    var bridge = new EspeakWrapper();
                    var phonemizer = new UnifiedPhonemizer(bridge, mapper, config, useDetector ? detector : null);
                    _ = phonemizer.GetPhonemes("[" + body + "]", "en");
                    Require(bridge.Calls.Count + bridge.CharacterCalls.Count + bridge.EmojiCalls.Count > 0,
                        "Ambiguous or non-IPA content bypassed ordinary phonemization.");
                }
                );
            }

            foreach (string input in new[]
            {
                "array[ɪ]", "[ɪ].Length", "[ɪ]name", "\\[ɪ]", "`[ɪ]`", "![ɪ](image.png)",
                "[ɪ](https://example.test)", "C:\\[ɪ]\\file.txt"
            })
            {
                yield return ($"Single bracket technical context retained: {input}; detector={useDetector}", () =>
                {
                    var bridge = new EspeakWrapper();
                    var phonemizer = new UnifiedPhonemizer(bridge, mapper, config, useDetector ? detector : null);
                    _ = phonemizer.GetPhonemes(input, "en");
                    Require(bridge.Calls.Any(c => c.Text.Contains('ɪ')) || bridge.CharacterCalls.Any(c => c.Character == 'ɪ'),
                        "Technical context was interpreted as an implicit IPA override.");
                }
                );
            }

            foreach (string whitespace in new[] { "", " ", "\t", "\n" })
                foreach (var (Open, Close) in new[] { (Open: "[", Close: "]"), (Open: "[[", Close: "]]") })
                {
                    yield return ($"Raw phoneme word separator preserved: {Open}; U+{(whitespace.Length > 0 ? (int)whitespace[0] : 0):X4}; detector={useDetector}", () =>
                    {
                        var bridge = new EspeakWrapper();
                        var phonemizer = new UnifiedPhonemizer(bridge, mapper, config, useDetector ? detector : null);
                        string actual = phonemizer.GetPhonemes(Open + "ɪ" + Close + whitespace + Open + "ə" + Close, "en");
                        Require(actual == "ɪ" + whitespace + "ə" && bridge.Calls.Count == 0,
                            "Original word separation was lost or an adjacent block became ordinary text.");
                    }
                    );
                }

            yield return ($"Single bracket nested explicit override retains priority; detector={useDetector}", () =>
            {
                var bridge = new EspeakWrapper();
                var phonemizer = new UnifiedPhonemizer(bridge, mapper, config, useDetector ? detector : null);
                string actual = phonemizer.GetPhonemes("[note [[kat]]]", "en");
                Require(actual.Contains("kat", StringComparison.Ordinal) && !actual.Contains("[[", StringComparison.Ordinal),
                    "An ordinary outer bracket consumed an explicit override.");
                Require(bridge.Calls.All(c => !c.Text.Contains("kat", StringComparison.Ordinal)), "Explicit phonemes reached eSpeak.");
            }
            );
            yield return ($"Single bracket explicit ASCII override remains available; detector={useDetector}", () =>
            {
                var bridge = new EspeakWrapper();
                var phonemizer = new UnifiedPhonemizer(bridge, mapper, config, useDetector ? detector : null);
                Require(phonemizer.GetPhonemes("[[kat]]", "en") == "kat" && bridge.Calls.Count == 0,
                    "The explicit ASCII phoneme contract changed.");
            }
            );

            foreach (bool early in new[] { false, true })
                foreach (int limit in new[] { 16, 64, 200 })
                {
                    yield return ($"Single bracket complete pipeline; detector={useDetector}; early={early}; cap={limit}", () =>
                    {
                        string body = string.Join(" ", Enumerable.Repeat("həˈloʊ", 40));
                        string block = "[" + body + "]";
                        var chunker = new TextChunker(new ChunkerSettings { MaxChunkLength = limit });
                        var bridge = new EspeakWrapper();
                        var phonemizer = new UnifiedPhonemizer(bridge, mapper, config, useDetector ? detector : null);
                        var chunks = chunker.Split("Say " + block + " now. Next.", early);
                        string[] rendered = chunks.Select(c => phonemizer.GetPhonemes(c.Text, "en")).ToArray();
                        Require(chunks.Count(c => c.Text.Contains(block, StringComparison.Ordinal)) == 1,
                            "A recognized IPA block was split or duplicated.");
                        Require(rendered.Any(value => value.Contains(body, StringComparison.Ordinal)) &&
                            bridge.Calls.All(c => !c.Text.Contains("həˈloʊ", StringComparison.Ordinal)),
                            "Chunking sent pre-written IPA to eSpeak.");
                        Require(chunks.Count(c => c.IsSentenceFinished) == 2 && chunks[^1].IsSentenceFinished,
                            "The IPA block changed sentence completion flags.");
                    }
                    );
                }
        }

        yield return ("Single bracket IPA uses the same model adaptation as explicit IPA", () =>
        {
            var restricted = Config();
            restricted.PhonemeIdMap["t"] = [20];
            restricted.PhonemeIdMap["s"] = [21];
            var fallback = new PhonemeFallbackMapper(Path.Combine("PHOIBLE", "phoible.csv"), restricted);
            var bridge = new EspeakWrapper();
            var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(restricted), restricted, fallbackMapper: fallback);
            string expected = phonemizer.GetPhonemes("[[t\u0361s]]", "en");
            Require(expected.Length > 0 && phonemizer.GetPhonemes("[t\u0361s]", "en") == expected && bridge.Calls.Count == 0,
                "Implicit IPA bypassed model adaptation or used ordinary pronunciation.");
        }
        );
    }

    private static PiperConfig Config() => new()
    {
        Espeak = new EspeakConfig { Voice = "en-us" },
        PhonemeIdMap = new Dictionary<string, int[]>
        {
            ["."] = [1],
            ["?"] = [2],
            ["!"] = [3],
            [","] = [4],
            [" "] = [5],
            ["["] = [6],
            ["]"] = [7],
            [":"] = [8],
            [";"] = [9],
            ["("] = [10],
            [")"] = [11]
        }
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
