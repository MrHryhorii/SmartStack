using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Services.Inference;
using ONNX_Runner.Services.Infrastructure;

int passed = 0;
int failed = 0;

Check("Legacy configuration retains eSpeak and no explicit default speaker", () =>
{
    var config = Read("{}");
    Require(config.PhonemeType == "espeak" && config.DefaultSpeakerId is null, "Legacy defaults changed.");
});
Check("Explicit null metadata remains distinguishable from absent metadata", () =>
{
    var config = Read("""{"phoneme_type":null,"default_speaker_id":null}""");
    Require(config.PhonemeType is null && config.DefaultSpeakerId is null, "Explicit null was replaced.");
});
Check("Model default speaker is read without a speaker name map", () =>
{
    var config = Read("""{"num_speakers":4,"default_speaker_id":3}""");
    Require(Select(config) == 3, "A valid unnamed model default was ignored.");
});
Check("Configured speaker wins over the model default", () =>
    Require(Select(Model(2), "voice-a") == 1, "Server selection lost its priority."));
Check("Configured speaker accepts surrounding whitespace", () =>
    Require(Select(Model(2), "  voice-b  ") == 3, "Server selection was not trimmed."));
Check("Configured speaker remains valid when the model default is invalid", () =>
    Require(Select(Model(-7), "voice-b") == 3, "An unused invalid default blocked a valid override."));
Check("Missing configured speaker falls back to the model default", () =>
    Require(Select(Model(2), "missing") == 2, "Unknown selection bypassed the model default."));
Check("Numeric speaker names are map keys rather than direct IDs", () =>
{
    var config = Model(2);
    config.SpeakerIdMap["123"] = 1;
    Require(Select(config, "123") == 1 && Select(config, "3") == 2, "Numeric names were reinterpreted as IDs.");
});
Check("Existing case-sensitive speaker keys remain distinct", () =>
{
    var config = Model(2);
    config.SpeakerIdMap["VOICE-A"] = 0;
    Require(Select(config, "voice-a") == 1 && Select(config, "VOICE-A") == 0,
        "Speaker names were collapsed by case.");
});
foreach (string? selection in new string?[] { null, "", "   " })
    Check($"Empty selection uses model default ({selection ?? "null"})", () =>
        Require(Select(Model(2), selection) == 2, "Empty selection did not use the model default."));

foreach (int invalidId in new[] { int.MinValue, -1, 4, int.MaxValue })
{
    Check($"Invalid configured ID falls back to model default ({invalidId})", () =>
    {
        var config = Model(2);
        config.SpeakerIdMap["bad"] = invalidId;
        var logger = new CaptureLogger();
        Require(Select(config, "bad", logger) == 2 && logger.Warnings > 0,
            "Invalid configured ID was accepted or silently discarded.");
    });
    Check($"Invalid model default falls back to lowest valid mapped ID ({invalidId})", () =>
    {
        var config = Model(invalidId);
        config.SpeakerIdMap["negative"] = -1;
        config.SpeakerIdMap["too-large"] = 4;
        var logger = new CaptureLogger();
        Require(Select(config, logger: logger) == 1 && logger.Warnings > 0,
            "Invalid default or mapped ID reached the selected speaker.");
    });
}
Check("Absent model default preserves the existing lowest-ID fallback", () =>
    Require(Select(Model(null)) == 1, "Legacy mapped fallback changed."));
Check("Explicit default zero wins over a nonzero mapped fallback", () =>
    Require(Select(Model(0)) == 0, "Explicit speaker zero was treated as missing."));
Check("Equal mapped IDs preserve the first named fallback", () =>
{
    var config = Model(null);
    config.SpeakerIdMap["same-id"] = 1;
    var logger = new CaptureLogger();
    Require(Select(config, logger: logger) == 1 && logger.Messages.Any(message => message.Contains("'voice-a'")),
        "Equal-ID fallback order changed.");
});
foreach (bool nullMap in new[] { false, true })
{
    Check($"Empty speaker map accepts a valid model default (null={nullMap})", () =>
    {
        var config = Model(3);
        config.SpeakerIdMap = nullMap ? null! : [];
        Require(Select(config, "missing") == 3, "Missing speaker names blocked the default.");
    });
    Check($"Empty speaker map retains zero fallback (null={nullMap})", () =>
    {
        var config = Model(null);
        config.SpeakerIdMap = nullMap ? null! : [];
        Require(Select(config) == 0, "Missing speaker names broke the zero fallback.");
    });
}
Check("All invalid mapped IDs retain the zero fallback", () =>
{
    var config = Model(-1);
    config.SpeakerIdMap = new() { ["negative"] = -1, ["outside"] = 4 };
    Require(Select(config, "negative") == 0, "No valid speaker fallback remained.");
});
foreach (int count in new[] { -1, 0, 1 })
    Check($"Single-speaker path does not emit sid (count={count})", () =>
    {
        var config = Model(3);
        config.NumSpeakers = count;
        Require(Select(config, "voice-b") is null, "Single-speaker input behavior changed.");
    });
Check("Highest valid ID is accepted without integer overflow", () =>
{
    var config = Model(int.MaxValue - 1);
    config.NumSpeakers = int.MaxValue;
    Require(Select(config) == int.MaxValue - 1, "A valid large boundary ID was rejected.");
});
Check("Banner identifies the same assembly version without build metadata", () =>
{
    string expected = typeof(ServerIdentity).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    TextWriter original = Console.Out;
    using var output = new StringWriter();
    try
    {
        Console.SetOut(output);
        ServerIdentity.WriteBanner();
    }
    finally
    {
        Console.SetOut(original);
    }
    Require(output.ToString().Trim() == $"Tsubaki TTS Engine v{expected}", "Product name or assembly version diverged.");
});

