using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

bool failuresOnly = args.Contains("--failures-only", StringComparer.OrdinalIgnoreCase);
bool synthesisOnly = args.Contains("--synthesis-only", StringComparer.OrdinalIgnoreCase);
string[] paths = args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (args.Any(argument => argument.StartsWith("--", StringComparison.Ordinal) &&
        argument is not ("--failures-only" or "--synthesis-only")) ||
    paths.Length is not (0 or 2) || paths.Any(path => !File.Exists(path)) || synthesisOnly && paths.Length != 2)
{
    Console.Error.WriteLine("Usage: [--failures-only] [--synthesis-only] [<model.onnx> <model.onnx.json>]");
    return 2;
}

int total = 0, failures = 0;
var native = NativeAudioDependencies.Detect();

if (!synthesisOnly)
{
    using var emptyServices = new ServiceCollection().BuildServiceProvider();
    var api = new ApiSettings();
    var effects = new EffectsSettings();
    var cloner = new ClonerSettings();
    var dsp = new DspSettings();
    var stream = new StreamSettings();
    var chunker = new ChunkerSettings();
    var phonemizer = new PhonemizerSettings();
    var rateLimit = new RateLimitSettings();
    JsonElement Status(IServiceProvider? services = null) => Read(InfoEndpoints.GetServerStatus(
        api, effects, cloner, dsp, stream, chunker, phonemizer, rateLimit, native, services ?? emptyServices));

    Check("Health and status use the production assembly version", () =>
    {
        string expected = typeof(InfoEndpoints).Assembly.GetName().Version!.ToString(3);
        JsonElement health = Read(InfoEndpoints.GetHealth(native, emptyServices));
        Require(health.GetProperty("version").GetString() == expected &&
            Status().GetProperty("version").GetString() == expected, "API version differs from the compiled engine.");
    });
    Check("A missing pipeline remains live but cannot synthesize", () =>
    {
        IResult result = InfoEndpoints.GetHealth(native, emptyServices);
        Require(((IStatusCodeHttpResult)result).StatusCode == 200, "Liveness probe rejected a responsive process.");
        JsonElement health = Read(result);
        Require(health.GetProperty("status").GetString() == "ok" &&
            !health.GetProperty("synthesisReady").GetBoolean(), "Liveness was confused with synthesis readiness.");
    });
    Check("Missing model reports a degraded status", () =>
    {
        JsonElement status = Status();
        Require(status.GetProperty("status").GetString() == "degraded" &&
            !status.GetProperty("synthesisReady").GetBoolean() &&
            status.GetProperty("model").ValueKind == JsonValueKind.Null, "Missing model was advertised as ready.");
    });
    Check("Configured cloning does not imply available cloning", () =>
    {
        JsonElement cloning = Status().GetProperty("voiceCloning");
        Require(cloning.GetProperty("enabled").GetBoolean() && !cloning.GetProperty("available").GetBoolean(),
            "Configured cloning was advertised as initialized.");
    });
    Check("An unavailable language pipeline reports its actual mode", () =>
    {
        JsonElement language = Status().GetProperty("language");
        Require(language.GetProperty("autoDetectEnabled").GetBoolean() &&
            language.GetProperty("detectionMode").GetString() == "unavailable" &&
            language.GetProperty("detectionLanguages").GetArrayLength() == 0, "Missing detector was advertised as loaded.");
    });
    Check("A disabled detector remains explicit even without a model", () =>
    {
        phonemizer.UseLanguageDetector = false;
        JsonElement language = Status().GetProperty("language");
        Require(!language.GetProperty("autoDetectEnabled").GetBoolean() &&
            language.GetProperty("detectionMode").GetString() == "disabled", "Detector configuration was lost.");
        phonemizer.UseLanguageDetector = true;
    });
    Check("Model metadata alone cannot claim an initialized pipeline", () =>
    {
        using var metadataOnly = new ServiceCollection().AddSingleton(new PiperConfig
        {
            Espeak = new() { Voice = "en-us" }, Audio = new() { SampleRate = 22050 }
        }).BuildServiceProvider();
        JsonElement status = Status(metadataOnly);
        Require(status.GetProperty("model").ValueKind == JsonValueKind.Object &&
            !status.GetProperty("synthesisReady").GetBoolean(), "Model metadata was mistaken for loaded inference.");
    });
    Check("Unready voice endpoints agree and advertise no phantom base voice", () =>
    {
        JsonElement voices = Read(InfoEndpoints.GetVoices(cloner, native, emptyServices));
        Require(voices.GetProperty("voices").GetArrayLength() == 0 &&
            Status().GetProperty("availableVoices").GetArrayLength() == 0, "An unusable voice was advertised.");
    });
    Check("Status reports every configured detector tuning value", () =>
    {
        phonemizer.LocalWinnerProbabilityFloor = .61;
        phonemizer.LocalWinnerMarginFloor = .13;
        phonemizer.ReliabilityProbabilityThreshold = .73;
        phonemizer.ForeignValidationMaxLetters = 7;
        phonemizer.MaxBonusMultiplier = .31;
        phonemizer.BonusMinLetterCount = 4;
        phonemizer.BonusMaxLetterCount = 28;
        phonemizer.MixedLanguageOverrideThreshold = .92;
        phonemizer.MinSentenceLengthForOverride = 25;
        JsonElement tuning = Status().GetProperty("language").GetProperty("tuning");
        foreach (var (key, expected) in new (string, double)[]
        {
            ("localWinnerProbabilityFloor", .61), ("localWinnerMarginFloor", .13),
            ("reliabilityProbabilityThreshold", .73), ("foreignValidationMaxLetters", 7),
            ("maxBonusMultiplier", .31), ("bonusMinLetterCount", 4), ("bonusMaxLetterCount", 28),
            ("mixedLanguageOverrideThreshold", .92), ("minSentenceLengthForOverride", 25)
        }) Require(tuning.GetProperty(key).GetDouble() == expected, $"Wrong status value for {key}.");
    });
    Check("Status includes all simple chunking controls", () =>
    {
        chunker.EarlySplit = false;
        chunker.MaxChunkLength = 144;
        chunker.SentencePauseSeconds = .125f;
        JsonElement chunking = Status().GetProperty("chunking");
        Require(!chunking.GetProperty("earlySplit").GetBoolean() &&
            chunking.GetProperty("maxChunkLength").GetInt32() == 144 &&
            chunking.GetProperty("sentencePauseSeconds").GetSingle() == .125f, "Chunk defaults were not reported.");
    });
    Check("Muted default volume stays valid JSON", () =>
    {
        dsp.DefaultVolume = 0;
        Require(Status().GetProperty("dsp").GetProperty("defaultTotalGainDb").ValueKind == JsonValueKind.Null,
            "Mute emitted a non-finite gain value.");
    });
    Check("Omitted environment intensity uses the shipped conservative default", () =>
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EffectsSettings:DefaultEnvironment"] = "Cave"
        }).Build();
        var bound = configuration.GetSection("EffectsSettings").Get<EffectsSettings>()!;
        Require(bound.DefaultEnvironmentIntensity == .25f, "Omitted intensity unexpectedly enables a full-strength environment.");
    });

    foreach (var fixture in new (string Name, string Model, string[] Codes, bool Statistical, string[] Expected)[]
    {
        ("Empty candidates", "en-us", [], false, ["en-us"]),
        ("Model dialect duplicates", "en-us", ["en", "en-gb", "en_US"], false, ["en-us"]),
        ("Unknown additional codes", "en-us", ["unknown-test-code"], false, ["en-us"]),
        ("Unmapped model", "as", [], false, []),
        ("Enum aliases", "nb", ["no", "nb"], false, ["nb"]),
        ("Statistical dialect candidates", "en-us", ["fr-ca", "fr"], true, ["en-us", "fr-ca"])
    })
    {
        Check($"Runtime detector metadata: {fixture.Name}", () =>
        {
            var detector = new MixedLanguagePhonemizer(new PhonemizerSettings { SupportedLanguages = [.. fixture.Codes] },
                fixture.Model, NullLogger<MixedLanguagePhonemizer>.Instance);
            Require(detector.UsesStatisticalDetection == fixture.Statistical &&
                detector.DetectionLanguages.Order().SequenceEqual(fixture.Expected.Order()), "Runtime candidates or mode are incorrect.");
            Require(detector.DetectionLanguages is ICollection<string> { IsReadOnly: true }, "Runtime candidates expose mutable state.");
        });
    }
}

if (paths.Length == 2) await SynthesisChecks.Run(paths[0], paths[1], native, CheckAsync);
Console.WriteLine($"Result: {total - failures}/{total} passed");
return failures == 0 ? 0 : 1;

void Check(string name, Action action)
{
    total++;
    try { action(); if (!failuresOnly) Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}

async Task CheckAsync(string name, Func<Task> action)
{
    total++;
    try { await action(); if (!failuresOnly) Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}

static JsonElement Read(IResult result) => JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
