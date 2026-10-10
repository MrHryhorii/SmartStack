using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class SymbolPronunciationChecks
{
    // These are explicit spoken descriptions, not names obtained from the symbol under test.
    // Alternatives retain equivalent dictionary spellings across native data versions.
    private static readonly (char Symbol, string[] Names)[] EnglishDescriptions =
    [
        ('≤', ["less or equal to", "less than or equal to"]),
        ('≥', ["greater or equal to", "greater than or equal to"]),
        ('≠', ["not equal to"]),
        ('±', ["plus or minus"]),
        ('×', ["times"]),
        ('÷', ["divided by"]),
        ('√', ["square root"]),
        ('∑', ["sum sign", "summation"]),
        ('∫', ["integral"]),
        ('∞', ["infinity"]),
        ('→', ["right arrow"]),
        ('←', ["left arrow"]),
        ('€', ["euros", "euro"]),
        ('£', ["pound", "pounds"]),
        ('₹', ["rupee", "rupees"]),
        ('™', ["trade mark", "trademark"]),
        ('©', ["copyright"]),
        ('®', ["registered", "registered sign"]),
        ('°', ["degrees", "degree"])
    ];

    internal static void Run(
        EspeakWrapper bridge,
        PiperConfig config,
        MixedLanguagePhonemizer detector,
        PhonemeFallbackMapper adapter,
        Action<string, Action> check)
    {
        foreach (string voice in new[] { "en-us", "en" })
        {
            foreach (var fixture in EnglishDescriptions)
            {
                check($"Complete symbol description ({voice}): {fixture.Symbol}", () =>
                {
                    string[] expected = ReadDescriptions(bridge, fixture.Names, voice);
                    string actual = ReadSymbol(bridge, fixture.Symbol, voice);
                    Require(expected.Any(ipa => SameSpokenContent(ipa, actual)),
                        $"Expected a complete spoken description: {string.Join(" / ", expected)}; actual: {actual}");
                });
            }
        }

        foreach (string voice in new[] { "en-us", "fr", "es", "uk" })
        {
            foreach (char symbol in "_?.&<>|^")
            {
                check($"Literal character name preserved ({voice}): {symbol}", () =>
                {
                    string expected = WithoutLanguageLabels(bridge.ReadNativeCharacterForTest(symbol, voice));
                    Require(ReadSymbol(bridge, symbol, voice) == expected,
                        "The literal character name was replaced by ordinary text reading.");
                });
            }

            check($"Compatible fullwidth punctuation ({voice})", () =>
            {
                foreach (var pair in new[] { ('＿', '_'), ('？', '?'), ('．', '.'), ('＋', '+'), ('＆', '&') })
                    Require(ReadSymbol(bridge, pair.Item1, voice) == ReadSymbol(bridge, pair.Item2, voice),
                        $"Fullwidth U+{(int)pair.Item1:X4} differs from its compatible character.");
            });
        }

        foreach (var fixture in new[]
        {
            (Voice: "fr", Symbols: "∞€£±×÷°"),
            (Voice: "es", Symbols: "€£°")
        })
        {
            foreach (char symbol in fixture.Symbols)
            {
                check($"Existing localized name preserved ({fixture.Voice}): {symbol}", () =>
                {
                    string expected = WithoutLanguageLabels(bridge.ReadNativeCharacterForTest(symbol, fixture.Voice));
                    Require(ReadSymbol(bridge, symbol, fixture.Voice) == expected,
                        "A working local character name was replaced by English pronunciation.");
                });
            }
        }

        var fallbackDescriptions = EnglishDescriptions.Where(fixture => "≤≥√∑∫→←".Contains(fixture.Symbol)).ToArray();
        foreach (string voice in new[] { "fr", "es", "uk" })
        {
            foreach (var fixture in fallbackDescriptions)
            {
                check($"Complete existing English fallback ({voice}): {fixture.Symbol}", () =>
                {
                    string literal = bridge.ReadNativeCharacterForTest(fixture.Symbol, voice);
                    // Newer dictionaries can add local descriptions. The English fallback contract
                    // applies only where the native character path actually selects English.
                    if (!literal.Contains("(en)", StringComparison.Ordinal))
                    {
                        Require(ReadSymbol(bridge, fixture.Symbol, voice).Length > 0,
                            "The local dictionary returned no pronunciation.");
                        return;
                    }

                    string[] expected = ReadDescriptions(bridge, fixture.Names, "en");
                    string actual = ReadSymbol(bridge, fixture.Symbol, voice);
                    Require(expected.Any(ipa => SameSpokenContent(ipa, actual)),
                        $"The English fallback lost part of the name. Expected: {string.Join(" / ", expected)}; actual: {actual}");
                });
            }

            check($"Symbol fallback restores selected voice ({voice})", () =>
            {
                Require(bridge.TryGetIpaPhonemes("Ordinary text is still readable.", voice, out string expected),
                    "The reference text voice is unavailable.");
                ReadSymbol(bridge, '≤', voice);
                Require(bridge.GetIpaPhonemes("Ordinary text is still readable.") == expected,
                    "English fallback changed the voice used by subsequent text.");
                Require(!bridge.TryGetCharacterPhonemes('≤', "invalid-symbol-routing-voice", out _),
                    "An unavailable requested voice unexpectedly succeeded.");
                Require(bridge.TryGetIpaPhonemes("Ordinary text is still readable.", voice, out string after) && after == expected,
                    "Failed symbol voice selection contaminated the next text operation.");
            });
        }

        var englishConfig = new PiperConfig { Espeak = new() { Voice = "en-us" }, PhonemeIdMap = config.PhonemeIdMap };
        var tensorMapper = new PiperPhonemizer(englishConfig, NullLogger<PiperPhonemizer>.Instance);
        var stressIds = new HashSet<long>(new[] { "ˈ", "ˌ" }
            .Where(config.PhonemeIdMap.ContainsKey).SelectMany(mark => config.PhonemeIdMap[mark]).Select(id => (long)id));
        foreach (bool useDetector in new[] { true, false })
        {
            var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(englishConfig), englishConfig,
                useDetector ? detector : null, adapter);
            foreach (var fixture in EnglishDescriptions)
            {
                check($"Complete symbol name reaches Piper IDs ({useDetector}): x{fixture.Symbol}y", () =>
                {
                    string[] expected = ReadDescriptions(bridge, fixture.Names, "en-us").Select(ipa =>
                        phonemizer.GetPhonemes($"x [[{ipa}]] y", "en-us")).ToArray();
                    string input = $"x{fixture.Symbol}y";
                    string actual = phonemizer.GetPhonemes(input, "en-us");
                    Require(expected.Any(ipa => SameSpokenContent(ipa, actual)),
                        $"A complete native name did not reach the adapted stream. Actual: {actual}; expected: {string.Join(" / ", expected)}");
                    long[] ids = tensorMapper.PhonemesToIds(actual);
                    Require(expected.Any(ipa => tensorMapper.PhonemesToIds(ipa).Where(id => !stressIds.Contains(id))
                        .SequenceEqual(ids.Where(id => !stressIds.Contains(id)))),
                        "The complete name did not produce the reference Piper IDs.");
                    Require(phonemizer.GetPhonemes(input, "en-us") == actual,
                        "The cached symbol name differs from the initial complete pronunciation.");
                });
            }

            check($"Compatible underscore reaches the same Piper IDs ({useDetector})", () =>
            {
                string ordinary = phonemizer.GetPhonemes("user_name", "en-us");
                string wide = phonemizer.GetPhonemes("user＿name", "en-us");
                Require(wide == ordinary && tensorMapper.PhonemesToIds(wide).SequenceEqual(tensorMapper.PhonemesToIds(ordinary)),
                    "The wide underscore was spoken as its Unicode code instead of underscore.");
            });
        }

        check("Concurrent symbol routing preserves callbacks and per-call voices", () =>
        {
            var samples = new[] { ('≤', "en-us"), ('√', "fr"), ('∞', "fr"), ('€', "es"), ('≤', "uk"), ('_', "en-us") }
                .Select(pair => (pair.Item1, pair.Item2, Expected: ReadSymbol(bridge, pair.Item1, pair.Item2))).ToArray();
            int mismatches = 0;
            Parallel.For(0, 16, _ =>
            {
                foreach (var sample in samples)
                    if (ReadSymbol(bridge, sample.Item1, sample.Item2) != sample.Expected)
                        Interlocked.Increment(ref mismatches);
            });
            Require(mismatches == 0, "A concurrent request changed a symbol's selected voice or pronunciation.");
        });
    }

    private static string ReadText(EspeakWrapper bridge, string text, string voice)
    {
        Require(bridge.TryGetIpaPhonemes(text, voice, out string ipa) && ipa.Length > 0,
            $"The reference voice '{voice}' could not read '{text}'.");
        return WithoutLanguageLabels(ipa);
    }

    private static string ReadSymbol(EspeakWrapper bridge, char symbol, string voice)
    {
        Require(bridge.TryGetCharacterPhonemes(symbol, voice, out string ipa) && ipa.Length > 0,
            $"No pronunciation for U+{(int)symbol:X4} with voice '{voice}'.");
        Require(!ipa.Contains('(') && !ipa.Contains(')'), "Native language labels leaked into the IPA stream.");
        return ipa;
    }

    // Dictionary expansions and ordinary phrases can assign different stress. A following
    // variable also gives English prepositions their weak form, as in "not equal to x".
    // Generate that independent reference from the declared words, then remove only "x".
    private static string[] ReadDescriptions(EspeakWrapper bridge, string[] names, string voice)
    {
        string marker = ReadText(bridge, "x", voice);
        var descriptions = new List<string>();
        foreach (string name in names)
        {
            descriptions.Add(ReadText(bridge, name, voice));
            string contextual = ReadText(bridge, name + " x", voice);
            Require(contextual.EndsWith(" " + marker, StringComparison.Ordinal),
                "The independent contextual reference did not end with the declared variable.");
            descriptions.Add(contextual[..^(marker.Length + 1)]);
        }
        return descriptions.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool SameSpokenContent(string left, string right) =>
        left.Replace("ˈ", "").Replace("ˌ", "") == right.Replace("ˈ", "").Replace("ˌ", "");

    private static string WithoutLanguageLabels(string ipa) => Regex.Replace(ipa, @"\([a-z][a-z0-9-]*\)", "");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

namespace ONNX_Runner.Services
{
    public partial class EspeakWrapper
    {
        // Test-only direct native reference: bypasses the production symbol-routing method.
        // It keeps working character names as independent controls without changing public APIs.
        internal string ReadNativeCharacterForTest(char character, string voice)
        {
            lock (_espeakLock)
            {
                if (!TrySelectVoiceLocked(voice)) throw new InvalidOperationException($"Reference voice unavailable: {voice}");
                if (_capturedPhonemes != null) throw new InvalidOperationException("Nested native reference call.");
                _capturedPhonemes = new StringBuilder();
                try
                {
                    espeak_SetSynthCallback(_synthCallback);
                    espeak_SetPhonemeCallback(_phonemeCallback);
                    espeak_SetPhonemeTrace(2, GetTraceSinkLocked());
                    int status = espeak_Char(character);
                    if (status != 0) throw new InvalidOperationException($"Native character reference failed: {status}");
                    return _capturedPhonemes.ToString().Trim();
                }
                finally
                {
                    espeak_SetPhonemeTrace(0, IntPtr.Zero);
                    espeak_SetPhonemeCallback(null);
                    espeak_SetSynthCallback(null);
                    _capturedPhonemes = null;
                }
            }
        }
    }
}
