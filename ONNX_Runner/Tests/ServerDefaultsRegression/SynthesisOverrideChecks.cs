using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Models.Requests;
using ONNX_Runner.Services;
using ONNX_Runner.Services.Cloning;
using ONNX_Runner.Services.Inference;
using ONNX_Runner.Services.Infrastructure;
using ONNX_Runner.Services.Text;
using ONNX_Runner.Services.Text.Phonemization;
using static TestRun;

internal static class SynthesisOverrideChecks
{
    internal static async Task Run(TestRun run)
    {
        var native = NativeAudioDependencies.Detect();
        Require(native.EspeakAvailable, "The synthesis mode requires native eSpeak NG.");
        string directory = Path.Combine(Path.GetTempPath(), "Tsubaki-Defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await Verify(run, native, directory); }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task Verify(TestRun run, NativeAudioDependencies native, string directory)
    {
        string piperPath = Path.Combine(directory, "piper.onnx"), colorPath = Path.Combine(directory, "color.onnx");
        File.WriteAllBytes(piperPath, FixtureModels.Piper());
        File.WriteAllBytes(colorPath, FixtureModels.Color());
        var model = JsonSerializer.Deserialize<PiperConfig>(File.ReadAllText("Model/en_US-hfc_female-medium.onnx.json"))!;
        model.Inference = new InferenceConfig { NoiseScale = .13f, LengthScale = .2f, NoiseW = .07f };
        var mapper = new RecordingPhonemizer();
        using var piper = new PiperRunner(piperPath, model, mapper, new OnnxSettings { Cpu = { IntraOpNumThreads = 1 } },
            new HardwareSettings { ForcePiperToCpu = true }, new ModelSettings(), NullLogger<PiperRunner>.Instance);
        foreach (float speed in new[] { .25f, 1, 4 })
        foreach (float? noise in new float?[] { null, 0, .31f })
        foreach (float? durationNoise in new float?[] { null, 0, .29f })
            run.Check($"Native Piper receives model/request scales: speed={speed}, noise={noise}, noiseW={durationNoise}", () =>
            {
                var result = piper.SynthesizeAudioRaw("fixture", speed: speed, requestNoiseScale: noise, requestNoiseW: durationNoise);
                try { Require(result.Length == 3 && result.Buffer.AsSpan(0, 3).SequenceEqual(new[] { noise ?? .13f, .2f / speed, durationNoise ?? .07f }), "Model defaults or explicit zero did not reach the native scales tensor."); }
                finally { ArrayPool<float>.Shared.Return(result.Buffer); }
            });

        var tone = new ToneConfig { Data = { SamplingRate = model.Audio.SampleRate, FilterLength = 4, HopLength = 2 }, Model = { GinChannels = 2 } };
        using var openVoice = new OpenVoiceRunner(colorPath, colorPath, tone, new OnnxSettings { Cpu = { IntraOpNumThreads = 1 } }, new HardwareSettings(), NullLogger<OpenVoiceRunner>.Instance);
        openVoice.UnloadExtractor();
        openVoice.VoiceLibrary["piper_base"] = [.1f, 0];
        openVoice.VoiceLibrary["fixture"] = [0, .1f];
        using var bridge = new EspeakWrapper(Path.GetFullPath("PiperNative"), model.Espeak.Voice);
        var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(model), model,
            fallbackMapper: new PhonemeFallbackMapper(Path.Combine("PHOIBLE", "phoible.csv"), model));
        var dsp = new DspSettings { EnableLowPassFilter = false, VolumeBoosterDb = 0 };
        var effects = new EffectsSettings();
        var cloner = new ClonerSettings { EnableCloning = true, CloneIntensity = .5f, ToneTemperature = .4f };
        var chunker = new ChunkerSettings { SentencePauseSeconds = 0, EarlySplit = false };
        var api = new ApiSettings();
        var stream = new StreamSettings { EnableStreaming = false };
        using var services = new ServiceCollection().AddSingleton(model).AddSingleton(piper).AddSingleton(phonemizer)
            .AddSingleton(openVoice).AddSingleton(new AudioProcessor(tone)).AddSingleton(dsp).AddSingleton(effects)
            .AddSingleton(cloner).AddSingleton(chunker).AddSingleton(api).AddSingleton(stream).AddSingleton(new TextChunker(chunker)).BuildServiceProvider();
        using var gate = new SemaphoreSlim(1, 1);
        var synthesis = new SpeechSynthesisService(gate, services, native, NullLogger<SpeechSynthesisService>.Instance);

        async Task<byte[]> Read(Action<SynthesisRequest>? change = null, string text = "Hello.")
        {
            var request = new SynthesisRequest { Input = text, Voice = "piper_base", Speed = 1, Format = AudioFormat.Wav, Language = "en" };
            change?.Invoke(request);
            var context = new DefaultHttpContext { RequestServices = services };
            IResult result = await synthesis.SynthesizeAsync(request, context, CancellationToken.None);
            Require(result is FileContentHttpResult, "The native fixture failed or did not return buffered WAV.");
            Require(gate.CurrentCount == 1, "A defaults test retained the request permit.");
            return ((FileContentHttpResult)result).FileContents.ToArray();
        }
        byte[] dry = await Read();
        foreach (float? value in new float?[] { null, 0, .5f, 1 })
            await run.CheckAsync("Pipeline volume override with flat server boost: " + value, async () =>
            {
                dsp.DefaultVolume = .3f; dsp.VolumeBoosterDb = 6;
                float[] actual = Samples(await Read(request => request.Volume = value));
                float gain = (value ?? .3f) * MathF.Pow(10, 6 / 20f);
                Match(actual, [.13f * gain, .2f * gain, .07f * gain]);
            });
        dsp.DefaultVolume = 1; dsp.VolumeBoosterDb = 0;
        await run.CheckAsync("Pitch request overrides configured pitch and restores dry PCM", async () =>
        {
            dsp.DefaultPitch = 1.3f;
            Require(!(await Read()).SequenceEqual(dry), "The pitch-default fixture did not exercise pitch processing.");
            Require((await Read(request => request.Pitch = 1)).SequenceEqual(dry), "Explicit unshifted pitch inherited the server shift.");
        });
        dsp.DefaultPitch = 1;
        effects.DefaultEffect = "Telephone";
        await run.CheckAsync("Character default is active and explicit None/zero bypass it", async () =>
        {
            Require(!(await Read()).SequenceEqual(dry), "Configured character effect did not run.");
            Require((await Read(request => request.Effect = "None")).SequenceEqual(dry) && (await Read(request => request.EffectIntensity = 0)).SequenceEqual(dry), "None or zero inherited the character default.");
        });
        effects.DefaultEffect = "None";
        effects.DefaultEnvironment = "Echo";
        effects.ExtendReverbTailOnFinish = false;
        await run.CheckAsync("Spatial default and tail booleans resolve independently", async () =>
        {
            byte[] shortTail = await Read();
            Require((await Read(request => { request.Environment = "None"; request.ExtendReverbTail = true; })).SequenceEqual(dry) &&
                (await Read(request => { request.EnvironmentIntensity = 0; request.ExtendReverbTail = true; })).SequenceEqual(dry), "None or zero inherited the spatial default.");
            byte[] extended = await Read(request => request.ExtendReverbTail = true);
            Require(extended.Length > shortTail.Length, "Explicit true did not override the disabled tail default.");
            effects.ExtendReverbTailOnFinish = true;
            Require((await Read(request => request.ExtendReverbTail = false)).SequenceEqual(shortTail), "Explicit false did not override the enabled tail default.");
        });
        effects.DefaultEnvironment = "None";
        effects.DefaultEqualizer = ["handset-class-device"];
        await run.CheckAsync("Equalizer omission/null inherits; empty request array bypasses", async () =>
        {
            byte[] inherited = await Read();
            Require(!inherited.SequenceEqual(dry), "The configured equalizer did not run.");
            Require((await Read(request => request.Equalizer = null)).SequenceEqual(inherited), "Null lost equalizer inheritance.");
            Require((await Read(request => request.Equalizer = [])).SequenceEqual(dry), "Empty equalizer array inherited configured profiles.");
        });
        await run.CheckAsync("Effects master switch blocks explicit layers and preserves volume", async () =>
        {
            effects.EnableGlobalEffects = false;
            Require((await Read(request => { request.Effect = "Telephone"; request.Environment = "Echo"; request.Equalizer = ["handset-class-device"]; })).SequenceEqual(dry), "Request controls bypassed the effects master switch.");
            Require(!(await Read(request => request.Volume = .5f)).SequenceEqual(dry), "The effects master switch blocked independent volume.");
        });
        effects.EnableGlobalEffects = true; effects.DefaultEqualizer = [];
        dsp.DefaultSampleRate = 8000;
        await run.CheckAsync("Output rate omission inherits while zero bypasses the server rate", async () =>
        {
            Require(BitConverter.ToInt32(await Read(), 24) == 8000, "Omission ignored DefaultSampleRate.");
            Require((await Read(request => request.SampleRate = 0)).SequenceEqual(dry), "Zero did not restore the model rate and original PCM.");
            Require(BitConverter.ToInt32(await Read(request => request.SampleRate = 16000), 24) == 16000, "Explicit output rate did not override the server default.");
        });
        dsp.DefaultSampleRate = 0;
        foreach (float? intensity in new float?[] { null, 0, .5f, 1 })
        foreach (float? temperature in new float?[] { null, .2f, .6f })
            await run.CheckAsync($"Native clone inputs: intensity={intensity}, temperature={temperature}", async () =>
            {
                byte[] actual = await Read(request => { request.Voice = "fixture"; request.CloneIntensity = intensity; request.ToneTemperature = temperature; });
                if (intensity == 0) { Require(actual.SequenceEqual(dry), "Zero clone intensity did not bypass conversion."); return; }
                float amount = intensity ?? .5f;
                Match(Samples(actual), [.1f * MathF.Cos(amount * MathF.PI / 2), .1f * MathF.Sin(amount * MathF.PI / 2), temperature ?? .4f]);
            });
        await run.CheckAsync("Cloning toggle blocks voice/tau/intensity requests", async () =>
        {
            cloner.EnableCloning = false;
            Require((await Read(request => { request.Voice = "fixture"; request.CloneIntensity = 1; request.ToneTemperature = .6f; request.LowPassQFactor = .3f; })).SequenceEqual(dry), "A request bypassed the cloning switch.");
            cloner.EnableCloning = true;
        });
        await run.CheckAsync("Clone cleanup Q overrides defaults only while cloning/filter are active", async () =>
        {
            dsp.EnableLowPassFilter = true; dsp.LowPassCutoffFrequency = 2500; dsp.LowPassQFactor = .6f;
            byte[] configured = await Read(request => request.Voice = "fixture");
            Require((await Read(request => { request.Voice = "fixture"; request.LowPassQFactor = .6f; })).SequenceEqual(configured), "Explicit default Q differed from inherited Q.");
            Require(!(await Read(request => { request.Voice = "fixture"; request.LowPassQFactor = .3f; })).SequenceEqual(configured), "Explicit Q did not reach clone cleanup.");
            Require((await Read(request => request.LowPassQFactor = .3f)).SequenceEqual(dry), "Clone-only Q changed the base voice.");
            dsp.EnableLowPassFilter = false;
        });
        const string clause = "This is a sufficiently long opening clause, and this is a complete following clause in the same sentence.";
        await run.CheckAsync("Early split request true/false overrides both server defaults", async () =>
        {
            byte[] regular = await Read(request => request.EarlySplit = false, clause);
            byte[] early = await Read(request => request.EarlySplit = true, clause);
            Require(early.Length > regular.Length, "The early-split fixture did not create a separate opening chunk.");
            chunker.EarlySplit = true;
            Require((await Read(text: clause)).SequenceEqual(early) && (await Read(request => request.EarlySplit = false, clause)).SequenceEqual(regular), "False/omission resolved the enabled early-split default incorrectly.");
            chunker.EarlySplit = false;
            Require((await Read(text: clause)).SequenceEqual(regular), "Omission ignored the disabled early-split default.");
        });
        await run.CheckAsync("Sentence pause and speed affect only the configured time budget", async () =>
        {
            chunker.SentencePauseSeconds = .1f;
            int ordinary = Samples(await Read(text: "Hello. Hello.")).Length;
            int fast = Samples(await Read(request => request.Speed = 2, "Hello. Hello.")).Length;
            Require(ordinary - fast == 2 * (int)(model.Audio.SampleRate * .1f) - 2 * (int)(model.Audio.SampleRate * .05f), "Sentence pauses ignored configured duration or request speed.");
            chunker.SentencePauseSeconds = 0;
        });
        await run.CheckAsync("MaxTextLength truncates both request shape and generated chunks", async () =>
        {
            api.MaxTextLength = 6;
            SynthesisRequest? sent = null;
            byte[] limited = await Read(request => { request.Input = "Hello. Hello."; sent = request; });
            Require(sent?.Input == "Hello." && limited.SequenceEqual(dry), "Configured text limit did not truncate the second sentence.");
            api.MaxTextLength = 0;
        });
        await run.CheckAsync("Forced pronunciation reaches the phonemizer and cannot alter stored defaults", async () =>
        {
            string before = JsonSerializer.Serialize(new { dsp, effects, cloner, chunker, api, stream, model.Inference });
            await Read(request => request.Language = "en");
            string english = mapper.LastPhonemes;
            await Read(request => request.Language = "fr");
            Require(mapper.LastPhonemes != english, "Forced language did not reach the phonemizer.");
            Require(JsonSerializer.Serialize(new { dsp, effects, cloner, chunker, api, stream, model.Inference }) == before, "A request mutated server defaults.");
        });
    }

    private static float[] Samples(byte[] wave) => MemoryMarshal.Cast<byte, short>(wave.AsSpan(44)).ToArray().Select(sample => sample / 32767f).ToArray();
    private static void Match(float[] actual, float[] expected)
    {
        Require(actual.Length == expected.Length, $"Unexpected fixture duration: {actual.Length} instead of {expected.Length}.");
        Require(actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 4f / 32767), "Resolved controls did not reach native inference or final output gain.");
    }
    private sealed class RecordingPhonemizer : IPhonemizer
    {
        internal string LastPhonemes { get; private set; } = "";
        public long[] PhonemesToIds(string phonemes, bool isContinuation = false, bool isFinished = true)
        {
            LastPhonemes = phonemes;
            return [1, 2, 3];
        }
    }
}
