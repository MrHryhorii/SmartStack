using System.Buffers.Binary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class EqualizerSynthesisChecks
{
    /// <summary>Checks real CPU synthesis with profiles, sentence pauses, DSP order, and output resampling.</summary>
    internal static async Task Run(SpeechSynthesisService synthesis, IServiceProvider services,
        PiperConfig config, Func<string, Func<Task>, Task> check)
    {
        var dsp = services.GetRequiredService<DspSettings>();
        var effects = services.GetRequiredService<EffectsSettings>();
        var chunker = services.GetRequiredService<ChunkerSettings>();
        var savedDsp = (dsp.DefaultSampleRate, dsp.VolumeBoosterDb);
        var savedEffects = effects.EnableGlobalEffects;
        var savedEqualizer = effects.DefaultEqualizer;
        float savedPause = chunker.SentencePauseSeconds;
        dsp.DefaultSampleRate = 0;
        dsp.VolumeBoosterDb = 0;
        effects.EnableGlobalEffects = true;
        effects.DefaultEqualizer = [];
        chunker.SentencePauseSeconds = .017f;

        // Uses deterministic inference and several sentences to exercise continuous per-request filters.
        async Task<byte[]> Read(string?[]? profiles = null, string effect = "None", string environment = "None",
            AudioFormat format = AudioFormat.Wav, bool stream = false, int rate = 0, bool extendTail = false)
        {
            var request = new SynthesisRequest
            {
                Input = "Please check the audio, and listen to the clear voice. The second sentence follows a short pause. The final sentence ends here.",
                Voice = "piper_base", Speed = 1, Format = format, Stream = stream,
                EarlySplit = true, Language = config.Espeak.Voice, NoiseScale = 0, NoiseW = 0,
                Pitch = 1, Volume = 1, Equalizer = profiles, Effect = effect, EffectIntensity = .5f,
                Environment = environment, EnvironmentIntensity = .25f, ExtendReverbTail = extendTail,
                SampleRate = rate
            };
            using var body = new MemoryStream();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Response.Body = body;
            IResult result = await synthesis.SynthesizeAsync(request, context, CancellationToken.None);
            byte[] audio = result is FileContentHttpResult file ? file.FileContents.ToArray() : body.ToArray();
            Require(audio.Length > 44, "Synthesis returned empty audio.");
            return audio;
        }

        try
        {
            byte[] dry = await Read();
            float[] drySamples = WavSamples(dry);
            await check("CPU omitted equalizer inherits server profiles and empty arrays override them", async () =>
            {
                byte[] phone = await Read(["telephony-class-application"]);
                byte[] headphones = await Read(["headphone-class-device"]);
                effects.DefaultEqualizer = ["telephony-class-application"];
                try
                {
                    Require((await Read()).AsSpan().SequenceEqual(phone), "Omitted array did not inherit the default.");
                    Require((await Read([])).AsSpan().SequenceEqual(dry), "Empty array failed to disable the default.");
                    Require((await Read(["headphone-class-device"])).AsSpan().SequenceEqual(headphones),
                        "Explicit profiles were merged with the default rather than replacing it.");
                    Require((await Read(["unknown", null])).AsSpan().SequenceEqual(dry),
                        "Unsupported explicit array fell back to server profiles.");
                    byte[] streamed = await Read(format: AudioFormat.Pcm, stream: true);
                    byte[] buffered = await Read(format: AudioFormat.Pcm);
                    Require(streamed.AsSpan().SequenceEqual(buffered), "Default profiles changed streamed PCM.");
                    effects.EnableGlobalEffects = false;
                    Require((await Read()).AsSpan().SequenceEqual(dry), "Default ignored the DSP master switch.");
                }
                finally { effects.DefaultEqualizer = []; effects.EnableGlobalEffects = true; }
            });
            foreach (string?[]? defaults in new string?[]?[] { null, [], ["unknown", null] })
            {
                await check("CPU inactive server profile lists retain the dry waveform", async () =>
                {
                    effects.DefaultEqualizer = defaults;
                    try { Require((await Read()).AsSpan().SequenceEqual(dry), "Inactive defaults changed audio."); }
                    finally { effects.DefaultEqualizer = []; }
                });
            }
            await check("CPU server defaults retain the 16-entry raw-prefix limit", async () =>
            {
                effects.DefaultEqualizer = Enumerable.Repeat<string?>("unknown", 16)
                    .Append("telephony-class-application").ToArray();
                try { Require((await Read()).AsSpan().SequenceEqual(dry), "The default inspected a discarded 17th entry."); }
                finally { effects.DefaultEqualizer = []; }
            });

            foreach (string?[] profiles in new string?[][] { [], ["unknown"], [null, "", "unknown"] })
            {
                await check("CPU synthesis preserves dry PCM for an empty or unsupported equalizer list", async () =>
                {
                    Require((await Read(profiles)).AsSpan().SequenceEqual(dry), "Bypass changed the established waveform.");
                });
            }
            foreach (string id in AudioEqualizerProfiles.Ids)
            {
                await check($"CPU equalizer {id} changes PCM without changing sentence timing", async () =>
                {
                    byte[] actual = await Read([id]);
                    Require(actual.Length == dry.Length && !actual.AsSpan().SequenceEqual(dry),
                        "Active profile was ignored or changed audio duration.");
                    float[] expected = (float[])drySamples.Clone();
                    AudioEqualizer.Create([id], config.Audio.SampleRate)!.Apply(expected);
                    VolumeShifter.ApplyVolume(expected, 1f, config.Audio.SampleRate);
                    Near(expected, WavSamples(actual));
                });
            }
            await check("CPU equalizer ignores unknown entries among known profiles", async () =>
            {
                byte[] expected = await Read(["wearable-class-device", "telephony-class-application"]);
                byte[] actual = await Read([null, "unknown", "wearable-class-device", "", "telephony-class-application"]);
                Require(expected.AsSpan().SequenceEqual(actual), "Unknown entries changed synthesized audio.");
            });
            await check("CPU synthesis applies repeated profiles repeatedly", async () =>
            {
                byte[] once = await Read(["telephony-class-application"]);
                byte[] twice = await Read(["telephony-class-application", "telephony-class-application"]);
                Require(once.Length == twice.Length && !once.AsSpan().SequenceEqual(twice), "Repeated profile was deduplicated.");
                float[] expected = (float[])drySamples.Clone();
                var ids = new[] { "telephony-class-application", "telephony-class-application" };
                AudioEqualizer.Create(ids, config.Audio.SampleRate)!.Apply(expected);
                VolumeShifter.ApplyVolume(expected, 1f, config.Audio.SampleRate);
                Near(expected, WavSamples(twice));
            });

            foreach (var (effect, environment) in new[]
            {
                ("RingModulator", "None"), ("None", "LivingRoom"), ("RingModulator", "LivingRoom")
            })
            {
                await check($"CPU DSP order remains {effect} then {environment} then equalizer", async () =>
                {
                    float[] expected = (float[])drySamples.Clone();
                    if (effect != "None") new AudioEffectsEngine(effects, config.Audio.SampleRate)
                        .ApplyEffect(expected, VoiceEffectType.RingModulator, .5f);
                    if (environment != "None") new SpatialEffectsEngine(config.Audio.SampleRate)
                        .ApplyEnvironment(expected, SpatialEnvironment.LivingRoom, .25f);
                    AudioEqualizer.Create(["telephony-class-application"], config.Audio.SampleRate)!.Apply(expected);
                    float[] actual = WavSamples(await Read(["telephony-class-application"], effect, environment));
                    VolumeShifter.ApplyVolume(expected, 1f, config.Audio.SampleRate);
                    Near(expected, actual);
                });
            }
            await check("CPU final device profiles preserve streamed PCM exactly", async () =>
            {
                string[] ids = ["wearable-class-device", "handset-class-device"];
                byte[] buffered = await Read(ids, "RingModulator", "LivingRoom", AudioFormat.Pcm);
                byte[] streamed = await Read(ids, "RingModulator", "LivingRoom", AudioFormat.Pcm, stream: true);
                Require(buffered.AsSpan().SequenceEqual(streamed), "Streaming changed filtered PCM or pauses.");
            });
            await check("CPU master switch bypasses all three requested effect categories", async () =>
            {
                effects.EnableGlobalEffects = false;
                try
                {
                    byte[] actual = await Read(["telephony-class-application"], "Overdrive", "ConcreteHall");
                    Require(actual.AsSpan().SequenceEqual(dry), "Equalizer bypassed the disabled DSP master switch.");
                }
                finally { effects.EnableGlobalEffects = true; }
            });

            foreach (string[] ids in AudioEqualizerProfiles.Ids.Select(id => new[] { id }).Concat(new[]
            {
                new[] { "handset-class-device", "telephony-class-application" },
                AudioEqualizerProfiles.Ids.ToArray()
            }))
            {
                await check($"CPU final cascade {string.Join(",", ids)} colors effects and reverb", async () =>
                {
                    float[] expected = (float[])drySamples.Clone();
                    new AudioEffectsEngine(effects, config.Audio.SampleRate).ApplyEffect(expected, VoiceEffectType.RingModulator, .5f);
                    new SpatialEffectsEngine(config.Audio.SampleRate).ApplyEnvironment(expected, SpatialEnvironment.LivingRoom, .25f);
                    AudioEqualizer.Create(ids, config.Audio.SampleRate)!.Apply(expected);
                    VolumeShifter.ApplyVolume(expected, 1f, config.Audio.SampleRate);
                    float[] actual = WavSamples(await Read(ids, "RingModulator", "LivingRoom"));
                    Near(expected, actual, .0002f);
                });
                await check($"CPU final cascade {string.Join(",", ids)} filters complete reverb tails", async () =>
                {
                    float[] actual = WavSamples(await Read(ids, environment: "ConcreteHall", extendTail: true));
                    Require(actual.Length > drySamples.Length && actual.Length <= drySamples.Length + config.Audio.SampleRate * 4,
                        "Tail disappeared, changed speech length, or exceeded the cap.");
                    var spatial = new SpatialEffectsEngine(config.Audio.SampleRate);
                    var equalizer = AudioEqualizer.Create(ids, config.Audio.SampleRate)!;
                    float[] expected = new float[actual.Length];
                    drySamples.CopyTo(expected, 0);
                    spatial.ApplyEnvironment(expected, SpatialEnvironment.ConcreteHall, .25f);
                    equalizer.Apply(expected);
                    // Final gain has one continuous lookahead/release history across speech and tail.
                    VolumeShifter.ApplyVolume(expected, 1f, config.Audio.SampleRate);
                    Near(expected, actual, .0002f);
                    Require(actual.All(float.IsFinite) && actual.Max(Math.Abs) <= 1f, "Filtered reverb produced invalid PCM.");
                    byte[] buffered = await Read(ids, environment: "ConcreteHall", format: AudioFormat.Pcm, extendTail: true);
                    byte[] streamed = await Read(ids, environment: "ConcreteHall", format: AudioFormat.Pcm, stream: true, extendTail: true);
                    Require(buffered.AsSpan().SequenceEqual(streamed), "Tail filtering depends on delivery mode.");
                });
            }

            byte[] nativePcm = await Read(["telephony-class-application"], "RingModulator", "LivingRoom", AudioFormat.Pcm);
            float[] nativeSamples = PcmSamples(nativePcm);
            foreach (int rate in new[] { 8000, 16000, 48000 })
            {
                await check($"CPU equalizer precedes final output resampling to {rate} Hz", async () =>
                {
                    byte[] buffered = await Read(["telephony-class-application"], "RingModulator", "LivingRoom", AudioFormat.Pcm, rate: rate);
                    byte[] streamed = await Read(["telephony-class-application"], "RingModulator", "LivingRoom", AudioFormat.Pcm, stream: true, rate: rate);
                    Require(buffered.AsSpan().SequenceEqual(streamed), "Resampled equalizer output differs between delivery modes.");
                    float[] actual = PcmSamples(buffered);
                    double expectedLength = nativeSamples.Length * (double)rate / config.Audio.SampleRate;
                    Require(Math.Abs(actual.Length - expectedLength) <= 1, "Output rate changed logical audio duration.");
                    float[] beforeVolume = (float[])drySamples.Clone();
                    new AudioEffectsEngine(effects, config.Audio.SampleRate).ApplyEffect(beforeVolume, VoiceEffectType.RingModulator, .5f);
                    new SpatialEffectsEngine(config.Audio.SampleRate).ApplyEnvironment(beforeVolume, SpatialEnvironment.LivingRoom, .25f);
                    AudioEqualizer.Create(["telephony-class-application"], config.Audio.SampleRate)!.Apply(beforeVolume);
                    var reference = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(
                        new FloatSource(beforeVolume, config.Audio.SampleRate), rate);
                    float[] expected = new float[actual.Length + 2];
                    int count = reference.Read(expected, 0, expected.Length);
                    Require(Math.Abs(count - actual.Length) <= 1, "Reference output has a different sample count.");
                    VolumeShifter.ApplyVolume(expected.AsSpan(0, count), 1f, rate);
                    Near(expected.AsSpan(0, Math.Min(count, actual.Length)), actual.AsSpan(0, Math.Min(count, actual.Length)), .00015f);
                });
            }
        }
        finally
        {
            dsp.DefaultSampleRate = savedDsp.DefaultSampleRate;
            dsp.VolumeBoosterDb = savedDsp.VolumeBoosterDb;
            effects.EnableGlobalEffects = savedEffects;
            effects.DefaultEqualizer = savedEqualizer;
            chunker.SentencePauseSeconds = savedPause;
        }
    }

    /// <summary>Compares float references with PCM16 synthesis, allowing only small quantization error.</summary>
    private static void Near(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, float tolerance = .0001f)
    {
        Require(expected.Length == actual.Length, "Filtered waveform has a different length.");
        float maximum = 0;
        for (int i = 0; i < expected.Length; i++) maximum = Math.Max(maximum, Math.Abs(expected[i] - actual[i]));
        Require(maximum <= tolerance, $"Filtered waveform differs from the independent pipeline reference: {maximum:F7}.");
    }

    /// <summary>Reads mono PCM16 from a RIFF data chunk rather than assuming a fixed header length.</summary>
    private static float[] WavSamples(byte[] wav)
    {
        Require(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wav.AsSpan(8, 4).SequenceEqual("WAVE"u8), "Invalid WAV container.");
        for (int offset = 12; offset + 8 <= wav.Length;)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            Require(length >= 0 && length <= wav.Length - offset - 8, "Invalid WAV chunk size.");
            if (wav.AsSpan(offset, 4).SequenceEqual("data"u8)) return PcmSamples(wav.AsSpan(offset + 8, length));
            offset += 8 + length + (length & 1);
        }
        throw new InvalidOperationException("WAV contains no PCM data.");
    }

    /// <summary>Decodes signed little-endian PCM16 for waveform comparisons.</summary>
    private static float[] PcmSamples(ReadOnlySpan<byte> pcm)
    {
        float[] samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768f;
        return samples;
    }

    /// <summary>Throws with the supplied diagnostic when a regression assertion fails.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Feeds a complete float waveform into the independent reference resampler.</summary>
    private sealed class FloatSource(float[] samples, int rate) : NAudio.Wave.ISampleProvider
    {
        private int _position;
        public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);

        /// <summary>Copies the next available samples without padding beyond the logical waveform.</summary>
        public int Read(float[] buffer, int offset, int count)
        {
            int length = Math.Min(count, samples.Length - _position);
            samples.AsSpan(_position, length).CopyTo(buffer.AsSpan(offset, length));
            _position += length;
            return length;
        }
    }
}
