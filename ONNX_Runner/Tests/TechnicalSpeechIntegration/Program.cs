global using Microsoft.Extensions.Logging;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

if (args.Length is < 1 or > 3 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: <model.onnx.json> [native-library-path] [eSpeak-data-parent]");
    return 2;
}

var config = JsonSerializer.Deserialize<PiperConfig>(File.ReadAllText(args[0]))
    ?? throw new InvalidDataException("Model configuration is empty.");
string voice = config.Espeak.Voice ?? throw new InvalidDataException("Model has no eSpeak voice.");
string nativeDirectory = Path.GetFullPath("PiperNative");
string filename = OperatingSystem.IsWindows() ? "espeak-ng.dll" :
    OperatingSystem.IsMacOS() ? "libespeak-ng.dylib" : "libespeak-ng.so.1";
string localLibrary = Path.Combine(nativeDirectory, filename);
string library = args.Length > 1 ? Path.GetFullPath(args[1]) :
    File.Exists(localLibrary) ? localLibrary : filename;
NativeLibraryResolver.Initialize(library);
using var bridge = new EspeakWrapper(args.Length > 2 ? Path.GetFullPath(args[2]) : nativeDirectory, voice);
var names = new Dictionary<char, string>();
int total = 0, failures = 0;

foreach (char symbol in "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~≤×±♯€＿")
{
    Check($"Native name U+{(int)symbol:X4}", () =>
    {
        Require(bridge.TryGetCharacterPhonemes(symbol, voice, out string ipa) && ipa.Length > 0,
            "Native character name is empty.");
        names.Add(symbol, ipa);
    });
}

Require(bridge.TryGetIpaPhonemes("Ordinary text is still readable.", voice, out string normal),
    "Model voice cannot be selected.");
Check("Callbacks and trace are restored after character calls", () =>
{
    bridge.TryGetCharacterPhonemes('_', voice, out _);
    Require(bridge.TryGetIpaPhonemes("Ordinary text is still readable.", voice, out string after) && after == normal,
        "Character capture changed subsequent text phonemization.");
    Require(!bridge.TryGetCharacterPhonemes('_', "invalid-technical-test-voice", out _),
        "Invalid voice unexpectedly succeeded.");
    Require(bridge.TryGetIpaPhonemes("Ordinary text is still readable.", voice, out after) && after == normal,
        "A failed voice change contaminated subsequent text.");
});

Check("Concurrent text and character operations preserve native state", () =>
{
    int mismatches = 0;
    Parallel.For(0, 8, _ =>
    {
        foreach (var pair in names)
            if (!bridge.TryGetCharacterPhonemes(pair.Key, voice, out string ipa) || ipa != pair.Value)
                Interlocked.Increment(ref mismatches);
        if (!bridge.TryGetIpaPhonemes("Ordinary text is still readable.", voice, out string text) || text != normal)
            Interlocked.Increment(ref mismatches);
    });
    Require(mismatches == 0, "Concurrent calls changed the selected voice or callback output.");
});

var rules = TextChunkerRules.Default;
var chunker = new TextChunker(new ChunkerSettings(), rules);
var detector = new MixedLanguagePhonemizer(new PhonemizerSettings { SupportedLanguages = ["en", "fr", "es", "uk"] },
    voice, NullLogger<MixedLanguagePhonemizer>.Instance, chunker);
var adapter = new PhonemeFallbackMapper(Path.Combine("PHOIBLE", "phoible.csv"), config);
var tensorMapper = new PiperPhonemizer(config, NullLogger<PiperPhonemizer>.Instance);

string[] fixtures =
[
    "_", "_$^", "C++", "a+b", "a&b", "a*b", "a^b", "$HOME", "user_name", "_name", "name_",
    "a±b", "C♯", "€amount", "user＿name", "*.json", "*args", "C*",
    "data[0]", "call(obj)", "user's_name.txt", "Compare A-B with A_B.",
    "foo?.Bar()", "a!=b", "a?b:c", "List<T>", "data[0]=value", "obj={x:1}", "x≤y", "a×b",
    "config.prod.json\"tail", "config.prod.json", "ONNX_Runner.exe", "test.user+tts@example.co.uk",
    "https://example.com/a/b?q=hello.world&x=1.25#part-2",
    "https://user:pass@example.co.uk:8443/api/v2.3/items?id=42&debug=true",
    "C:\\Users\\Test\\build_output\\config.prod.json", "/home/user/test-data/v1.2.3/config.json",
    "Call System.Text.Json.JsonSerializer.Serialize(obj).",
    "Maya checked config.prod.json—twice—and whispered.",
    "The server is running at 192.168.1.25:5045.",
    "The response time is 2.75 milliseconds. Version v1.0.9 was released at 8:30 a.m.",
    "Wait... did you hear that? Yes! Everything works... doesn't it?"
];

