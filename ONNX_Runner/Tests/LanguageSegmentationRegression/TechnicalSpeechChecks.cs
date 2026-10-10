using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class TechnicalSpeechChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        var rules = TextChunkerRules.Default;
        var detector = new MixedLanguagePhonemizer(
            new PhonemizerSettings { SupportedLanguages = ["en", "uk"] }, "en-us",
            NullLogger<MixedLanguagePhonemizer>.Instance, new TextChunker(new ChunkerSettings(), rules));

        var fixtures = new (string Text, string Protected, string Symbols)[]
        {
            ("The variable user_name contains the value.", "user_name", "_"),
            ("Read _name, then name_.", "_name", "_"),
            ("Finally, read the symbol _.", "_", "_"),
            ("Compare C++ with a+b.", "C++", "+"),
            ("Use $HOME and a^b.", "$HOME", "$^"),
            ("Evaluate alpha+beta=gamma&delta#test.", "alpha+beta=gamma&delta#test", "+=&#"),
            ("Send test.user+tts@example.co.uk.", "test.user+tts@example.co.uk", ".+@"),
            ("Open https://example.com/a/b?q=hello.world&x=1.25#part-2.",
                "https://example.com/a/b?q=hello.world&x=1.25#part-2", ":/.?=&#-"),
            ("Read C:\\Users\\Test\\build_output\\config.prod.json.",
                "C:\\Users\\Test\\build_output\\config.prod.json", ":\\_."),
            ("Open /home/user/test-data/v1.2.3/config.json.",
                "/home/user/test-data/v1.2.3/config.json", "/-."),
            ("Use foo?.Bar() when the object isn't null.", "foo?.Bar()", "?.()"),
            ("Call System.Text.Json.JsonSerializer.Serialize(obj).",
                "System.Text.Json.JsonSerializer.Serialize(obj)", ".()"),
            ("Maya checked config.prod.json—twice—and whispered.", "config.prod.json", "."),
            ("Open (config.prod.json), then continue.", "config.prod.json", "."),
            ("Read \"config.prod.json\" now.", "config.prod.json", "."),
            ("Check a!=b and a?b:c.", "a!=b", "!=?:"),
            ("Use List<T> and a*b.", "List<T>", "<>*"),
            ("Read data[0]=value and obj={x:1}.", "data[0]=value", "[]= {}:".Replace(" ", "")),
            ("Measure x≤y and a×b.", "x≤y", "≤×"),
            ("The framework is .NET.", ".NET", "."),
            ("Read data[0] and call(obj).", "data[0]", "[]()"),
            ("Open user's_name.txt now.", "user's_name.txt", "'_."),
            ("Compare A-B with A_B.", "A_B", "_"),
            ("Read a±b and C♯.", "a±b", "±♯"),
            ("Use €amount and user＿name.", "€amount", "€＿"),
            ("Open *.json and pass *args to C*.", "*.json", "*."),
        };

        foreach (var fixture in fixtures)
        {
            foreach (bool useDetector in new[] { true, false })
            {
                yield return ($"Technical speech ({useDetector}): {fixture.Text}", () =>
                {
                    var bridge = new EspeakWrapper();
                    var config = Config();
                    var phonemizer = new UnifiedPhonemizer(bridge,
                        new DynamicPunctuationMapper(config, rules), config, useDetector ? detector : null);
                    string result = phonemizer.GetPhonemes(fixture.Text, "en");
                    Require(bridge.CharacterCalls.Select(c => c.Character).ToHashSet()
                        .SetEquals(fixture.Symbols), "Character-name routing differs from the fixture.");
                    Require(bridge.CharacterCalls.All(c => c.Voice == "en-us"), "Symbol voice was reclassified.");
                    Require(bridge.CharacterCalls.GroupBy(c => c.Character).All(g => g.Count() == 1),
                        "Repeated symbols bypassed the adapted-name cache.");
                    Require(result.EndsWith('.'), "Sentence-final punctuation was spoken as a symbol.");

                    var tokens = detector.ProcessTextToLanguageTokens(fixture.Text, "en");
                    Require(string.Concat(tokens.Select(t => t.Text)) == fixture.Text.Replace("\"", string.Empty),
                        "Tokenizer lost content beyond its intentional silent quote boundaries.");
                    Require(tokens.Any(t => t.IsTechnical && t.Text == fixture.Protected),
                        "The real tokenizer did not identify the expected technical token.");
                });
            }
        }

        foreach (string text in new[]
        {
            "Is everything working correctly?", "They don't change well-known words.",
            "Dr. Morgan met Prof. Lee at St. Peter Hospital.", "J. R. R. Tolkien arrived.",
            "The response took 2.75 milliseconds.", "Version v1.0.9 was released at 8:30 a.m.",
            "The server is running at 192.168.1.25:5045.", "Wait... did you hear that?",
            "English-текст-English keeps lexical hyphens.", "Use *emphasis* in prose."
        })
        {
            foreach (bool useDetector in new[] { true, false })
            {
                yield return ($"Ordinary speech ({useDetector}): {text}", () =>
                {
                    var bridge = new EspeakWrapper();
                    var config = Config();
                    var phonemizer = new UnifiedPhonemizer(bridge,
                        new DynamicPunctuationMapper(config, rules), config, useDetector ? detector : null);
                    phonemizer.GetPhonemes(text, "en");
                    Require(bridge.CharacterCalls.Count == 0, "Ordinary punctuation was spelled out.");
                });
            }
        }

        var partsCases = new (string Name, string[] Parts, string Expected)[]
        {
            ("Numeric reference No. 12", ["nˈoʊ", "twˈɛlv"], "nˈoʊ twˈɛlv"),
            ("Figure reference Fig. 3", ["fˈɪɡ", "θɹˈiː"], "fˈɪɡ θɹˈiː"),
            ("Ordered list item one", ["wˈʌn", "ˈoʊpən ðə ɹᵻpˈoːɹt"], "wˈʌn ˈoʊpən ðə ɹᵻpˈoːɹt"),
            ("Ordered list item two", ["tˈuː", "tʃˈɛk ðə ɹɪzˈʌlts"], "tˈuː tʃˈɛk ðə ɹɪzˈʌlts"),
            ("Ordered list item three", ["θɹˈiː", "sˈɛnd ðə fˈaɪnəl"], "θɹˈiː sˈɛnd ðə fˈaɪnəl"),
            ("Doctor and surname", ["dˈɑːktɚ", "mˈɔːɹɡən"], "dˈɑːktɚ mˈɔːɹɡən"),
            ("Professor and surname", ["pɹˈɑːf", "lˈiː"], "pɹˈɑːf lˈiː"),
            ("Multiple punctuation boundaries", ["nˈoʊ", "twˈɛlv ænd fˈɪɡ", "θɹˈiː"], "nˈoʊ twˈɛlv ænd fˈɪɡ θɹˈiː"),
            ("Native leading separator", ["nˈoʊ", " twˈɛlv"], "nˈoʊ twˈɛlv"),
            ("Native trailing separator", ["nˈoʊ ", "twˈɛlv"], "nˈoʊ twˈɛlv"),
            ("Native separators on all parts", [" nˈoʊ", " twˈɛlv"], " nˈoʊ twˈɛlv"),
            ("Empty fragment", ["nˈoʊ", "", "twˈɛlv", ""], "nˈoʊ twˈɛlv"),
            ("Single native fragment", ["nˈoʊ twˈɛlv"], "nˈoʊ twˈɛlv"),
            ("Comma attaches to preceding word", ["həlˈoʊ", ",", "wˈɜːld"], "həlˈoʊ, wˈɜːld"),
            ("Period attaches to preceding word", ["həlˈoʊ", ".", "wˈɜːld"], "həlˈoʊ. wˈɜːld"),
            ("No space between phoneme and stress", ["wˈʌn", "ˈoʊpən"], "wˈʌn ˈoʊpən"),
            ("Retain internal phoneme symbols", ["ɹˈiːd ɹˈɛd"], "ɹˈiːd ɹˈɛd"),
            ("Do not insert before punctuation", ["həlˈoʊ", ","], "həlˈoʊ,"),
        };

        foreach (var test in partsCases)
        {
            yield return ($"Native fragment joining: {test.Name}", () =>
            {
                var output = new StringBuilder();
                foreach (string part in test.Parts) EspeakPhonemePartJoiner.Append(output, part);
                Require(output.ToString() == test.Expected, $"Phoneme word boundaries: {output}");
            });
        }

        yield return ("Technical speech: padding and sentence tokens remain structural", () =>
        {
            var bridge = new EspeakWrapper();
            bridge.CharacterResults['_'] = "a";
            bridge.CharacterResults['$'] = "b";
            bridge.CharacterResults['^'] = "c";
            var config = Config();
            var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config);
            string ipa = phonemizer.GetPhonemes("_$^");
            Require(ipa == "a b c", $"Character boundaries: {ipa}");
            long[] ids = new PiperPhonemizer(config, NullLogger<PiperPhonemizer>.Instance).PhonemesToIds(ipa);
            Require(ids.Count(id => id == 1) == 1 && ids.Count(id => id == 2) == 1,
                "Literal controls introduced extra sentence IDs.");
            Require(ids.Contains(4) && ids.Contains(5) && ids.Contains(6), "Named symbols disappeared from IDs.");
        });

        yield return ("Technical speech: unsupported character-name IPA is adapted before IDs", () =>
        {
            var config = Config();
            config.PhonemeIdMap["t"] = [20];
            config.PhonemeIdMap["ʃ"] = [21];
            var fallback = new PhonemeFallbackMapper(Path.Combine("PHOIBLE", "phoible.csv"), config);
            var bridge = new EspeakWrapper();
            bridge.CharacterResults['_'] = "ʧ";
            var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config,
                fallbackMapper: fallback);
            string ipa = phonemizer.GetPhonemes("_");
            Require(ipa == "tʃ", $"Character name was not adapted: {ipa}");
            long[] ids = new PiperPhonemizer(config, NullLogger<PiperPhonemizer>.Instance).PhonemesToIds(ipa);
            Require(ids.Contains(20) && ids.Contains(21), "Adapted character phonemes did not reach the tensor IDs.");
        });

        foreach (bool useDetector in new[] { true, false })
        {
            yield return ($"Technical speech ({useDetector}): silent quote preserves the following word boundary", () =>
            {
                var bridge = new EspeakWrapper();
                var config = Config();
                var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config,
                    useDetector ? detector : null);
                string result = phonemizer.GetPhonemes("config.prod.json\"tail", "en");
                Require(result == "config s prod s json tail", $"Technical name merged with the next word: {result}");
                Require(bridge.CharacterCalls.Count == 1 && bridge.CharacterCalls[0].Character == '.',
                    "A visual quote was pronounced as technical syntax.");
            });
        }

        yield return ("Technical speech: raw IPA bypasses symbol pronunciation", () =>
        {
            var bridge = new EspeakWrapper();
            var config = Config();
            var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, detector);
            Require(phonemizer.GetPhonemes("[[a_b]]", "en") == "a_b", "Raw IPA was rewritten.");
            Require(bridge.Calls.Count == 0 && bridge.CharacterCalls.Count == 0, "Raw IPA reached eSpeak.");
        });

        foreach (bool useDetector in new[] { true, false })
        {
            yield return ($"Technical speech ({useDetector}): symbol names keep the model voice under a forced language", () =>
            {
                var bridge = new EspeakWrapper();
                var config = Config();
                new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config,
                    useDetector ? detector : null).GetPhonemes("дані_значення", "uk");
                Require(bridge.CharacterCalls.Single().Voice == "en-us", "Forced language replaced the symbol-name voice.");
                Require(bridge.Calls.Count == 2 && bridge.Calls.All(c => c.Voice == "uk"),
                    "The symbol-name route replaced the text language.");
            });
        }

        yield return ("Technical speech: symbol cache belongs to each model voice", () =>
        {
            var first = new EspeakWrapper();
            var second = new EspeakWrapper();
            var english = Config();
            var french = Config();
            french.Espeak.Voice = "fr-fr";
            new UnifiedPhonemizer(first, new DynamicPunctuationMapper(english), english).GetPhonemes("_");
            new UnifiedPhonemizer(second, new DynamicPunctuationMapper(french), french).GetPhonemes("_");
            Require(first.CharacterCalls.Single().Voice == "en-us" &&
                second.CharacterCalls.Single().Voice == "fr-fr", "Character names leaked between model voices.");
        });

        yield return ("Technical speech: optional marker additions preserve immutable defaults", () =>
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "{\"AdditionalTechnicalTokenMarkers\":[\"§\"]}");
                var custom = TextChunkerRules.LoadOrDefault(path);
                Require(TechnicalTextRecognizer.TryGetSpanLength("a§b".AsSpan(), 0, custom, out int length) && length == 3,
                    "Custom technical marker did not reach the recognizer.");
                Require(!TechnicalTextRecognizer.TryGetSpanLength("a§b".AsSpan(), 0, rules, out _),
                    "Custom marker mutated the defaults.");
                var config = Config();
                var bridge = new EspeakWrapper();
                new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config, custom), config)
                    .GetPhonemes("a§b");
                Require(bridge.CharacterCalls.Single().Character == '§', "Custom marker was not pronounced.");
            }
            finally { File.Delete(path); }
        });
    }

    private static PiperConfig Config() => new()
    {
        Espeak = new EspeakConfig { Voice = "en-us" },
        PhonemeIdMap = new Dictionary<string, int[]>
        {
            ["_"] = [0], ["^"] = [1], ["$"] = [2], [" "] = [3],
            ["a"] = [4], ["b"] = [5], ["c"] = [6], ["s"] = [7],
            ["."] = [10], ["?"] = [13], ["!"] = [14], [","] = [15],
            ["-"] = [16], [":"] = [17], [";"] = [18]
        }
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
