using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Services.Text;
using ONNX_Runner.Services.Text.Phonemization;
using ONNX_Runner.Services.Text.Phonemization.Adaptation;

internal static class PhonemeAdaptationChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        var config = Config(new()
        {
            ["a"] = [10], ["i"] = [11], ["ɛ"] = [12], ["ː"] = [13], ["n"] = [14],
            ["p"] = [15], ["j"] = [16], ["h"] = [17], ["ɡ"] = [18], ["ɦ"] = [19],
            ["k"] = [20], ["w"] = [21], ["t"] = [22], ["s"] = [23], ["ʃ"] = [24],
            ["r"] = [25], ["ə"] = [26], ["e"] = [27], ["l"] = [28], ["z"] = [29],
            ["kʷ"] = [30, 31], ["!"] = [32], ["."] = [33], ["ˈ"] = [34]
        });
        var fallback = new PhonemeFallbackMapper(Path.Combine("PHOIBLE", "phoible.csv"), config);
        var tensor = Tensor(config);
        var vectors = new (string Input, string Expected, long[] Body)[]
        {
            ("kʷʰ", "kʷh", [30, 31, 17, 0]),
            ("pʲʰ", "pjh", [15, 16, 17, 0]),
            ("aɪ̃", "ain", [10, 11, 14, 0]),
            ("ɛ̃ː", "ɛːn", [12, 13, 14, 0]),
            ("ɡʰ", "ɡɦ", [18, 19, 0]),
            ("gʰ", "ɡɦ", [18, 19, 0]),
            ("ã", "an", [10, 14, 0]),
            ("a\u0303", "an", [10, 14, 0]),
            ("t\u0361s", "ts", [22, 23, 0]),
            ("t\u035Cs", "ts", [22, 23, 0]),
            ("ʦ", "ts", [22, 23, 0]),
            ("ʧ", "tʃ", [22, 24, 0]),
            ("n\u0329", "ən", [26, 14, 0]),
            ("kʲʷʰ", "kjwh", [20, 16, 21, 17, 0]),
            ("ʦʧ", "tstʃ", [22, 23, 0, 22, 24, 0]),
            ("ʦ ʧ", "ts tʃ", [22, 23, 0, 3, 0, 22, 24, 0]),
            ("tʃ", "tʃ", [22, 0, 24, 0]),
            ("kʷ", "kʷ", [30, 31, 0]),
            ("ˈkata", "ˈkata", [34, 0, 20, 0, 10, 0, 22, 0, 10, 0])
        };
        foreach (var (Input, Expected, Body) in vectors)
        {
            yield return ($"Prepared phoneme adaptation: {Input}", () =>
            {
                var actual = fallback.Adapter.Adapt(Input);
                Require(actual.Text == Expected, $"Expected '{Expected}', received '{actual.Text}'.");
                Equal(tensor.PhonemesToIds(actual, true, false), [1, 0, .. Body, 2]);
            });
        }

        foreach (string language in new[] { "en", "uk", "fr", "de", "ja", "ar", "nb" })
        {
            yield return ($"Raw adaptation is independent of language selection: {language}", () =>
            {
                var bridge = new EspeakWrapper();
                var pipeline = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, fallbackMapper: fallback);
                var actual = pipeline.GetSequence("[[kʷʰ aɪ̃]]", language);
                Equal(tensor.PhonemesToIds(actual, true, false), [1, 0, 30, 31, 17, 0, 3, 0, 10, 11, 14, 0, 2]);
                Require(bridge.Calls.Count == 0, "Explicit IPA was sent to the orthographic phonemizer.");
            });
        }

        foreach (bool continuation in new[] { false, true })
        foreach (bool finished in new[] { false, true })
        {
            yield return ($"Adapted groups preserve sentence boundary controls: continuation={continuation}; finished={finished}", () =>
            {
                long[] expected = [1, 0, .. (continuation ? Array.Empty<long>() : [3, 0]),
                    22, 23, 0, .. (finished ? new long[] { 3, 0 } : []), 2];
                Equal(tensor.PhonemesToIds(fallback.Adapter.Adapt("ʦ"), continuation, finished), expected);
            });
        }

        foreach (int limit in new[] { 4, 6, 7, 8, 11, 16, 64, 199, 200, 201 })
        foreach (bool early in new[] { false, true })
        foreach (string unit in new[] { "aɪ", "kʷʰ", "aɪ̃", "kʲʷʰ", "t\u035Cs" })
        {
            yield return ($"Chunked adaptation equals the whole tensor: {unit}; cap={limit}; early={early}", () =>
            {
                string body = "t" + string.Concat(Enumerable.Repeat(unit, 120));
                var chunker = new TextChunker(new ChunkerSettings { MaxChunkLength = limit },
                    phonemeIdMap: config.PhonemeIdMap, phonemeAdapter: fallback.Adapter);
                var chunks = chunker.Split("[[" + body + "]]", early);
                var bridge = new EspeakWrapper();
                var pipeline = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, fallbackMapper: fallback);
                var actual = new List<long>();
                foreach (var chunk in chunks)
                {
                    var sequence = pipeline.GetSequence(chunk.Text);
                    actual.AddRange(tensor.PhonemesToIds(sequence, true, false)[2..^1]);
                    Require(!chunk.IsSentenceFinished, "An internal IPA cut completed the thought.");
                    Require(chunk.Text.Length <= limit || sequence.Text.Length <= unit.Length + 1,
                        "A feasible fragment cap was exceeded.");
                }
                Equal(actual, tensor.PhonemesToIds(fallback.Adapter.Adapt(body), true, false)[2..^1]);
                Require(bridge.Calls.Count == 0, "A raw fragment changed its pronunciation route.");
            });
        }

        yield return ("Native complex tokens take priority over decomposition", () =>
        {
            var nativeConfig = Config(new() { ["a"] = [10], ["n"] = [11], ["ã"] = [40, 41], ["aɪ̃"] = [50, 51] });
            var native = new PhonemeFallbackMapper("missing-phoneme-test.csv", nativeConfig);
            var sequence = native.Adapter.Adapt("ãaɪ̃");
            Require(sequence.Text == "ãaɪ̃", "A native whole sound was changed.");
            Equal(Tensor(nativeConfig).PhonemesToIds(sequence, true, false), [1, 0, 40, 41, 0, 50, 51, 0, 2]);
        });

        yield return ("Canonical spelling preserves supported component padding", () =>
        {
            var nativeConfig = Config(new() { ["a"] = [10, 11], ["\u0303"] = [12] });
            var native = new PhonemeFallbackMapper("missing-phoneme-test.csv", nativeConfig);
            var encoder = Tensor(nativeConfig);
            Equal(encoder.PhonemesToIds(native.Adapter.Adapt("ã"), true, false), [1, 0, 10, 11, 0, 12, 0, 2]);
            Equal(encoder.PhonemesToIds(native.Adapter.Adapt("a\u0303"), true, false), [1, 0, 10, 11, 0, 12, 0, 2]);
        });

        yield return ("Structural adaptation remains available without PHOIBLE", () =>
        {
            var minimalConfig = Config(new() { ["t"] = [10, 11], ["s"] = [12], ["p"] = [13], ["j"] = [14], ["h"] = [15] });
            var minimal = new PhonemeFallbackMapper("missing-phoneme-test.csv", minimalConfig);
            Equal(Tensor(minimalConfig).PhonemesToIds(minimal.Adapter.Adapt("ʦpʲʰ"), true, false),
                [1, 0, 10, 11, 12, 0, 13, 14, 15, 0, 2]);
        });

        yield return ("Nasal modifiers preserve their place of articulation", () =>
        {
            var nasalConfig = Config(new() { ["m"] = [10], ["n"] = [11], ["ŋ"] = [12], ["b"] = [13], ["ɡ"] = [14] });
            var nasal = new PhonemeFallbackMapper("missing-phoneme-test.csv", nasalConfig);
            Equal(Tensor(nasalConfig).PhonemesToIds(nasal.Adapter.Adapt("ᵐb ᵑɡ bᵐ"), true, false),
                [1, 0, 10, 13, 0, 3, 0, 12, 14, 0, 3, 0, 13, 10, 0, 2]);
        });

        yield return ("Unlisted combinations retain explicit voicing changes", () =>
        {
            var voiceConfig = Config(new() { ["b"] = [10], ["p"] = [11], ["j"] = [12], ["w"] = [13], ["h"] = [14], ["ɦ"] = [15] });
            var voice = new PhonemeFallbackMapper(Path.Combine("PHOIBLE", "phoible.csv"), voiceConfig);
            Equal(Tensor(voiceConfig).PhonemesToIds(voice.Adapter.Adapt("b\u0325ʲʷʰ"), true, false), [1, 0, 11, 12, 13, 14, 0, 2]);
            Equal(Tensor(voiceConfig).PhonemesToIds(voice.Adapter.Adapt("p\u032Cʲʷʰ"), true, false), [1, 0, 10, 12, 13, 15, 0, 2]);
        });

        yield return ("Adaptation preserves complete control and component ID arrays", () =>
        {
            var completeConfig = Config(new()
            {
                ["^"] = [7, 8], ["_"] = [90, 91], ["$"] = [9, 10], [" "] = [3, 4], ["t"] = [20, 21], ["s"] = [22, 23]
            });
            var complete = new PhonemeFallbackMapper("missing-phoneme-test.csv", completeConfig);
            Equal(Tensor(completeConfig).PhonemesToIds(complete.Adapter.Adapt("ʦ")),
                [7, 8, 90, 91, 3, 4, 90, 91, 20, 21, 22, 23, 90, 91, 3, 4, 90, 91, 9, 10]);
        });

        yield return ("Typed interface dispatch preserves joined encodings", () =>
        {
            IPhonemizer encoder = tensor;
            Equal(encoder.PhonemesToIds(fallback.Adapter.Adapt("ʦ"), true, false), [1, 0, 22, 23, 0, 2]);
            Equal(encoder.PhonemesToIds(default(PhonemeSequence), true, false), [1, 0, 2]);
        });

        yield return ("Canonical aliases retain native space semantics", () =>
        {
            var aliasConfig = Config(new() { ["ɡ"] = [3] });
            var alias = new PhonemeFallbackMapper("missing-phoneme-test.csv", aliasConfig);
            Equal(Tensor(aliasConfig).PhonemesToIds(alias.Adapter.Adapt("g"), true, true), [1, 0, 3, 0, 2]);
        });

        yield return ("Prepared tensors remain stable under parallel requests", () =>
        {
            long[] expected = [1, 0, 30, 31, 17, 0, 3, 0, 20, 16, 21, 17, 0, 2];
            Parallel.For(0, 128, _ => Equal(tensor.PhonemesToIds(fallback.Adapter.Adapt("kʷʰ kʲʷʰ"), true, false), expected));
        });

        yield return ("Unlisted modifier runs have bounded output growth", () =>
        {
            var minimalConfig = Config(new() { ["k"] = [10], ["j"] = [11], ["w"] = [12], ["h"] = [13] });
            var minimal = new PhonemeFallbackMapper("missing-phoneme-test.csv", minimalConfig);
            string input = "k" + new string('ː', 2048) + "ʲʷʰ";
            var sequence = minimal.Adapter.Adapt(input);
            Require(sequence.Text.StartsWith("kk", StringComparison.Ordinal) && sequence.Text.Length < 16,
                "A modifier run recursively expanded the output.");
            Require(Tensor(minimalConfig).PhonemesToIds(sequence, true, false).Length < 24, "A modifier run expanded the tensor without a bound.");
            Equal(Tensor(minimalConfig).PhonemesToIds(minimal.Adapter.Adapt("kʲʷː"), true, false), [1, 0, 10, 10, 11, 12, 0, 2]);
            Equal(Tensor(minimalConfig).PhonemesToIds(minimal.Adapter.Adapt("kʲʷˑ"), true, false), [1, 0, 10, 10, 11, 12, 0, 2]);
        });

        foreach (string input in new[] { "", "\uD800", "\uD800a", "\uDC00ʦ", "\u0361", "ʰ", "\r\n", "  " })
            yield return ($"Malformed or nonsegment IPA remains safe: {string.Join('-', input.Select(value => ((int)value).ToString("X4")))}", () =>
            {
                var sequence = fallback.Adapter.Adapt(input);
                Require(sequence.Text.Length <= input.Length * 2 + 2, "Invalid input expanded unexpectedly.");
                tensor.PhonemesToIds(sequence);
            });

        foreach (string source in new[] { "ʦ", "kʲʷʰ" })
        yield return ($"Technical character cache retains adaptation groups: {source}", () =>
        {
            var bridge = new EspeakWrapper();
            bridge.CharacterResults['_'] = source;
            var pipeline = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, fallbackMapper: fallback);
            var first = pipeline.GetSequence("a_a");
            int calls = bridge.CharacterCalls.Count;
            var second = pipeline.GetSequence("a_a");
            Equal(tensor.PhonemesToIds(first), tensor.PhonemesToIds(second));
            Require(first.Adaptations.Length > 0 && bridge.CharacterCalls.Count == calls,
                "The cached character lost its group or requested a second native transcription.");
            Require(first.Adaptations.AllJoined(), "The cached character was re-encoded with internal padding.");
        });

        foreach (string source in new[] { "ʦ", "kʲʷʰ" })
        yield return ($"Emoji name cache retains adaptation groups: {source}", () =>
        {
            const string symbol = "\U0001F600";
            var bridge = new EspeakWrapper();
            bridge.EmojiResults[symbol] = source;
            var pipeline = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, fallbackMapper: fallback);
            var first = pipeline.GetSequence(symbol);
            var second = pipeline.GetSequence(symbol);
            Equal(tensor.PhonemesToIds(first), tensor.PhonemesToIds(second));
            Require(bridge.EmojiCalls.Count == 1 && first.Adaptations.Length > 0 && second.Adaptations.Length > 0,
                "The cached name lost its prepared group.");
        });

        foreach (var test in FeatureChecks()) yield return test;
    }

    private static IEnumerable<(string Name, Action Check)> FeatureChecks()
    {
        yield return ("PHOIBLE retains contour phases and segment classes", () => WithCatalog(path =>
        {
            var catalog = new PhoibleCatalog(path);
            Require(catalog.TryGet("x", out var source) && source.Kind == PhonemeKind.Vowel && source.Steps == 2,
                "Segment class or contour phase count was discarded.");
            Require(source.Features[1].SequenceEqual(new sbyte[] { -1, 1 }), "The second feature phase was discarded.");
            Require(catalog.TryGet("s", out var consonant) && consonant.Kind == PhonemeKind.Consonant,
                "Consonant classification was discarded.");
            Require(catalog.TryGet("˥", out var tone) && tone.Kind == PhonemeKind.Tone, "Tone classification was discarded.");
        }));

        yield return ("PHOIBLE matching uses the entire contour", () => WithCatalog(path =>
        {
            var config = Config(new() { ["a"] = [10], ["i"] = [11], ["s"] = [12] });
            var fallback = new PhonemeFallbackMapper(path, config);
            var sequence = fallback.Adapter.Adapt("x");
            Require(sequence.Text == "i", "Matching ignored the second feature phase or segment class.");
            Equal(Tensor(config).PhonemesToIds(sequence, true, false), [1, 0, 11, 0, 2]);
        }));

        yield return ("Unknown PHOIBLE values differ from negative features", () => WithCatalog(path =>
        {
            var catalog = new PhoibleCatalog(path);
            var source = new PhonemeProfile(PhonemeKind.Vowel, [[0], [1], [0]]);
            var negative = new PhonemeProfile(PhonemeKind.Vowel, [[0], [-1], [0]]);
            var unknown = new PhonemeProfile(PhonemeKind.Vowel, [[0], [0], [0]]);
            Require(catalog.Distance(source, unknown, int.MaxValue) < catalog.Distance(source, negative, int.MaxValue),
                "An unspecified feature was treated as a definite negative value.");
        }));

        yield return ("Cascade considers native suffixes after a dead prefix", () => WithCatalog(path =>
        {
            var config = Config(new() { ["a"] = [10], ["aɪ"] = [20], ["ɪə"] = [30, 31] });
            var fallback = new PhonemeFallbackMapper(path, config);
            Equal(Tensor(config).PhonemesToIds(fallback.Adapter.Adapt("aɪə"), true, false), [1, 0, 10, 0, 30, 31, 0, 2]);
        }));

        yield return ("Cascade retains a supported compound prefix", () => WithCatalog(path =>
        {
            var config = Config(new() { ["aɪ"] = [20, 21], ["ə"] = [30] });
            var fallback = new PhonemeFallbackMapper(path, config);
            Equal(Tensor(config).PhonemesToIds(fallback.Adapter.Adapt("aɪə"), true, false), [1, 0, 20, 21, 0, 30, 0, 2]);
        }));
    }

    private static void WithCatalog(Action<string> check)
    {
        string path = Path.Combine(Path.GetTempPath(), $"phoneme-adaptation-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "Phoneme,SegmentClass,tone,high,periodicGlottalSource\n" +
                "x,vowel,-,\"-,+\",+\na,vowel,-,-,+\ni,vowel,-,\"-,+\",+\ns,consonant,-,\"-,+\",+\n" +
                "˥,tone,+,0,0\naɪə,vowel,-,\"-,+,-\",+\n");
            check(path);
        }
        finally { File.Delete(path); }
    }

    private static PiperConfig Config(Dictionary<string, int[]> tokens)
    {
        var config = new PiperConfig { PhonemeIdMap = new() { ["_"] = [0], ["^"] = [1], ["$"] = [2], [" "] = [3] } };
        foreach (var (text, ids) in tokens) config.PhonemeIdMap[text] = ids;
        return config;
    }

    private static PiperPhonemizer Tensor(PiperConfig config) => new(config, NullLogger<PiperPhonemizer>.Instance);
    private static void Equal(IEnumerable<long> actual, long[] expected) => Require(actual.SequenceEqual(expected),
        $"Expected [{string.Join(',', expected)}], received [{string.Join(',', actual)}].");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool AllJoined(this ReadOnlySpan<AdaptedSpan> spans)
    {
        foreach (var span in spans) if (!span.Joined) return false;
        return true;
    }
}
