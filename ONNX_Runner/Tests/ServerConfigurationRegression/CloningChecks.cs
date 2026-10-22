using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CloningChecks
{
    /// <summary>Checks real OpenVoice cloning, fallback behavior, and output-rate precedence with the supplied models.</summary>
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

        await check("Voice fingerprints preserve payload and invalidate outdated normalization", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".voice");
            float[] fingerprint = openVoice.VoiceLibrary["female"];
            try
            {
                openVoice.SaveVoiceFingerprint(path, fingerprint, -23);
                Require(OpenVoiceRunner.IsVoiceFingerprintCurrent(path, -23, fingerprint.Length), "Fresh fingerprint was not recognized.");
                Require(!OpenVoiceRunner.IsVoiceFingerprintCurrent(path, -20, fingerprint.Length), "A changed normalization target retained the cache.");
                Require(openVoice.LoadVoiceFingerprint(path).SequenceEqual(fingerprint), "Versioned fingerprint changed the embedding.");
                var legacy = new byte[fingerprint.Length * sizeof(float)];
                Buffer.BlockCopy(fingerprint, 0, legacy, 0, legacy.Length);
                File.WriteAllBytes(path, legacy);
                Require(!OpenVoiceRunner.IsVoiceFingerprintCurrent(path, -23, fingerprint.Length), "Legacy fingerprint bypassed regeneration.");
                Require(openVoice.LoadVoiceFingerprint(path).SequenceEqual(fingerprint), "A legacy voice without its WAV became unreadable.");
            }
            finally { File.Delete(path); }
            return Task.CompletedTask;
        });

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

        // Runs a configured synthesis request into memory for cloned-audio comparisons.
        async Task<byte[]> ReadAudio(string voice = "piper_base", float? intensity = null, int? sampleRate = null,
            string?[]? profiles = null, float? temperature = null)
        {
            var request = new SynthesisRequest
            {
                Input = "The variable user_name is ready.", Voice = voice, Speed = 1, Format = AudioFormat.Wav,
                Stream = false, EarlySplit = false, NoiseScale = 0, NoiseW = 0, Language = config.Espeak.Voice,
                CloneIntensity = intensity, SampleRate = sampleRate, Equalizer = profiles, ToneTemperature = temperature,
                Effect = "None", Environment = "None"
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
        byte[]? nativeClone = null;
        await check("A loaded cloned voice remains active with global DSP effects disabled", async () =>
        {
            nativeClone = await ReadAudio("female");
            Require(nativeClone.Length > 44 && !nativeClone.SequenceEqual(dry), "The global DSP switch incorrectly disabled voice conversion.");
        });
        foreach (var (configured, rate) in new (int, int?)[]
        {
            (0, 0), (0, 8000), (0, 48000), (8000, null), (8000, 0), (8000, 48000)
        })
        {
            await check($"Cloned audio preserves alignment: server={configured}, request={rate?.ToString() ?? "omitted"}", async () =>
            {
                dsp.DefaultSampleRate = configured;
                try
                {
                    byte[] data = await ReadAudio("female", sampleRate: rate);
                    int requested = rate ?? configured;
                    int expectedRate = requested == 0 ? tone.Data.SamplingRate : requested;
                    Require(SampleRateChecks.ReadEncodedRate(data, AudioFormat.Wav) == expectedRate && nativeClone != null,
                        "Output samplerate replaced the OpenVoice model rate.");
                    float[] samples = SampleRateSynthesisChecks.WavSamples(data);
                    double expected = SampleRateSynthesisChecks.WavSamples(nativeClone!).Length *
                        (double)expectedRate / tone.Data.SamplingRate;
                    Require(Math.Abs(samples.Length - expected) <= 1 && samples.Any(value => MathF.Abs(value) > .001f),
                        "Cloning lost audio or changed duration while adapting the output rate.");
                }
                finally { dsp.DefaultSampleRate = 0; }
            });
        }
        // Zero tau is an internal deterministic fixture, not a new public request range.
        // Ordinary cloning checks above retain the configured nonzero temperature.
        var effects = services.GetRequiredService<EffectsSettings>();
        bool savedEnabled = effects.EnableGlobalEffects;
        bool savedCleanup = dsp.EnableLowPassFilter;
        try
        {
            dsp.EnableLowPassFilter = false;
            effects.EnableGlobalEffects = true;
            await check("Equalizer filters the completed OpenVoice conversion", async () =>
            {
                byte[] original = await ReadAudio("female", temperature: 0);
                float[] expected = SampleRateSynthesisChecks.WavSamples(original);
                AudioEqualizer.Create(["telephony-class-application"], tone.Data.SamplingRate)!.Apply(expected);
                byte[] filtered = await ReadAudio("female", profiles: ["telephony-class-application"], temperature: 0);
                float[] actual = SampleRateSynthesisChecks.WavSamples(filtered);
                Require(filtered.Length == original.Length && !filtered.SequenceEqual(original),
                    "Cloned voice bypassed the profile or changed its duration.");
                Require(actual.Length == expected.Length, "Cloning/profile order changed the output length.");
                float maximum = 0;
                for (int i = 0; i < actual.Length; i++) maximum = Math.Max(maximum, Math.Abs(actual[i] - expected[i]));
                Require(maximum < .0001f, $"Equalizer precedes cloning or resets its filters: {maximum:F7}.");
            });
            await check("Zero-intensity cloning keeps the independent equalizer active", async () =>
            {
                byte[] expected = await ReadAudio(profiles: ["telephony-class-application"]);
                byte[] actual = await ReadAudio("female", 0, profiles: ["telephony-class-application"]);
                Require(actual.SequenceEqual(expected) && !actual.SequenceEqual(dry),
                    "Bypassing cloning also bypassed the profile.");
            });
            await check("Disabled global effects bypass the profile while preserving real cloning", async () =>
            {
                effects.EnableGlobalEffects = false;
                byte[] original = await ReadAudio("female", temperature: 0);
                byte[] actual = await ReadAudio("female", profiles: ["telephony-class-application"], temperature: 0);
                Require(actual.SequenceEqual(original) && !actual.SequenceEqual(dry),
                    "The DSP switch retained the profile or disabled voice cloning.");
            });
        }
        finally
        {
            effects.EnableGlobalEffects = savedEnabled;
            dsp.EnableLowPassFilter = savedCleanup;
        }

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

    /// <summary>Throws with the supplied diagnostic when a regression assertion fails.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
