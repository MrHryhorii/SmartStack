using ONNX_Runner.Services.Text.Phonemization;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Services;

internal static class EmojiPronunciationChecks
{
    // Spoken references are declared independently of the native emoji result.
    private static readonly (string Emoji, string Name)[] English =
    [
        ("\U0001F600", "grinning face"),
        ("\U0001F642", "slightly smiling face"),
        ("\U0001F602", "face with tears of joy"),
        ("\U0001F914", "thinking face"),
        ("\u2764", "red heart"),
        ("\u2764\uFE0F", "red heart"),
        ("\U0001F44D", "thumbs up"),
        ("\U0001F44D\U0001F3FD", "thumbs up medium skin tone"),
        ("\U0001F469\u200D\U0001F4BB", "woman technologist"),
        ("\U0001F469\U0001F3FD\u200D\U0001F4BB", "woman technologist medium skin tone"),
        ("\U0001F1FA\U0001F1E6", "Ukraine"),
        ("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466", "family man woman girl boy"),
        ("1\uFE0F\u20E3", "one")
    ];

    /// <summary>Checks real eSpeak emoji names, unsupported sequences, model dialects, adaptation, and cached concurrent reading.</summary>
    internal static void Run(EspeakWrapper bridge, PiperConfig config, MixedLanguagePhonemizer detector,
        PhonemeFallbackMapper adapter, Action<string, Action> check)
    {
        // These declared spoken references require an English model voice, regardless of the supplied inventory.
        var englishConfig = new PiperConfig { Espeak = new() { Voice = "en-us" }, PhonemeIdMap = config.PhonemeIdMap };
        // The bundled eSpeak data identifies its default British dialect as "en".
        foreach (string voice in new[] { "en-us", "en", "en-gb-x-rp" })
            for (int index = 0; index < English.Length; index++)
            {
                var (Emoji, Name) = English[index];
                check($"Complete native emoji description ({voice}): fixture {index}", () =>
                {
                    Require(bridge.TryGetIpaPhonemes(Name, voice, out string expected), "Reference voice is unavailable.");
                    Require(bridge.TryGetEmojiPhonemes(Emoji, voice, out string actual) && Spoken(expected) == Spoken(actual),
                        $"Expected complete name: {expected}; actual: {actual}");
                });
            }

        foreach (bool useDetector in new[] { false, true })
        {
            foreach (bool supplySettings in new[] { false, true })
                check($"Default emoji reading ({useDetector}, settings supplied: {supplySettings})", () =>
                {
                    var pipeline = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(englishConfig), englishConfig,
                        useDetector ? detector : null, adapter, supplySettings ? new PhonemizerSettings() : null);
                    var reference = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(englishConfig), englishConfig,
                        fallbackMapper: adapter);
                    string actual = pipeline.GetPhonemes("\u2764\uFE0F?", "uk");
                    string expected = reference.GetPhonemes("red heart", englishConfig.Espeak.Voice) + "?";
                    Require(Spoken(actual) == Spoken(expected),
                        $"Default settings did not read the emoji using the model voice: {actual}");
                });

