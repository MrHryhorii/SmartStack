using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class SampleRateSynthesisChecks
{
    internal static async Task Run(SpeechSynthesisService synthesis, IServiceProvider services,
        PiperConfig config, NativeAudioDependencies native, Func<string, Func<Task>, Task> check)
    {
        async Task<(byte[] Audio, int HeaderRate)> Read(AudioFormat format, int? rate = null,
            bool stream = false, bool sse = false, string effect = "None", string environment = "None",
            float pitch = 1, float speed = 1, bool openAi = false)
        {
            var request = new SynthesisRequest
            {
                Input = "The file is ready. Open it now.", Voice = "piper_base", Speed = speed,
                Format = format, SampleRate = rate, Stream = stream, EarlySplit = true,
                StreamFormat = sse ? SpeechStreamFormat.Sse : SpeechStreamFormat.Audio,
                Language = config.Espeak.Voice, NoiseScale = 0, NoiseW = 0, Pitch = pitch, Volume = 1,
                Effect = effect, Environment = environment, EffectIntensity = .5f, EnvironmentIntensity = .25f,
                ExtendReverbTail = true
            };
            if (openAi)
            {
                request = OpenAiRequestAdapter.ToSynthesisRequest(new OpenAiSpeechRequest
                {
                    Input = request.Input, Voice = new OpenAiVoice("piper_base"),
                    ResponseFormat = format == AudioFormat.B64Json ? "b64_json" : format.ToString().ToLowerInvariant(),
                    Stream = stream, StreamFormat = sse ? "sse" : "audio"
                }).Request!;
            }
            using var body = new MemoryStream();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Response.Body = body;
            IResult result = await synthesis.SynthesizeAsync(request, context, CancellationToken.None);
            byte[] data = result is FileContentHttpResult file ? file.FileContents.ToArray() : body.ToArray();
            Require(data.Length > 0, "The synthesis response contains no audio.");
            if (sse)
            {
                using var audio = new MemoryStream();
                bool done = false;
                foreach (string line in Encoding.UTF8.GetString(data).Split('\n'))
                {
                    if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                    using var item = JsonDocument.Parse(line.AsMemory(6));
                    JsonElement root = item.RootElement;
                    string? type = root.GetProperty("type").GetString();
                    if (type == "speech.audio.done") { done = true; continue; }
                    Require(type == "speech.audio.delta", "SSE contains an unexpected event.");
                    string field = format == AudioFormat.B64Json ? "audioContent" : "audio";
                    audio.Write(Convert.FromBase64String(root.GetProperty(field).GetString()!));
                }
                Require(done, "SSE did not complete.");
                data = audio.ToArray();
            }
            else if (format == AudioFormat.B64Json)
            {
                using var item = JsonDocument.Parse(data);
                data = Convert.FromBase64String(item.RootElement.GetProperty("audioContent").GetString()!);
            }
            int headerRate = int.Parse(context.Response.Headers["X-Audio-Sample-Rate"].ToString());
            return (data, headerRate);
        }

        byte[] dry = (await Read(AudioFormat.Wav)).Audio;
        foreach (int rate in new[] { 0, config.Audio.SampleRate })
        {
            await check($"Samplerate {rate} preserves native WAV audio exactly", async () =>
            {
                var result = await Read(AudioFormat.Wav, rate);
                Require(result.Audio.AsSpan().SequenceEqual(dry) && result.HeaderRate == config.Audio.SampleRate,
                    "An inactive/equal-rate override changed native PCM.");
            });
        }

        foreach (AudioFormat format in Enum.GetValues<AudioFormat>())
        foreach (int requested in new[] { 8000, 12345, 48000 })
        {
            if (NativeAudioDependencies.RequiresLame(format) && !native.Mp3Available) continue;
            await check($"CPU synthesis delivers {format} at requested rate {requested} in both delivery modes", async () =>
            {
                int actualRate = AudioSampleRate.Resolve(requested, format);
                int headerRate = format == AudioFormat.Opus ? 48000 : actualRate;
                var buffered = await Read(format, requested);
                var streamed = await Read(format, requested, stream: true);
                Require(buffered.HeaderRate == headerRate && streamed.HeaderRate == headerRate,
                    "The sample-rate header differs from the output clock.");
                if (format == AudioFormat.Pcm)
                {
                    float[] samples = WavSamples(dry);
                    double expected = samples.Length * (double)actualRate / config.Audio.SampleRate;
                    Require(Math.Abs(buffered.Audio.Length / 2.0 - expected) <= 1 &&
                        buffered.Audio.AsSpan().SequenceEqual(streamed.Audio), "PCM duration or streaming samples changed.");
                    return;
                }
                Require(SampleRateChecks.ReadEncodedRate(buffered.Audio, format) == actualRate &&
                    SampleRateChecks.ReadEncodedRate(streamed.Audio, format) == actualRate,
                    "Encoded audio disagrees with the resolved sample rate.");
                // Buffered FLAC patches its total sample count; streaming leaves it unknown.
                if (format == AudioFormat.Flac)
                {
                    Require(buffered.Audio.AsSpan(42).SequenceEqual(streamed.Audio.AsSpan(42)),
                        "Delivery timing changed FLAC audio frames.");
                    return;
                }
                // Ogg stream IDs vary per response; the remaining encoders are deterministic here.
                if (format != AudioFormat.Opus)
                    Require(buffered.Audio.AsSpan().SequenceEqual(streamed.Audio), "Delivery timing changed encoded audio.");
            });
        }

        foreach (AudioFormat format in Enum.GetValues<AudioFormat>())
        {
            if (NativeAudioDependencies.RequiresLame(format) && !native.Mp3Available) continue;
            await check($"SSE preserves requested sample rate and codec for {format}", async () =>
            {
                int actualRate = AudioSampleRate.Resolve(8000, format);
                var normal = await Read(format, 8000, stream: true);
                var sse = await Read(format, 8000, stream: true, sse: true);
                Require(sse.HeaderRate == (format == AudioFormat.Opus ? 48000 : actualRate), "SSE lost the sample-rate header.");
                if (format != AudioFormat.Opus)
                    Require(sse.Audio.AsSpan().SequenceEqual(normal.Audio), "SSE changed the encoded payload.");
                else Require(SampleRateChecks.ReadEncodedRate(sse.Audio, format) == actualRate, "SSE changed Opus input rate.");
            });
        }

        foreach (var (effect, environment, pitch, speed) in new[]
        {
            ("Telephone", "None", 1f, 1f), ("None", "Cave", 1f, 1f),
            ("Telephone", "ConcreteHall", 1f, 1f), ("None", "None", 1.25f, 1.4f)
        })
        {
            await check($"Output resampling preserves DSP timing: {effect}/{environment}, pitch={pitch}, speed={speed}", async () =>
            {
                float[] original = WavSamples((await Read(AudioFormat.Wav, effect: effect,
                    environment: environment, pitch: pitch, speed: speed)).Audio);
                float[] actual = WavSamples((await Read(AudioFormat.Wav, 8000, effect: effect,
                    environment: environment, pitch: pitch, speed: speed)).Audio);
                float[] expected = SampleRateChecks.Resample(original, config.Audio.SampleRate, 8000, [37, 503]);
                Require(actual.Length == expected.Length, "Output conversion changed sentence pauses or reverb-tail duration.");
                double squared = 0;
                for (int i = 0; i < actual.Length; i++) squared += Math.Pow(actual[i] - expected[i], 2);
                Require(Math.Sqrt(squared / actual.Length) < 4.0 / short.MaxValue,
                    "Changing output rate changed DSP processing beyond PCM quantization error.");
            });
        }
        var dsp = services.GetRequiredService<DspSettings>();
        int originalDefault = dsp.DefaultSampleRate;
        try
        {
            foreach (AudioFormat format in Enum.GetValues<AudioFormat>())
            {
                if (NativeAudioDependencies.RequiresLame(format) && !native.Mp3Available) continue;
                var nativeAudio = await Read(format, 0, stream: true);
                foreach (int configured in new[] { 8000, 12345, 48000 })
                {
                    await check($"Server output rate {configured} respects both endpoint contracts for {format}", async () =>
                    {
                        dsp.DefaultSampleRate = configured;
                        foreach (var (rate, openAi) in new (int?, bool)[] { (null, false), (0, false), (16000, false), (null, true) })
                        {
                            int requested = rate ?? configured;
                            int actualRate = AudioSampleRate.Resolve(requested == 0 ? config.Audio.SampleRate : requested, format);
                            var result = await Read(format, rate, stream: true, openAi: openAi);
                            Require(result.HeaderRate == (format == AudioFormat.Opus ? 48000 : actualRate),
                                "Request/configuration precedence changed the output clock.");
                            if (format != AudioFormat.Pcm)
                                Require(SampleRateChecks.ReadEncodedRate(result.Audio, format) == actualRate,
                                    "The encoder ignored the resolved server/request rate.");
                            else if (!openAi)
                                Require(Math.Abs(result.Audio.Length / 2.0 - WavSamples(dry).Length *
                                    (double)actualRate / config.Audio.SampleRate) <= 1, "Server resampling changed PCM duration.");
                            if (rate == 0 && format != AudioFormat.Opus)
                                Require(result.Audio.AsSpan().SequenceEqual(nativeAudio.Audio),
                                    "Explicit zero retained the server's output-rate override.");
                        }
                    });
                }
            }
            await check("The disabled effects master switch preserves server output resampling", async () =>
            {
                var effects = services.GetRequiredService<EffectsSettings>();
                bool originalEnabled = effects.EnableGlobalEffects;
                dsp.DefaultSampleRate = 8000;
                try
                {
                    effects.EnableGlobalEffects = false;
                    var result = await Read(AudioFormat.Wav);
                    Require(result.HeaderRate == 8000 && SampleRateChecks.ReadEncodedRate(result.Audio, AudioFormat.Wav) == 8000,
                        "Disabling artistic effects also disabled the output-rate setting.");
                }
                finally { effects.EnableGlobalEffects = originalEnabled; }
            });
        }
        finally { dsp.DefaultSampleRate = originalDefault; }
    }

    internal static float[] WavSamples(byte[] data)
    {
        Require(data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WAVE"u8), "Invalid WAV response.");
        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
            if (data.AsSpan(offset, 4).SequenceEqual("data"u8))
            {
                Require(size >= 0 && offset + 8 + size <= data.Length && size % 2 == 0, "WAV PCM size is invalid.");
                var result = new float[size / 2];
                for (int i = 0; i < result.Length; i++)
                    result[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 8 + i * 2, 2)) / (float)short.MaxValue;
                return result;
            }
            Require(size >= 0, "WAV chunk size is invalid.");
            offset += 8 + size + (size & 1);
        }
        throw new InvalidOperationException("WAV contains no PCM data.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
