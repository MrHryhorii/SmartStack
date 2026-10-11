using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CloningChecks
{
    internal static async Task Run(PiperConfig config, PiperRunner piper, UnifiedPhonemizer phonemizer,
        NativeAudioDependencies native, string clonerDirectory, string voicesDirectory,
        Func<string, Func<Task>, Task> check)
    {
        var tone = JsonSerializer.Deserialize<ToneConfig>(File.ReadAllText(Path.Combine(clonerDirectory, "tone_config.json")))!;
        using var openVoice = new OpenVoiceRunner(Path.Combine(clonerDirectory, "tone_extract.onnx"),
            Path.Combine(clonerDirectory, "tone_color.onnx"), tone,
            new OnnxSettings { Cpu = { IntraOpNumThreads = 1 } }, new HardwareSettings(), NullLogger<OpenVoiceRunner>.Instance);
        var processor = new AudioProcessor(tone);
        var cloner = new ClonerSettings();
        new BaseVoiceGenerator(phonemizer, piper, processor, openVoice, config, cloner).GenerateAndCacheBaseFingerprint();
        openVoice.UnloadExtractor();
        openVoice.VoiceLibrary["female"] = openVoice.LoadVoiceFingerprint(Path.Combine(voicesDirectory, "female.voice"));

        var dsp = new DspSettings { EnableLowPassFilter = true, LowPassCutoffFrequency = 1000, VolumeBoosterDb = 0 };
        using var services = new ServiceCollection()
            .AddSingleton(config).AddSingleton(piper).AddSingleton(phonemizer).AddSingleton(openVoice).AddSingleton(processor)
            .AddSingleton(cloner).AddSingleton(dsp).AddSingleton(new EffectsSettings { EnableGlobalEffects = false })
            .AddSingleton(new ChunkerSettings { EarlySplit = false, SentencePauseSeconds = 0 })
            .AddSingleton(new PhonemizerSettings { UseLanguageDetector = false }).AddSingleton(new StreamSettings { EnableStreaming = false })
            .AddSingleton(new ApiSettings())
            .AddSingleton(new TextChunker(new ChunkerSettings())).BuildServiceProvider();
        using var gate = new SemaphoreSlim(1);
        var synthesis = new SpeechSynthesisService(gate, services, native, NullLogger<SpeechSynthesisService>.Instance);

        async Task<byte[]> ReadAudio(string voice = "piper_base", float? intensity = null)
        {
            var request = new SynthesisRequest
            {
                Input = "The variable user_name is ready.", Voice = voice, Speed = 1, Format = AudioFormat.Wav,
                Stream = false, EarlySplit = false, NoiseScale = 0, NoiseW = 0, Language = config.Espeak.Voice,
                CloneIntensity = intensity, Effect = "None", Environment = "None"
            };
            IResult result = await synthesis.SynthesizeAsync(request, new DefaultHttpContext { RequestServices = services }, CancellationToken.None);
            if (result is not FileContentHttpResult file) throw new InvalidOperationException("Cloning check did not return buffered audio.");
            return file.FileContents.ToArray();
        }

        byte[] dry = await ReadAudio();
        foreach (string voice in new[] { "unknown-voice", "  " })
        {
            await check($"Unavailable voice '{voice}' preserves dry audio with cloning and its filter enabled", async () =>
                Require((await ReadAudio(voice)).SequenceEqual(dry), "Fallback voice retained cloning-only filtering or sample-rate conversion."));
        }
        await check("Zero clone intensity bypasses conversion and its low-pass filter", async () =>
            Require((await ReadAudio("female", 0)).SequenceEqual(dry), "Zero-intensity cloning changed base audio."));
        await check("A loaded cloned voice remains active with global DSP effects disabled", async () =>
        {
            byte[] cloned = await ReadAudio("female");
            Require(cloned.Length > 44 && !cloned.SequenceEqual(dry), "The global DSP switch incorrectly disabled voice conversion.");
        });
        await check("A missing source fingerprint falls back without cloning-only processing", async () =>
        {
            float[] source = openVoice.VoiceLibrary["piper_base"];
            openVoice.VoiceLibrary.Remove("piper_base");
            try { Require((await ReadAudio("female")).SequenceEqual(dry), "Missing source retained cloning-only processing."); }
            finally { openVoice.VoiceLibrary["piper_base"] = source; }
        });
        await check("Disabled cloning returns the base voice even when models are loaded", async () =>
        {
            cloner.EnableCloning = false;
            Require((await ReadAudio("female")).SequenceEqual(dry), "Loaded models bypassed the cloning switch.");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