foreach (bool useDetector in new[] { true, false })
{
    var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config, rules), config,
        useDetector ? detector : null, adapter);
    Check($"Native pipeline ({useDetector}): forced Ukrainian text keeps model-voice symbol names", () =>
    {
        Require(bridge.TryGetIpaPhonemes("дані", "uk", out string text) && text.Length > 0,
            "The native Ukrainian voice is unavailable.");
        string name = phonemizer.GetPhonemes("_", "uk");
        string mixed = phonemizer.GetPhonemes("дані_значення", "uk");
        Require(mixed.Contains(name, StringComparison.Ordinal) && mixed != name,
            "Forced text language replaced the cached symbol name or dropped text parts.");
    });
    foreach (string fixture in fixtures)
    {
        Check($"Native pipeline ({useDetector}): {fixture}", () =>
        {
            string ipa = phonemizer.GetPhonemes(fixture, voice);
            Require(ipa.Length > 0 && !ipa.Contains('_') && !ipa.Contains('^') && !ipa.Contains('$'),
                "Literal control symbols reached the phoneme stream.");
            for (int index = 0; index < ipa.Length;)
            {
                int length = StringInfo.GetNextTextElementLength(ipa.AsSpan(index));
                string phoneme = ipa.Substring(index, length);
                Require(config.PhonemeIdMap.ContainsKey(phoneme), $"Unmapped IPA would disappear from IDs: {phoneme}");
                index += length;
            }
            long[] ids = tensorMapper.PhonemesToIds(ipa);
            Require(ids.Length > 4 && ids.Count(id => id == config.PhonemeIdMap["^"][0]) == 1 &&
                ids.Count(id => id == config.PhonemeIdMap["$"][0]) == 1,
                "The tensor has missing content or extra structural sentence IDs.");
            Require(phonemizer.GetPhonemes(fixture, voice) == ipa, "Cached names changed the phoneme output.");
        });
    }
}

// Each input has unambiguous prose in the declared language. The forced native route is an
// independent pronunciation reference for the automatic route, including the verb between code.
var automaticFixtures = new (string Text, string Language)[]
{
    ("The variable user_name contains build_output.", "en"),
    ("The file config.prod.json contains the expected values in build_output.", "en"),
    ("La variable user_name contient une valeur valide dans build_output.", "fr"),
    ("La variable user_name contiene un valor válido en build_output.", "es"),
    ("Dr. Morgan and J. R. R. Tolkien checked user_name before opening build_output.", "en")
};
foreach (var fixture in automaticFixtures)
{
    Check($"Native automatic language matches declared prose: {fixture.Language}; {fixture.Text}", () =>
    {
        var automatic = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config, rules), config,
            detector, adapter);
        var reference = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config, rules), config,
            fallbackMapper: adapter);
        string expected = reference.GetPhonemes(fixture.Text, fixture.Language);
        string actual = automatic.GetPhonemes(fixture.Text);
        Require(actual == expected, $"Automatic language changed native pronunciation. Expected: {expected}; actual: {actual}");
        Require(tensorMapper.PhonemesToIds(actual).SequenceEqual(tensorMapper.PhonemesToIds(expected)),
            "Correct native pronunciation did not produce the reference tensor IDs.");
    });
}

SymbolPronunciationChecks.Run(bridge, config, detector, adapter, Check);

// The declared English model language keeps these routing fixtures independent of the
// supplied Piper inventory. Both routes use that inventory and the same native dictionaries.
var scriptOnlyDetector = new MixedLanguagePhonemizer(new PhonemizerSettings(),
    "en-us", NullLogger<MixedLanguagePhonemizer>.Instance, chunker);
var scriptOnlyPhonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config, rules),
    config, scriptOnlyDetector, adapter);
var explicitReference = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config, rules),
    config, fallbackMapper: adapter);
foreach (var fixture in new (string Text, string Language, string? Forced)[]
{
    ("The variable user_name contains build_output.", "en-us", null),
    ("Dr. Morgan and J. R. R. Tolkien checked config.prod.json.", "en-us", null),
    ("watashi wa gakusei desu", "en-us", null),
    ("Καλημέρα", "el", null),
    ("Їжак дані_значення", "uk", null),
    ("かな", "ja", null),
    ("漢字", "cmn", null),
    ("La variable user_name contient une valeur valide.", "fr", "fr")
})
{
    Check($"Native model/script routing matches explicit reference: {fixture.Language}; {fixture.Text}", () =>
    {
        string expected = explicitReference.GetPhonemes(fixture.Text, fixture.Language);
        string actual = scriptOnlyPhonemizer.GetPhonemes(fixture.Text, fixture.Forced);
        Require(actual.Length > 0 && actual == expected, "Routing without Lingua changed native pronunciation.");
        Require(tensorMapper.PhonemesToIds(actual).SequenceEqual(tensorMapper.PhonemesToIds(expected)),
            "Routing without Lingua changed the reference tensor IDs.");
    });
}

Console.WriteLine($"Result: {total - failures}/{total} passed");
return failures == 0 ? 0 : 1;

void Check(string name, Action action)
{
    total++;
    try { action(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
