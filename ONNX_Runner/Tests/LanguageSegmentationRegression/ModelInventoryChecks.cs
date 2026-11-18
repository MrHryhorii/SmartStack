using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Services.Text;
using ONNX_Runner.Services.Text.Phonemization;

internal static class ModelInventoryChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        var vectors = new (string Name, string Text, Dictionary<string, int[]> Map, long[] Expected)[]
        {
            ("Every ID of one token is retained", "a", new() { ["a"] = [20, 21] }, [1, 0, 20, 21, 0, 2]),
            ("Padding separates complete token mappings", "ab", new() { ["a"] = [20, 21], ["b"] = [22, 23] }, [1, 0, 20, 21, 0, 22, 23, 0, 2]),
            ("Separate combining-mark tokens are retained", "ɛ\u0303", new() { ["ɛ"] = [20], ["\u0303"] = [21] }, [1, 0, 20, 0, 21, 0, 2]),
            ("A complete combining token wins", "ɛ\u0303", new() { ["ɛ"] = [20], ["\u0303"] = [21], ["ɛ\u0303"] = [30] }, [1, 0, 30, 0, 2]),
            ("A complete diphthong token wins", "aɪ", new() { ["a"] = [20], ["ɪ"] = [21], ["aɪ"] = [30] }, [1, 0, 30, 0, 2]),
            ("A tied affricate uses its complete mapping", "t\u0361s", new() { ["t\u0361s"] = [30, 31] }, [1, 0, 30, 31, 0, 2]),
            ("Supplementary tokens stay complete", "\U00010428", new() { ["\U00010428"] = [30] }, [1, 0, 30, 0, 2]),
            ("Unknown scalars do not consume the next known token", "x\uD800a", new() { ["a"] = [20] }, [1, 0, 20, 0, 2]),
            ("CRLF supplies one word boundary", "a\r\nb", new() { ["a"] = [20], ["b"] = [21] }, [1, 0, 20, 0, 3, 0, 21, 0, 2]),
            ("Tabs supply one word boundary", "a\t\tb", new() { ["a"] = [20], ["b"] = [21] }, [1, 0, 20, 0, 3, 0, 21, 0, 2]),
            ("Model-native whitespace remains available", "a\u00A0b", new() { ["a"] = [20], ["\u00A0"] = [25], ["b"] = [21] }, [1, 0, 20, 0, 25, 0, 21, 0, 2]),
            ("Empty model mappings do not crash or become supported", "ab", new() { ["a"] = [], ["b"] = [21] }, [1, 0, 21, 0, 2]),
            ("Control mappings retain every ID", "a", new() { ["^"] = [7, 8], ["_"] = [90, 91], ["$"] = [9, 10], ["a"] = [20, 21] }, [7, 8, 90, 91, 20, 21, 90, 91, 9, 10])
        };
        foreach (var (Name, Text, Map, Expected) in vectors)
        {
            yield return ($"Model inventory: {Name}", () =>
            {
                var config = Config(Map);
                var mapper = new PiperPhonemizer(config, NullLogger<PiperPhonemizer>.Instance);
                long[] actual = mapper.PhonemesToIds(Text, isContinuation: true, isFinished: false);
                Require(actual.SequenceEqual(Expected), $"Expected [{string.Join(',', Expected)}], received [{string.Join(',', actual)}].");
            });
        }

        foreach (bool parallel in new[] { false, true })
        {
            yield return ($"Model inventory: pooled tensor growth retains mappings; parallel={parallel}", () =>
            {
                var config = Config(new() { ["a"] = [20, 21] });
                var mapper = new PiperPhonemizer(config, NullLogger<PiperPhonemizer>.Instance);
                string input = new('a', 4096);
                long[] expected = [1, 0, .. Enumerable.Repeat(new long[] { 20, 21, 0 }, 4096).SelectMany(ids => ids), 2];
                void Check(int _) => Require(mapper.PhonemesToIds(input, true, false).SequenceEqual(expected),
                    "Growing or reusing a temporary buffer corrupted the tensor.");
                if (parallel) Parallel.For(0, 32, Check);
                else for (int index = 0; index < 8; index++) Check(index);
            });
        }

        foreach (bool continuation in new[] { false, true })
        foreach (bool finished in new[] { false, true })
        {
            yield return ($"Model inventory: established sentence spaces; continuation={continuation}; finished={finished}", () =>
            {
                var config = Config(new() { ["a"] = [20], ["b"] = [21] });
                long[] expected = [1, 0, .. (continuation ? Array.Empty<long>() : [3, 0]), 20, 0, 3, 0, 21, 0,
                    .. (finished ? new long[] { 3, 0 } : []), 2];
                var mapper = new PiperPhonemizer(config, NullLogger<PiperPhonemizer>.Instance);
                Require(mapper.PhonemesToIds("a b", continuation, finished).SequenceEqual(expected), "The established sentence-space contract changed.");
            });
        }

        foreach (bool supportedMark in new[] { false, true })
        {
            yield return ($"Model inventory: nasal adaptation follows native support; combining-mark={supportedMark}", () =>
            {
                var config = Config(new() { ["ɛ"] = [20], ["n"] = [22] });
                if (supportedMark) config.PhonemeIdMap["\u0303"] = [21];
                var fallback = new PhonemeFallbackMapper(Path.Combine("PHOIBLE", "phoible.csv"), config);
                var bridge = new EspeakWrapper();
                var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, fallbackMapper: fallback);
                string actual = phonemizer.GetPhonemes("[ɛ\u0303]", "en");
                string expected = supportedMark ? "ɛ\u0303" : "ɛn";
                Require(actual == expected && bridge.Calls.Count == 0, "Native phonemes were unnecessarily degraded or unsupported phonemes bypassed adaptation.");
                var mapper = new PiperPhonemizer(config, NullLogger<PiperPhonemizer>.Instance);
                long[] ids = mapper.PhonemesToIds(actual, true, false);
                Require(ids.SequenceEqual(supportedMark ? [1, 0, 20, 0, 21, 0, 2] : [1, 0, 20, 0, 22, 0, 2]),
                    "Adapted phonemes lost IDs at the tensor boundary.");
            });
        }

        foreach (int limit in new[] { 4, 6, 7, 8, 11, 16, 64, 200 })
        foreach (bool early in new[] { false, true })
        {
            yield return ($"Model inventory: chunking preserves native tokens and all IDs; cap={limit}; early={early}", () =>
            {
                var config = Config(new() { ["a"] = [10], ["ɪ"] = [11], ["aɪ"] = [30, 31], ["e"] = [20], ["\u0303"] = [21] });
                string body = string.Concat(Enumerable.Repeat("aɪe\u0303aɪ", 40));
                var chunks = new TextChunker(new ChunkerSettings { MaxChunkLength = limit }, phonemeIdMap: config.PhonemeIdMap)
                    .Split("[[" + body + "]]", early);
                var bridge = new EspeakWrapper();
                var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config);
                var tensor = new PiperPhonemizer(config, NullLogger<PiperPhonemizer>.Instance);
                var actual = new List<long>();
                foreach (var chunk in chunks)
                {
                    string ipa = phonemizer.GetPhonemes(chunk.Text, "en");
                    long[] ids = tensor.PhonemesToIds(ipa, true, false);
                    actual.AddRange(ids[2..^1]);
                    Require(!chunk.IsSentenceFinished && (chunk.Text.Length <= limit || ipa is "aɪ" or "e\u0303"),
                        "Chunking exceeded a feasible cap or finished the sentence internally.");
                }
                long[] expected = Enumerable.Range(0, 40).SelectMany(_ => new long[] { 30, 31, 0, 20, 0, 21, 0, 30, 31, 0 }).ToArray();
                Require(actual.SequenceEqual(expected) && bridge.Calls.Count == 0, "Native tokens changed interpretation or lost IDs across a chunk boundary.");
            });
        }

        foreach (string culture in new[] { "en-US", "tr-TR", "az-Latn-AZ", "nb-NO" })
        {
            yield return ($"Language code normalization is independent of {culture}", () =>
            {
                var previous = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    var mapper = new EspeakLinguaMapper();
                    string[] actual = mapper.BuildLinguaList(["HI", "IS", "IT"]).Select(language => language.ToString()).Order().ToArray();
                    Require(actual.SequenceEqual(["Hindi", "Icelandic", "Italian"]) && mapper.GetLinguaLanguage("HI")?.ToString() == "Hindi",
                        "The OS locale changed ISO language codes.");
                }
                finally
                {
                    CultureInfo.CurrentCulture = previous;
                }
            });
        }

        foreach (var (Text, Expected) in new (string, string?)[]
        {
            ("(en) word", "en"), ("(fr) word", "fr"), ("(pt-br) word", "pt-br"), ("(zh-yue) word", "zh-yue"),
            ("(en-gb-x-rp) word", "en-gb-x-rp"), ("(en+f3) word", "en+f3"), ("(123)(fr-ca) word", "fr-ca"),
            ("ordinary IPA", null), ("(12) IPA", null), ("(IPA?) text", null), ("(incomplete", null), ("() text", null)
        })
        {
            yield return ($"Native language-switch metadata: {Text}", () =>
            {
                bool found = PhonemeLanguageLabels.TryFind(Text.AsSpan(), 0, out int start, out int length);
                Require(found == (Expected != null) && (!found || Text.Substring(start, length) == Expected), "Native fallback metadata was read incorrectly.");
            });
        }
    }

    private static PiperConfig Config(Dictionary<string, int[]> additions)
    {
        var config = new PiperConfig
        {
            Espeak = new EspeakConfig { Voice = "en-us" },
            PhonemeIdMap = new Dictionary<string, int[]> { ["_"] = [0], ["^"] = [1], ["$"] = [2], [" "] = [3] }
        };
        foreach (var (token, ids) in additions) config.PhonemeIdMap[token] = ids;
        return config;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