            foreach (bool read in new[] { false, true })
            {
                var pipeline = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(englishConfig), englishConfig,
                    useDetector ? detector : null, adapter, new PhonemizerSettings { ReadEmojis = read });
                var reference = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(englishConfig), englishConfig, fallbackMapper: adapter);
                for (int index = 0; index < English.Length; index++)
                {
                    var (Emoji, Name) = English[index];
                    check($"Emoji inventory and punctuation ({useDetector}, {read}): fixture {index}", () =>
                    {
                        string actual = pipeline.GetPhonemes(Emoji + "?", "en-us");
                        string expected = read ? reference.GetPhonemes(Name, englishConfig.Espeak.Voice) + "?" : "?";
                        Require(Spoken(expected) == Spoken(actual) && actual.EndsWith('?'),
                            $"Expected spelling with a separate question mark: {expected}; actual: {actual}");
                        Require(new PhonemeInventory(config.PhonemeIdMap).CanEncode(actual.AsSpan()),
                            "Emoji phonemes are not representable by the model inventory.");
                        Require(pipeline.GetPhonemes(Emoji + "?", "en-us") == actual, "The cached name changed output.");
                    });
                }

                check($"Emoji inside technical text ({useDetector}, {read})", () =>
                {
                    string input = "user_\U0001F600name";
                    string actual = pipeline.GetPhonemes(input, "en-us");
                    string expected = reference.GetPhonemes("user_", "en-us") + " " +
                        (read ? reference.GetPhonemes("grinning face", englishConfig.Espeak.Voice) + " " : "") +
                        reference.GetPhonemes("name", "en-us");
                    Require(Spoken(actual) == Spoken(expected), $"Technical token bypassed the setting: {actual}");
                });

                check($"Emoji attached to ordinary words ({useDetector}, {read})", () =>
                {
                    string actual = pipeline.GetPhonemes("Hello\U0001F600world.", "en-us");
                    string expected = reference.GetPhonemes("Hello", "en-us") + " " +
                        (read ? reference.GetPhonemes("grinning face", englishConfig.Espeak.Voice) + " " : "") +
                        reference.GetPhonemes("world", "en-us") + ".";
                    Require(Spoken(actual) == Spoken(expected), $"Emoji removal damaged adjacent words: {actual}");
                });

                if (!read)
                    check($"Disabled emoji preserve whole-phrase pronunciation ({useDetector})", () =>
                    {
                        foreach (string input in new[]
                        {
                            "This is\U0001F600our final result.",
                            "This is\U0001F600\u2764\uFE0Four final result."
                        })
                        {
                            string expected = reference.GetPhonemes("This is our final result.", "en-us");
                            string actual = pipeline.GetPhonemes(input, "en-us");
                            Require(actual == expected,
                                $"Removing emoji changed pronunciation across an ordinary word boundary. Expected: {expected}; actual: {actual}");
                        }
                    });
            }
        }

        foreach (var fixture in new[] { ("en-us", "red heart"), ("en-gb-x-rp", "red heart"), ("de", "rotes Herz") })
            check($"Emoji model voice overrides forced text language: {fixture.Item1}", () =>
            {
                var variant = new PiperConfig { Espeak = new() { Voice = fixture.Item1 }, PhonemeIdMap = config.PhonemeIdMap };
                var pipeline = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(variant), variant,
                    fallbackMapper: adapter, settings: new PhonemizerSettings { ReadEmojis = true });
                var reference = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(variant), variant, fallbackMapper: adapter);
                string actual = pipeline.GetPhonemes("\u2764\uFE0F?", "uk");
                string expected = reference.GetPhonemes(fixture.Item2, fixture.Item1) + "?";
                Require(Spoken(actual) == Spoken(expected), $"Emoji dialect followed the text language: {actual}");
            });

        check("Emoji routing follows the supplied model's native dictionary", () =>
        {
            string voice = config.Espeak.Voice;
            Require(bridge.TryGetEmojiPhonemes("\u2764\uFE0F", voice, out string native), "Model voice unavailable.");
            var pipeline = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config,
                fallbackMapper: adapter, settings: new PhonemizerSettings { ReadEmojis = true });
            var reference = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, fallbackMapper: adapter);
            string expected = (native.Length > 0 ? reference.GetPhonemes("[[" + native + "]]", voice) : "") + "?";
            Require(pipeline.GetPhonemes("\u2764\uFE0F?", "uk") == expected,
                "A forced text language replaced the model's dictionary name or silence for an unavailable name.");
        });

        check("Emoji native calls preserve subsequent text and character state", () =>
        {
            Require(bridge.TryGetIpaPhonemes("Ordinary text remains readable.", "en-us", out string expected), "Reference voice unavailable.");
            Parallel.For(0, 8, iteration =>
            {
                foreach (var (Emoji, Name) in English)
                    Require(bridge.TryGetEmojiPhonemes(Emoji, "en", out string name) && name.Length > 0,
                        "Concurrent emoji transcription lost the model voice.");
                bridge.TryGetCharacterPhonemes('_', "en-us", out _);
                Require(bridge.TryGetIpaPhonemes("Ordinary text remains readable.", "en-us", out string actual) && actual == expected,
                    "Emoji calls contaminated the native text state.");
            });
            Require(!bridge.TryGetEmojiPhonemes("\U0001F600", "invalid-emoji-test-voice", out _),
                "An unavailable voice was replaced silently.");
        });

        check("Emoji dictionary misses preserve silence without character-code spelling", () =>
        {
            // These newer emoji are absent from the bundled data used by the engine.
            // If installed data supplies them, compare to that declared name instead of requiring silence.
            foreach (var fixture in new[]
            {
                ("\U0001F979", "face holding back tears"), ("\U0001FAE0", "melting face"),
                ("\U0001F469\u200D\U0001F9B0", "woman red hair"),
                ("\U0001F62E\u200D\U0001F4A8", "face exhaling"),
                ("\U0001F642\u200D\u2194\uFE0F", "head shaking horizontally")
            })
            {
                Require(bridge.TryGetEmojiPhonemes(fixture.Item1, "en-us", out string actual), "Model voice unavailable.");
                if (actual.Length == 0) continue;
                Require(bridge.TryGetIpaPhonemes(fixture.Item2, "en-us", out string expected) && Spoken(actual) == Spoken(expected),
                    "An unknown emoji was spelled as a Unicode code or as an incomplete name.");
            }
        });

        check("Emoji support remains tied to the requested language dictionary", () =>
        {
            Require(bridge.TryGetIpaPhonemes("\U0001F600", "uk", out string native), "Ukrainian voice unavailable.");
            Require(bridge.TryGetEmojiPhonemes("\U0001F600", "uk", out string actual) && Spoken(actual) == Spoken(native),
                "An unavailable localized name was replaced with another language.");
        });
    }

    /// <summary>Removes whitespace and stress marks when comparing spoken phoneme content.</summary>
    private static string Spoken(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c) && c is not ('\u02C8' or '\u02CC')));
    /// <summary>Throws with the supplied diagnostic when a native integration assertion fails.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
