using System.Text;
using Lingua;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class LanguageDetectionSettingsChecks
{
    private const string ForeignWord = "bonjour";
    private const string MixedSentence =
        "The English report contains the requested information and all the expected values remain available, bonjour.";

    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        yield return ("Detector settings: omitted JSON thresholds retain previous defaults", () =>
        {
            var settings = ReadSettings("{}");
            Require(settings.LocalWinnerProbabilityFloor == 0.50 && settings.LocalWinnerMarginFloor == 0.08 &&
                settings.ReliabilityProbabilityThreshold == 0.50, "Missing settings changed the previous thresholds.");
            Require(settings.MaxBonusMultiplier == 0.60 && settings.MixedLanguageOverrideThreshold == 0.85,
                "Adding local thresholds changed other defaults.");
        });

        yield return ("Detector settings: JSON binds all three thresholds", () =>
        {
            var settings = ReadSettings("""
                { "LocalWinnerProbabilityFloor": 0.71, "LocalWinnerMarginFloor": 0.19,
                  "ReliabilityProbabilityThreshold": 0.83 }
                """);
            Require(settings.LocalWinnerProbabilityFloor == 0.71 && settings.LocalWinnerMarginFloor == 0.19 &&
                settings.ReliabilityProbabilityThreshold == 0.83, "The configuration binder ignored a threshold.");
        });

        yield return ("Detector settings: partial JSON retains omitted thresholds", () =>
        {
            var settings = ReadSettings("""{ "LocalWinnerMarginFloor": 0.19 }""");
            Require(settings.LocalWinnerMarginFloor == 0.19 && settings.LocalWinnerProbabilityFloor == 0.50 &&
                settings.ReliabilityProbabilityThreshold == 0.50, "Partial configuration changed omitted defaults.");
        });

        var defaults = Create(ReadSettings("{}"));
        var explicitDefaults = Create(ReadSettings("""
            { "LocalWinnerProbabilityFloor": 0.50, "LocalWinnerMarginFloor": 0.08,
              "ReliabilityProbabilityThreshold": 0.50 }
            """));
        foreach (string input in new[]
        {
            "Dr. Morgan and J. R. R. Tolkien checked user_name before opening config.prod.json.",
            MixedSentence,
            "bonjour, user_name",
            "The variable user_name contains 123 values in build_output."
        })
        {
            yield return ($"Detector settings: explicit defaults preserve token decisions for {input}", () =>
            {
                var omitted = defaults.ProcessTextToLanguageTokens(input);
                var supplied = explicitDefaults.ProcessTextToLanguageTokens(input);
                Require(omitted.Select(Decision).SequenceEqual(supplied.Select(Decision)),
                    "Explicit defaults changed language, reliability, or structural token decisions.");
                Require(string.Concat(supplied.Select(token => token.Text)) == input, "Source text changed.");
            });
        }

        yield return ("Detector settings: configured probability floor changes local versus sentence priority", () =>
        {
            var ordinary = Create(ReadSettings("""{ "LocalWinnerProbabilityFloor": 0.50 }"""));
            var strict = Create(ReadSettings("""{ "LocalWinnerProbabilityFloor": 1.01 }"""));
            Require(FindWord(ordinary, MixedSentence).DetectedLanguage == "fr",
                "The declared French word did not retain authoritative local pronunciation.");
            Require(FindWord(strict, MixedSentence).DetectedLanguage == "en-us",
                "Raising the probability floor did not allow the English sentence context to participate.");
        });

        yield return ("Detector settings: configured margin floor changes local versus sentence priority", () =>
        {
            var ordinary = Create(ReadSettings("""{ "LocalWinnerMarginFloor": 0.08 }"""));
            var strict = Create(ReadSettings("""{ "LocalWinnerMarginFloor": 1.01 }"""));
            Require(FindWord(ordinary, MixedSentence).DetectedLanguage == "fr",
                "The declared French word did not retain authoritative local pronunciation.");
            Require(FindWord(strict, MixedSentence).DetectedLanguage == "en-us",
                "Raising the margin floor did not allow the English sentence context to participate.");
        });

        // Independent Lingua scores supply exact boundary values; expected language comes from
        // the explicitly French fixture. No production decision determines the expected branch.
        var scores = LanguageDetectorBuilder.FromLanguages(Language.English, Language.French)
            .WithPreloadedLanguageModels().Build().ComputeLanguageConfidenceValues(ForeignWord);
        double confidence = scores.Single(pair => pair.Key == Language.French).Value;
        double margin = confidence - scores.Single(pair => pair.Key == Language.English).Value;

        yield return ("Detector settings: boundary fixture has independent French evidence", () =>
        {
            Require(confidence > 0.50 && confidence < 1.0 && margin > 0.08,
                "The pinned Lingua fixture no longer supplies the declared French confidence boundaries.");
        });

        foreach (var fixture in new[]
        {
            (Name: "below", Value: Math.BitDecrement(confidence), Reliable: true),
            (Name: "equal", Value: confidence, Reliable: true),
            (Name: "above", Value: Math.BitIncrement(confidence), Reliable: false)
        })
        {
            yield return ($"Detector settings: probability floor is inclusive ({fixture.Name})", () =>
            {
                var settings = BoundarySettings();
                settings.LocalWinnerProbabilityFloor = fixture.Value;
                var word = FindWord(Create(settings), ForeignWord);
                Require(word.DetectedLanguage == "fr" && word.Probability == confidence &&
                    word.IsReliable == fixture.Reliable, "The local probability boundary used the wrong comparison.");
            });
        }

        foreach (var fixture in new[]
        {
            (Name: "below", Value: Math.BitDecrement(margin), Reliable: true),
            (Name: "equal", Value: margin, Reliable: true),
            (Name: "above", Value: Math.BitIncrement(margin), Reliable: false)
        })
        {
            yield return ($"Detector settings: margin floor is inclusive ({fixture.Name})", () =>
            {
                var settings = BoundarySettings();
                settings.LocalWinnerProbabilityFloor = 0.0;
                settings.LocalWinnerMarginFloor = fixture.Value;
                var word = FindWord(Create(settings), ForeignWord);
                Require(word.DetectedLanguage == "fr" && word.IsReliable == fixture.Reliable,
                    "The local margin boundary used the wrong comparison.");
            });
        }

        foreach (var fixture in new[]
        {
            (Name: "below", Value: Math.BitDecrement(confidence), Reliable: true),
            (Name: "equal", Value: confidence, Reliable: false),
            (Name: "above", Value: Math.BitIncrement(confidence), Reliable: false)
        })
        {
            yield return ($"Detector settings: reliability threshold is strict ({fixture.Name})", () =>
            {
                var settings = BoundarySettings();
                settings.ReliabilityProbabilityThreshold = fixture.Value;
                var word = FindWord(Create(settings), ForeignWord);
                Require(word.DetectedLanguage == "fr" && word.Probability == confidence &&
                    word.IsReliable == fixture.Reliable,
                    "The reliability boundary changed the selected language or used an inclusive comparison.");
            });
        }

        foreach (var fixture in new[] { (Threshold: 0.50, Language: "fr"), (Threshold: 1.01, Language: "en-us") })
        {
            yield return ($"Detector settings: reliability controls technical-only inheritance ({fixture.Threshold})", () =>
            {
                const string input = "bonjour, user_name";
                var settings = BoundarySettings();
                settings.ReliabilityProbabilityThreshold = fixture.Threshold;
                var tokens = Create(settings).ProcessTextToLanguageTokens(input);
                Require(tokens.Single(token => token.Text.Trim() == ForeignWord).DetectedLanguage == "fr",
                    "The reliability setting changed the ordinary phrase's language.");
                Require(tokens.Single(token => token.IsTechnical).DetectedLanguage == fixture.Language,
                    "The technical-only phrase ignored the configured reliability gate.");
                Require(string.Concat(tokens.Select(token => token.Text)) == input, "Source text changed.");
            });
        }

        yield return ("Detector settings: authoritative local winner stays reliable", () =>
        {
            var settings = ReadSettings("""{ "ReliabilityProbabilityThreshold": 1.01 }""");
            var word = FindWord(Create(settings), ForeignWord);
            Require(word.DetectedLanguage == "fr" && word.IsReliable,
                "The ambiguous-result threshold weakened an authoritative local winner.");
        });

        yield return ("Detector settings: forced language bypasses configured statistical thresholds", () =>
        {
            var settings = BoundarySettings();
            var tokens = Create(settings).ProcessTextToLanguageTokens("bonjour, user_name", "es");
            Require(tokens.Where(token => !token.IsPunctuationOrSpace)
                .All(token => token.DetectedLanguage == "es" && token.IsReliable),
                "Statistical tuning overrode a forced language.");
        });

        yield return ("Detector settings: high thresholds preserve script-capability routing", () =>
        {
            var detector = Create(BoundarySettings());
            Require(detector.ProcessTextToLanguageTokens("かな").Any(token =>
                token.DetectedLanguage == "ja" && token.IsReliable), "Kana lost its explicit script route.");
            Require(detector.ProcessTextToLanguageTokens("漢字").Any(token =>
                token.DetectedLanguage == "cmn" && token.IsReliable), "Han lost its explicit script route.");
        });

        yield return ("Detector settings: reliable neighbors do not cross incompatible scripts", () =>
        {
            var settings = BoundarySettings();
            settings.ReliabilityProbabilityThreshold = 0.0;
            var tokens = Create(settings).ProcessTextToLanguageTokens("bonjour, дані_значення");
            Require(tokens.Single(token => token.IsTechnical).DetectedLanguage == "uk",
                "A reliable French neighbor overrode Cyrillic script fallback.");
        });

        yield return ("Detector settings: cached thresholds change only after detector recreation", () =>
        {
            var settings = ReadSettings("""
                { "LocalWinnerProbabilityFloor": 1.01, "LocalWinnerMarginFloor": 1.01,
                  "ReliabilityProbabilityThreshold": 1.01 }
                """);
            var detector = Create(settings);
            Require(FindWord(detector, MixedSentence).DetectedLanguage == "en-us", "The initial thresholds were ignored.");
            settings.LocalWinnerProbabilityFloor = 0.0;
            settings.LocalWinnerMarginFloor = 0.0;
            settings.ReliabilityProbabilityThreshold = 0.0;
            Require(FindWord(detector, MixedSentence).DetectedLanguage == "en-us",
                "Changing the settings object changed an existing detector's cached thresholds.");
            Require(FindWord(Create(settings), MixedSentence).DetectedLanguage == "fr",
                "A newly created detector did not read the changed thresholds.");
        });
    }

    private static PhonemizerSettings ReadSettings(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"PhonemizerSettings\":" + json + "}"));
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(stream).Build();
        return configuration.GetSection("PhonemizerSettings").Get<PhonemizerSettings>() ?? new PhonemizerSettings();
    }

    private static PhonemizerSettings BoundarySettings() => new()
    {
        MaxBonusMultiplier = 0.0,
        LocalWinnerProbabilityFloor = 1.01,
        LocalWinnerMarginFloor = 0.0,
        ReliabilityProbabilityThreshold = 1.01,
        MinSentenceLengthForOverride = int.MaxValue
    };

    private static MixedLanguagePhonemizer Create(PhonemizerSettings settings)
    {
        settings.SupportedLanguages = ["en", "fr"];
        return new MixedLanguagePhonemizer(settings, "en-us", NullLogger<MixedLanguagePhonemizer>.Instance);
    }

    private static TextChunk FindWord(MixedLanguagePhonemizer detector, string input) =>
        detector.ProcessTextToLanguageTokens(input).Single(token => token.Text.Trim() == ForeignWord);

    private static (string, string, double, bool, bool, bool, string) Decision(TextChunk token) =>
        (token.Text, token.DetectedLanguage, token.Probability, token.IsReliable,
            token.IsTechnical, token.IsPunctuationOrSpace, token.Script);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
