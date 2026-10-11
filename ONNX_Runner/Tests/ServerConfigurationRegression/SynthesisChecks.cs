using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class SynthesisChecks
{
    internal static async Task Run(string modelPath, string configPath, NativeAudioDependencies native,
        Func<string, Func<Task>, Task> check)
    {
        if (!native.EspeakAvailable) throw new InvalidOperationException("Native eSpeak is required for synthesis checks.");
        var config = JsonSerializer.Deserialize<PiperConfig>(File.ReadAllText(configPath))!;
        using var bridge = new EspeakWrapper(Path.GetFullPath("PiperNative"), config.Espeak.Voice ?? "en");
        var tensorMapper = new PiperPhonemizer(config, NullLogger<PiperPhonemizer>.Instance);
        using var piper = new PiperRunner(modelPath, config, tensorMapper,
            new OnnxSettings { Cpu = { IntraOpNumThreads = 1 } }, new HardwareSettings { ForcePiperToCpu = true },
            new ModelSettings(), NullLogger<PiperRunner>.Instance);
        var adapter = new PhonemeFallbackMapper(Path.Combine("PHOIBLE", "phoible.csv"), config);
        var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, fallbackMapper: adapter);
        var effects = new EffectsSettings();
        var cloner = new ClonerSettings { EnableCloning = false };
        var dsp = new DspSettings { VolumeBoosterDb = 0 };
        var chunker = new ChunkerSettings { EarlySplit = false, SentencePauseSeconds = 0 };
        var languages = new PhonemizerSettings { UseLanguageDetector = false };
        var api = new ApiSettings();
        var stream = new StreamSettings { EnableStreaming = false };
        var rateLimit = new RateLimitSettings();
        using var services = new ServiceCollection()
            .AddSingleton(config).AddSingleton(piper).AddSingleton(phonemizer)
            .AddSingleton(cloner).AddSingleton(dsp).AddSingleton(chunker).AddSingleton(languages)
            .AddSingleton(effects).AddSingleton(api).AddSingleton(stream).AddSingleton(new TextChunker(chunker))
            .BuildServiceProvider();
        using var gate = new SemaphoreSlim(1);
        var synthesis = new SpeechSynthesisService(gate, services, native, NullLogger<SpeechSynthesisService>.Instance);

        async Task<byte[]> ReadAudio(string? effect = null, string? environment = null, float volume = 1)
        {
            var request = new SynthesisRequest
            {
                Input = "The variable user_name contains build_output.", Voice = "piper_base", Speed = 1,
                Format = AudioFormat.Wav, Stream = false, EarlySplit = false, Language = config.Espeak.Voice,
                NoiseScale = 0, NoiseW = 0, Volume = volume,
                Effect = effect, Environment = environment, EffectIntensity = 1, EnvironmentIntensity = 1,
                ExtendReverbTail = true
            };
            var context = new DefaultHttpContext { RequestServices = services };
            IResult result = await synthesis.SynthesizeAsync(request, context, CancellationToken.None);
            if (result is not FileContentHttpResult file) throw new InvalidOperationException("Synthesis did not return buffered WAV audio.");
            byte[] bytes = file.FileContents.ToArray();
            Require(bytes.Length > 44 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8), "Synthesis returned an invalid WAV.");
            return bytes;
        }

        byte[] dry = await ReadAudio();
        await check("Loaded CPU pipeline reports synthesis readiness and only its usable base voice", () =>
        {
            JsonElement status = JsonSerializer.SerializeToElement(((IValueHttpResult)InfoEndpoints.GetServerStatus(
                api, effects, cloner, dsp, stream, chunker, languages, rateLimit, native, services)).Value);
            JsonElement health = JsonSerializer.SerializeToElement(((IValueHttpResult)InfoEndpoints.GetHealth(native, services)).Value);
            JsonElement voices = JsonSerializer.SerializeToElement(((IValueHttpResult)InfoEndpoints.GetVoices(cloner, native, services)).Value);
            Require(status.GetProperty("synthesisReady").GetBoolean() && health.GetProperty("synthesisReady").GetBoolean() &&
                status.GetProperty("status").GetString() == "ready" &&
                !status.GetProperty("voiceCloning").GetProperty("available").GetBoolean() &&
                status.GetProperty("language").GetProperty("detectionMode").GetString() == "disabled" &&
                voices.GetProperty("voices").EnumerateArray().Select(item => item.GetString()).SequenceEqual(["piper_base"]),
                "Initialized pipeline capabilities were misreported.");
            return Task.CompletedTask;
        });
        await check("Disabled master switch bypasses both configured DSP stages and reverb tails", async () =>
        {
            effects.EnableGlobalEffects = false;
            effects.DefaultEffect = "Telephone";
            effects.DefaultEnvironment = "Cave";
            byte[] actual = await ReadAudio();
            Require(actual.SequenceEqual(dry), "Disabled DSP changed dry PCM or extended the audio.");
        });
        await check("Explicit request effects cannot bypass the disabled master switch", async () =>
        {
            byte[] actual = await ReadAudio("Overdrive", "ConcreteHall");
            Require(actual.SequenceEqual(dry), "Request overrides bypassed disabled DSP.");
        });
        await check("The DSP master switch preserves ordinary volume control", async () =>
        {
            byte[] actual = await ReadAudio(volume: .5f);
            Require(actual.Length == dry.Length && !actual.SequenceEqual(dry), "Disabling effects also disabled volume control.");
        });
        byte[]? spatial = null;
        await check("DefaultEffect None still allows an enabled spatial environment", async () =>
        {
            effects.EnableGlobalEffects = true;
            effects.DefaultEffect = "None";
            spatial = await ReadAudio();
            Require(!spatial.SequenceEqual(dry) && spatial.Length > dry.Length, "Spatial processing or its reverb tail was bypassed.");
        });
        await check("An explicit None environment overrides its spatial default", async () =>
        {
            byte[] actual = await ReadAudio(environment: "None");
            Require(actual.SequenceEqual(dry), "None environment retained spatial processing or a tail.");
        });
        await check("An explicit None character effect preserves the independent spatial stage", async () =>
        {
            effects.DefaultEffect = "Telephone";
            byte[] actual = await ReadAudio(effect: "None");
            Require(spatial != null && actual.SequenceEqual(spatial), "Character override also changed the spatial stage.");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
