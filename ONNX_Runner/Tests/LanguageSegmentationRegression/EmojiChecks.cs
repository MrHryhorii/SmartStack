using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class EmojiChecks
{
    private const string Face = "\U0001F600";
    private const string Heart = "\u2764\uFE0F";
    private const string Technologist = "\U0001F469\u200D\U0001F4BB";

    /// <summary>Provides emoji recognition, language neutrality, and enabled/disabled pronunciation regression cases.</summary>
    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        yield return ("Emoji settings: omitted and explicit JSON values", () =>
        {
            foreach (var fixture in new[] { ("{}", true), ("{\"UseLanguageDetector\":false}", true),
                ("{\"ReadEmojis\":false}", false), ("{\"ReadEmojis\":true}", true) })
            {
                using var json = new MemoryStream(Encoding.UTF8.GetBytes(fixture.Item1));
                var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
                Require((configuration.Get<PhonemizerSettings>() ?? new()).ReadEmojis == fixture.Item2,
                    "Configuration did not preserve enabled-by-default reading or an explicit override.");
            }
        });

        string[] emoji = [Face, Heart, "\u2764", Technologist, "\U0001F469\U0001F3FD\u200D\U0001F4BB",
            "\U0001F44D\U0001F3FD", "\U0001F1FA\U0001F1E6", "1\uFE0F\u20E3", "#\u20E3",
            "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466", "\u00A9\uFE0F", "\u203C\uFE0F"];
        for (int index = 0; index < emoji.Length; index++)
        {
            string value = emoji[index];
            yield return ($"Emoji classification: complete text element {index}", () =>
                Require(EmojiTextRecognizer.TryGetSpanLength((value + "tail").AsSpan(), TextChunkerRules.Default, out int length)
                    && length == value.Length, "Emoji components or following text were separated incorrectly."));
        }

        string[] ordinary = [".", "?", "#tag", "*args", "1", "1\uFE0F", "\u00A9", "\u00AE", "\u2122",
            "\u00B1", "\u2264", "\u221E", "\u266F", "\u2192", "\u203C", "\u2049", "\u2764\uFE0E",
            "a\u0301", "\U00010400", "\U0001F02C", "\uD800", "\u200D", "\uFE0F"];
        for (int index = 0; index < ordinary.Length; index++)
        {
            string value = ordinary[index];
            yield return ($"Emoji classification: ordinary text or invalid input {index}", () =>
                Require(!EmojiTextRecognizer.TryGetSpanLength(value.AsSpan(), TextChunkerRules.Default, out _),
                    "Text punctuation, technical symbols, or invalid UTF-16 were classified as emoji."));
        }

        foreach (var fixture in new[]
        {
            ("is" + Face + Heart + "our", false), ("is1\uFE0F\u20E3our", false),
            ("user_" + Heart + "name", true), ("config" + Heart + ".prod.json", true)
        })
            yield return ($"Technical evidence excludes emoji: {fixture.Item1}", () =>
            {
                bool actual = TechnicalTextRecognizer.TryGetSpanLength(fixture.Item1.AsSpan(), 0, TextChunkerRules.Default, out int length);
                Require(actual == fixture.Item2 && (!actual || length == fixture.Item1.Length),
                    "Emoji changed syntax classification or the protected source length.");
            });

        foreach (bool detectorEnabled in new[] { false, true })
        {
            var detector = detectorEnabled ? new MixedLanguagePhonemizer(new PhonemizerSettings(), "en-us",
                NullLogger<MixedLanguagePhonemizer>.Instance, new TextChunker(new ChunkerSettings())) : null;
            foreach (bool read in new[] { false, true })
            {
                var fixtures = new (string Input, string Disabled, string Enabled, int EmojiCalls)[]
                {
                    (Face + "?", "?", "e?", 1),
                    ("Hello " + Face + " world.", "Hello world.", "Hello e world.", 1),
                    ("Hello" + Face + "world.", "Hello world.", "Hello e world.", 1),
                    ("Hello" + Face + Heart + "world.", "Hello world.", "Hello e e world.", 2),
                    ("user_" + Face + "name", "user s name", "user s e name", 1),
                    ("user_" + Heart + "name", "user s name", "user s e name", 1),
                    (Technologist + "?", "?", "e?", 1),
                    ("1\uFE0F\u20E3?", "?", "e?", 1),
                    (Face + Heart, "", "e e", 2),
                    ("\u203C\uFE0F?", "?", "e?", 1)
                };
                for (int index = 0; index < fixtures.Length; index++)
                {
                    var fixture = fixtures[index];
                    yield return ($"Emoji pipeline ({detectorEnabled}, {read}): source fixture {index}", () =>
                    {
                        var bridge = new EspeakWrapper();
                        string actual = Create(bridge, detector, read).GetPhonemes(fixture.Input, "en");
                        Require(NormalizeSpaces(actual) == (read ? fixture.Enabled : fixture.Disabled),
                            $"Unexpected spelling or lost words: {actual}");
                        Require(bridge.EmojiCalls.Count == (read ? fixture.EmojiCalls : 0),
                            "Emoji bypassed the setting or did not use its isolated native route.");
                        Require(bridge.CharacterCalls.All(c => c.Character == '_'), "Emoji reached the literal character API.");
                    });
                }
                yield return ($"Emoji pipeline ({detectorEnabled}, {read}): punctuation and initials retain their roles", () =>
                {
                    var bridge = new EspeakWrapper();
                    var pipeline = Create(bridge, detector, read);
                    foreach (string input in new[] { "Dr. Smith arrived.", "J. R. R. Tolkien arrived.", "Is it ready?", "Use a+b." })
                        Require(pipeline.GetPhonemes(input, "en") == Create(new EspeakWrapper(), detector, false).GetPhonemes(input, "en"),
                            "Emoji configuration changed ordinary text or technical operators.");
                    Require(bridge.EmojiCalls.Count == 0, "Ordinary punctuation invoked emoji spelling.");
                });
                yield return ($"Emoji pipeline ({detectorEnabled}, {read}): explicit raw IPA is unchanged", () =>
                {
                    var bridge = new EspeakWrapper();
                    string raw = "a" + Heart + "b";
                    Require(Create(bridge, detector, read).GetPhonemes("[[" + raw + "]]") == raw && bridge.EmojiCalls.Count == 0,
                        "Orthographic emoji handling rewrote explicit raw phonemes.");
                });
            }
            yield return ($"Emoji pipeline ({detectorEnabled}): omitted settings read technical emoji", () =>
            {
                var bridge = new EspeakWrapper();
                var config = Config("en-us");
                var pipeline = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, detector);
                Require(NormalizeSpaces(pipeline.GetPhonemes("user_" + Face + "name", "en")) == "user s e name"
                    && bridge.EmojiCalls.Count == 1 && bridge.EmojiCalls[0].Voice == "en-us",
                    "Omitted settings did not read a technical-token emoji using the model voice.");
            });
            yield return ($"Emoji pipeline ({detectorEnabled}): legacy JSON enables ordinary emoji", () =>
            {
                using var json = new MemoryStream(Encoding.UTF8.GetBytes("{\"UseLanguageDetector\":false}"));
                var settings = new ConfigurationBuilder().AddJsonStream(json).Build().Get<PhonemizerSettings>();
                var bridge = new EspeakWrapper();
                var config = Config("en-us");
                var pipeline = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, detector,
                    settings: settings);
                Require(NormalizeSpaces(pipeline.GetPhonemes("Hello " + Face + " world.", "en")) == "Hello e world."
                    && bridge.EmojiCalls.Count == 1, "Legacy JSON did not inherit enabled emoji reading.");
            });
            yield return ($"Emoji pipeline ({detectorEnabled}): disabled spelling retains ordinary phrase context", () =>
            {
                var bridge = new EspeakWrapper();
                Create(bridge, detector, false).GetPhonemes("This is" + Face + Heart + "our final result.", "en-us");
                Require(bridge.Calls.Count == 1 && bridge.Calls[0].Text == "This is our final result",
                    "Emoji caused an ordinary phrase to be fragmented: " + string.Join(" / ", bridge.Calls.Select(c => c.Text)));
            });
            yield return ($"Emoji pipeline ({detectorEnabled}): unsupported names stay silent", () =>
            {
                var bridge = new EspeakWrapper();
                bridge.EmojiResults[Face] = "";
                var pipeline = Create(bridge, detector, true);
                Require(NormalizeSpaces(pipeline.GetPhonemes("Hello" + Face + "world.")) == "Hello world.",
                    "An unsupported emoji erased adjacent words or introduced a Unicode-code name.");
                Require(pipeline.GetPhonemes(Face + "?") == "?" && bridge.CharacterCalls.Count == 0,
                    "Unsupported names changed the remaining question mark.");
            });
            foreach (string modelVoice in new[] { "en-us", "en-gb", "pt-br" })
                yield return ($"Emoji pipeline ({detectorEnabled}): exact model dialect {modelVoice}", () =>
                {
                    var bridge = new EspeakWrapper();
                    Create(bridge, detector, true, modelVoice).GetPhonemes("Hello " + Face + " world " + Face, "uk");
                    Require(bridge.EmojiCalls.Count == 1 && bridge.EmojiCalls[0].Voice == modelVoice,
                        "Forced text language replaced the model dialect or cached names were requested again.");
                    Require(bridge.Calls.All(c => c.Voice == "uk"), "Emoji model voice leaked into ordinary text pronunciation.");
                });
            if (detector != null)
                yield return ("Emoji source: whole sequences remain neutral in language analysis", () =>
                {
                    foreach (string value in emoji)
                    {
                        string input = "The report " + value + " contains the expected values.";
                        var tokens = detector.ProcessTextToLanguageTokens(input);
                        Require(string.Concat(tokens.Select(t => t.Text)) == input, "Language segmentation lost emoji source text.");
                        Require(tokens.Any(t => t.Text.Contains(value, StringComparison.Ordinal)), "A composed emoji was split between language tokens.");
                        Require(tokens.Where(t => t.Text.Any(char.IsLetter)).All(t => t.DetectedLanguage == "en-us"),
                            "Neutral emoji changed model/script language assignment.");
                    }
                });
        }
    }

    /// <summary>Creates a phonemizer with a recording eSpeak bridge, chosen model voice, and explicit emoji setting.</summary>
    private static UnifiedPhonemizer Create(EspeakWrapper bridge, MixedLanguagePhonemizer? detector,
        bool read, string voice = "en-us")
    {
        var config = Config(voice);
        return new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, detector,
            settings: new PhonemizerSettings { ReadEmojis = read });
    }
    /// <summary>Creates the small model phoneme inventory needed by emoji routing checks.</summary>
    private static PiperConfig Config(string voice) => new()
    {
        Espeak = new() { Voice = voice },
        PhonemeIdMap = new() { [" "] = [1], ["."] = [2], ["?"] = [3], ["!"] = [4], ["e"] = [5], ["s"] = [6] }
    };
    /// <summary>Normalizes repeated spaces before comparing recorded text fragments.</summary>
    private static string NormalizeSpaces(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    /// <summary>Throws with the supplied diagnostic when a regression assertion fails.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