// Supply the built server DLL to exercise the actual top-level startup guard as well.
if (args.Length > 0)
{
    string serverPath = Path.GetFullPath(args[0]);
    string dotnetHost = args.Length > 1 ? args[1] : "dotnet";
    string fixtureRoot = Path.Combine(Path.GetTempPath(), "Tsubaki model compatibility " + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixtureRoot);
    try
    {
        string primaryPath = Path.Combine(fixtureRoot, "primary.onnx");
        string primaryConfigPath = primaryPath + ".json";
        string modelDirectory = Path.Combine(fixtureRoot, "models");
        string fallbackDirectory = Path.Combine(modelDirectory, "Fallback");
        Directory.CreateDirectory(fallbackDirectory);
        File.WriteAllBytes(primaryPath, [0]);
        File.WriteAllBytes(Path.Combine(fallbackDirectory, "fallback.onnx"), [0]);
        File.WriteAllText(Path.Combine(fallbackDirectory, "fallback.onnx.json"), "{}");
        File.WriteAllText(Path.Combine(fixtureRoot, "appsettings.json"), JsonSerializer.Serialize(new
        {
            ModelSettings = new
            {
                ModelDirectory = modelDirectory,
                ExactModelFilePath = primaryPath,
                ExactConfigFilePath = primaryConfigPath,
                AutoDownloadFallbackModel = false
            },
            ClonerSettings = new { EnableCloning = false },
            StartupSettings = new { OpenBrowserOnStart = false }
        }));

        foreach (string? type in new string?[] { "text", "pinyin", "hebrew", "japanese", "thai", "lithuanian", "future-type", "eSpeak-ng", "", "   ", null })
            Check($"Startup rejects unsupported phoneme_type ({type ?? "null"})", () =>
            {
                File.WriteAllText(primaryConfigPath, JsonSerializer.Serialize(new { phoneme_type = type }));
                var result = RunServer();
                Require(result.ExitCode == 1 && result.Output.Contains("[WARNING] Unsupported phoneme_type:") &&
                    result.Output.Contains("Supported phoneme_type: espeak") &&
                    !result.Output.Contains("LOADED SUCCESSFULLY") && !result.Output.Contains("eSpeak NG") &&
                    !result.Output.Contains("Primary Piper model unavailable"),
                    "Unsupported primary model was initialized, replaced by fallback, or not rejected clearly. " + result.Output);
            });

        foreach (string? type in new string?[] { "espeak", "ESPEAK", "eSpEaK", " espeak ", null })
            Check($"Startup accepts supported or legacy phoneme_type ({type ?? "absent"})", () =>
            {
                File.WriteAllText(primaryConfigPath, type is null ? "{}" : JsonSerializer.Serialize(new { phoneme_type = type }));
                var result = RunServer();
                Require(result.Output.Contains("MODEL LOADED SUCCESSFULLY") &&
                    !result.Output.Contains("Unsupported phoneme_type:"), "Supported frontend was rejected. " + result.Output);
            });

        Check("Startup rejects unsupported cached fallback before initialization", () =>
        {
            File.Delete(primaryPath);
            File.WriteAllText(Path.Combine(fallbackDirectory, "fallback.onnx.json"), """{"phoneme_type":"text"}""");
            var result = RunServer();
            Require(result.ExitCode == 1 && result.Output.Contains("Primary Piper model unavailable") &&
                result.Output.Contains("Unsupported phoneme_type: 'text'") &&
                !result.Output.Contains("LOADED SUCCESSFULLY") && !result.Output.Contains("eSpeak NG"),
                "Unsupported fallback bypassed the startup guard. " + result.Output);
        });

        (int ExitCode, string Output) RunServer()
        {
            var start = new ProcessStartInfo(dotnetHost)
            {
                WorkingDirectory = fixtureRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(serverPath);
            start.ArgumentList.Add("--contentRoot");
            start.ArgumentList.Add(fixtureRoot);
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Server process did not start.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new InvalidOperationException("Server startup did not finish within the test deadline.");
            }
            string output = stdout.GetAwaiter().GetResult();
            string errors = stderr.GetAwaiter().GetResult();
            Require(output.StartsWith("Tsubaki TTS Engine v", StringComparison.Ordinal),
                "Application identity was not the first output. " + output + errors);
            Require(!output.Contains('\u001b'), "Redirected startup output contains ANSI color escapes.");
            return (process.ExitCode, output + errors);
        }
    }
    finally
    {
        Directory.Delete(fixtureRoot, recursive: true);
    }
}

Console.WriteLine($"Passed: {passed}; Failed: {failed}");
return failed == 0 ? 0 : 1;

void Check(string name, Action check)
{
    try
    {
        check();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static PiperConfig Read(string json) => JsonSerializer.Deserialize<PiperConfig>(json)!;
static PiperConfig Model(int? defaultSpeakerId) => new()
{
    NumSpeakers = 4,
    DefaultSpeakerId = defaultSpeakerId,
    SpeakerIdMap = new() { ["voice-b"] = 3, ["voice-a"] = 1 }
};
static long? Select(PiperConfig config, string? speaker = "", CaptureLogger? logger = null) =>
    PiperSpeakerSelector.Resolve(config, new ModelSettings { Speaker = speaker! }, logger ?? new CaptureLogger());

sealed class CaptureLogger : ILogger
{
    public List<string> Messages { get; } = [];
    public int Warnings { get; private set; }
    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
        if (logLevel == LogLevel.Warning) Warnings++;
    }
}
