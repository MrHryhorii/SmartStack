using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class ScriptOnlyLanguageChecks
{
    private const string EnglishSentence = "The English report contains all the requested information.";

    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        var configurations = new (string Name, List<string>? Languages)[]
        {
            ("empty list", []),
            ("null list", null),
            ("model language only", ["en"]),
            ("duplicate model dialects", ["en", "en-gb", "EN_US", "en-gb-x-rp"]),
            ("unknown additional codes", ["unknown-language", "not-a-language"]),
            ("blank entries", ["", " ", "\t", "en"])
        };

        foreach (var fixture in configurations)
        {
            yield return ($"Script-only routing: {fixture.Name} starts and processes a long sentence", () =>
            {
                var settings = new PhonemizerSettings { SupportedLanguages = fixture.Languages! };
                var tokens = Read(Create(settings), EnglishSentence);
                Require(tokens.All(token => token.DetectedLanguage == "en-us" &&
                    token.Probability == 0 && !token.IsReliable),
                    "Model fallback changed the dialect or invented statistical confidence.");
            });
        }

        yield return ("Script-only routing: null settings use the shared defaults", () =>
        {
            var detector = new MixedLanguagePhonemizer(null!, "en-us", NullLogger<MixedLanguagePhonemizer>.Instance);
            Require(Read(detector, EnglishSentence).All(token => token.DetectedLanguage == "en-us"),
                "Null settings did not retain model/script routing.");
        });

        yield return ("Script-only routing: missing JSON candidates keep automatic script routing", () =>
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<PhonemizerSettings>("{}")!;
            Require(Read(Create(settings), "Слова").Single().DetectedLanguage == "uk",
                "An older configuration lost foreign-script fallback.");
        });

        yield return ("Script-only routing: language aliases still form one candidate", () =>
        {
            var settings = new PhonemizerSettings { SupportedLanguages = ["no", "nb-no", "NO"] };
            Require(Read(Create(settings, "nb"), "Dette er en vanlig norsk setning.")
                .All(token => token.DetectedLanguage == "nb"), "Aliases created a spurious statistical choice.");
        });

        var routes = new (string Name, string Model, string Input, string Language, bool Reliable)[]
        {
            ("model dialect", "en-gb-x-rp", EnglishSentence, "en-gb-x-rp", false),
            ("French model keeps Latin", "fr-ca", "La variable contient une valeur valide.", "fr-ca", false),
            ("Cyrillic model keeps its language", "ru", "Обычный текст.", "ru", false),
            ("Latin on a Cyrillic model", "uk", EnglishSentence, "en", false),
            ("Cyrillic default", "en-us", "Слова", "uk", false),
            ("Ukrainian letter hint", "en-us", "Їжак", "uk", false),
            ("Russian letter hint", "en-us", "Эхо", "ru", false),
            ("Greek", "en-us", "Καλημέρα", "el", false),
            ("Hebrew", "en-us", "שלום", "he", false),
            ("Georgian", "en-us", "გამარჯობა", "ka", false),
            ("Devanagari", "en-us", "नमस्ते", "hi", false),
            ("Thai", "en-us", "สวัสดี", "th", false),
            ("Hangul", "en-us", "안녕하세요", "ko", false),
            ("Hiragana capability", "en-us", "かな", "ja", true),
            ("Katakana capability", "en-us", "カナ", "ja", true),
            ("Han capability", "en-us", "漢字", "cmn", true),
            ("Serbian Latin script", "sr", "Dobar dan", "sr", false),
            ("Serbian Cyrillic script", "sr", "Добар дан", "sr", false),
            ("romaji remains Latin", "en-us", "watashi wa gakusei desu", "en-us", false),
            ("unlisted same-script prose uses the model", "en-us", "bonjour", "en-us", false),
            ("zero mapped candidates use script fallback", "as", "Слова", "uk", false),
            ("zero mapped candidates keep technical routing", "as", "user_name", "en", false)
        };

        foreach (var fixture in routes)
        {
            yield return ($"Script-only routing: {fixture.Name}", () =>
            {
                var tokens = Read(Create(new PhonemizerSettings(), fixture.Model), fixture.Input);
                Require(tokens.All(token => token.DetectedLanguage == fixture.Language &&
                    token.IsReliable == fixture.Reliable), "The model/script route differs from its declared fixture.");
                if (!fixture.Reliable)
                {
                    Require(tokens.All(token => token.Probability == 0), "Fallback invented a Lingua confidence.");
                }
            });
        }

        yield return ("Script-only routing: statistical thresholds do not affect fallback", () =>
        {
            var settings = new PhonemizerSettings
            {
                LocalWinnerProbabilityFloor = 1.01,
                LocalWinnerMarginFloor = 1.01,
                ReliabilityProbabilityThreshold = 1.01,
                MixedLanguageOverrideThreshold = 0.0,
                MinSentenceLengthForOverride = 0
            };
            Require(Read(Create(settings), EnglishSentence).All(token => token.DetectedLanguage == "en-us" &&
                token.Probability == 0 && !token.IsReliable), "Statistical tuning changed the no-detector route.");
        });

        yield return ("Script-only routing: mixed scripts keep source and language boundaries", () =>
        {
            const string input = "English user_name, Їжак дані_значення, かな, 漢字.";
            var tokens = Read(Create(new PhonemizerSettings()), input);
            foreach (var anchor in new[]
            {
                (Text: "English", Language: "en-us"), (Text: "user_name", Language: "en-us"),
                (Text: "Їжак", Language: "uk"), (Text: "дані_значення", Language: "uk"),
                (Text: "かな", Language: "ja"), (Text: "漢字", Language: "cmn")
            })
            {
                Require(tokens.Any(token => token.Text.Contains(anchor.Text, StringComparison.Ordinal) &&
                    token.DetectedLanguage == anchor.Language), "A script boundary lost its declared language anchor.");
            }
        });

        yield return ("Script-only routing: technical spans keep the surrounding model dialect", () =>
        {
            const string input = "Dr. Morgan and J. R. R. Tolkien checked user_name before opening build_output.";
            var tokens = Read(Create(new PhonemizerSettings(), "en-gb-x-rp"), input);
            Require(tokens.All(token => token.DetectedLanguage == "en-gb-x-rp"), "A technical span changed the model dialect.");
            Require(tokens.Where(token => token.IsTechnical).Select(token => token.Text)
                .SequenceEqual(["user_name", "build_output"]), "Initials or prose were misclassified as technical syntax.");
        });

        yield return ("Script-only routing: forced language overrides all script routes", () =>
        {
            var tokens = Read(Create(new PhonemizerSettings()), "English user_name, Їжак, かな, 漢字.", "FR-CA");
            Require(tokens.All(token => token.DetectedLanguage == "fr-ca" && token.IsReliable && token.Probability == 1.0),
                "A script route overrode the explicitly requested language.");
        });

        yield return ("Script-only routing: forced base language inherits the model dialect", () =>
        {
            var tokens = Read(Create(new PhonemizerSettings(), "en-gb-x-rp"), EnglishSentence, "en");
            Require(tokens.All(token => token.DetectedLanguage == "en-gb-x-rp" && token.IsReliable),
                "Removing Lingua changed forced-language dialect inheritance.");
        });

        yield return ("Script-only routing: explicit Japanese overrides Latin romaji routing", () =>
        {
            var tokens = Read(Create(new PhonemizerSettings()), "watashi wa gakusei desu", "ja");
            Require(tokens.All(token => token.DetectedLanguage == "ja" && token.IsReliable),
                "An explicit Japanese request was replaced by Latin fallback.");
        });

        yield return ("Script-only routing: unified phonemizer uses the selected script languages", () =>
        {
            var bridge = new EspeakWrapper();
            var config = new PiperConfig
            {
                Espeak = new EspeakConfig { Voice = "en-us" },
                PhonemeIdMap = new() { ["."] = [1] }
            };
            var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config,
                Create(new PhonemizerSettings()));
            phonemizer.GetPhonemes("English user_name, Їжак, かな.");
            Require(bridge.Calls.Any(call => call.Voice == "en-us") &&
                bridge.Calls.Any(call => call.Voice == "uk") &&
                bridge.Calls.Any(call => call.Voice == "ja"), "Script decisions did not reach eSpeak calls.");
            Require(bridge.CharacterCalls.Any(call => call.Character == '_' && call.Voice == "en-us"),
                "Underscore bypassed literal character pronunciation in the selected language.");
        });

        yield return ("Script-only routing: candidate changes take effect after recreation", () =>
        {
            var settings = new PhonemizerSettings();
            var detector = Create(settings);
            settings.SupportedLanguages = ["fr"];
            Require(Read(detector, "bonjour").Single().DetectedLanguage == "en-us",
                "Mutating settings activated Lingua in an existing component.");
            Require(Read(Create(settings), "bonjour").Single().DetectedLanguage == "fr",
                "A recreated component did not load the second statistical candidate.");
        });
    }

    private static MixedLanguagePhonemizer Create(PhonemizerSettings settings, string model = "en-us") =>
        new(settings, model, NullLogger<MixedLanguagePhonemizer>.Instance);

    private static TextChunk[] Read(MixedLanguagePhonemizer detector, string input, string? language = null)
    {
        var tokens = detector.ProcessTextToLanguageTokens(input, language);
        Require(string.Concat(tokens.Select(token => token.Text)) == input, "Source text changed.");
        var speech = tokens.Where(token => !token.IsPunctuationOrSpace).ToArray();
        Require(speech.Length > 0, "The input produced no speech tokens.");
        return speech;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
