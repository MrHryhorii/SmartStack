using System.Buffers.Binary;
using System.Text.Json;
using NAudio.Wave;
using Microsoft.Extensions.Configuration;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models;
using ONNX_Runner.Services;
using ONNX_Runner.Services.Synthesis;

internal static class SampleRateChecks
{
    internal static void Run(Action<string, Action> check, NativeAudioDependencies native)
    {
        foreach (var (value, expected) in new (int?, int?)[]
        {
            (null, null), (0, 0), (int.MinValue, 8000), (-1, 8000), (1, 8000), (7999, 8000),
            (8000, 8000), (12345, 12345), (48000, 48000), (48001, 48000), (int.MaxValue, 48000)
        })
        {
            if (value.HasValue)
            {
                check($"Server samplerate configuration clamps {value}", () =>
                {
                    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["DspSettings:DefaultSampleRate"] = value.Value.ToString()
                    }).Build();
                    var settings = configuration.GetSection("DspSettings").Get<DspSettings>()!;
                    Require(settings.DefaultSampleRate == expected &&
                        new DspSettings { DefaultSampleRate = value.Value }.DefaultSampleRate == expected,
                        "Server binding and direct settings use different rate limits.");
                });
            }
            check($"Samplerate JSON and adapter preserve the contract for {value?.ToString() ?? "null"}", () =>
            {
                string json = JsonSerializer.Serialize(new { input = "The file is ready.", samplerate = value });
                var dto = JsonSerializer.Deserialize<TsubakiSpeechRequest>(json, JsonSerializerOptions.Web)!;
                var (request, error) = TsubakiRequestAdapter.ToSynthesisRequest(dto);
                Require(error == null && dto.SampleRate == expected && request?.SampleRate == expected,
                    "The wire value was not clamped and forwarded correctly.");
                Require(new SynthesisRequest { Input = "Test.", Voice = "piper_base", Speed = 1,
                    Format = AudioFormat.Pcm, SampleRate = value }.SampleRate == expected,
                    "Direct synthesis requests bypassed the sample-rate limits.");
            });
        }
        check("An omitted server samplerate preserves the zero default", () =>
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DspSettings:DefaultVolume"] = "0.5"
            }).Build();
            Require(new DspSettings().DefaultSampleRate == 0 &&
                configuration.GetSection("DspSettings").Get<DspSettings>()!.DefaultSampleRate == 0,
                "Missing configuration enabled output resampling.");
        });
        check("Omitted samplerate retains the old request defaults", () =>
        {
            var dto = JsonSerializer.Deserialize<TsubakiSpeechRequest>("{\"input\":\"Test.\"}")!;
            var (request, error) = TsubakiRequestAdapter.ToSynthesisRequest(dto);
            Require(error == null && request?.SampleRate == null, "An omitted rate forced output conversion.");
        });
        check("OpenAI requests retain their existing compatibility surface", () =>
        {
            var dto = JsonSerializer.Deserialize<OpenAiSpeechRequest>("{\"input\":\"Test.\",\"samplerate\":8000}")!;
            Require(OpenAiRequestAdapter.ToSynthesisRequest(dto).Request?.SampleRate == null,
                "A Tsubaki-only field changed the OpenAI endpoint.");
        });
        foreach (string value in new[] { "\"16000\"", "16000.5", "true", "[]", "{}", "2147483648" })
        {
            check($"Samplerate rejects a non-integer JSON value {value}", () =>
            {
                try { JsonSerializer.Deserialize<TsubakiSpeechRequest>($"{{\"input\":\"Test.\",\"samplerate\":{value}}}", JsonSerializerOptions.Web); }
                catch (JsonException) { return; }
                throw new InvalidOperationException("An invalid sample-rate value was accepted.");
            });
        }

        foreach (AudioFormat format in Enum.GetValues<AudioFormat>())
        {
            check($"Encoder rate selection for {format}", () =>
            {
                int expected = format is AudioFormat.Mp3 or AudioFormat.B64Json or AudioFormat.Aac or AudioFormat.Opus ? 12000 : 12345;
                Require(AudioSampleRate.Resolve(12345, format) == expected, "Nonstandard rate was resolved incorrectly.");
                Require(AudioSampleRate.Resolve(8000, format) == 8000 && AudioSampleRate.Resolve(48000, format) == 48000,
                    "A supported endpoint rate was changed.");
            });
        }
        check("Nearest-rate ties consistently choose the lower supported rate", () =>
        {
            Require(AudioSampleRate.Resolve(14000, AudioFormat.Opus) == 12000 &&
                AudioSampleRate.Resolve(46050, AudioFormat.Mp3) == 44100 &&
                AudioSampleRate.Resolve(23025, AudioFormat.Aac) == 22050, "Rate ties changed across codecs.");
        });

        foreach (int sourceRate in new[] { 8000, 16000, 22050, 24000, 48000 })
        foreach (int targetRate in new[] { 8000, 11025, 12345, 16000, 22050, 24000, 44100, 48000 })
        {
            check($"Incremental resampling preserves duration and chunk independence: {sourceRate}->{targetRate}", () =>
            {
                float[] input = Tone(sourceRate + 37, sourceRate, 700);
                float[] whole = Resample(input, sourceRate, targetRate, [input.Length]);
                float[] split = Resample(input, sourceRate, targetRate, [1, 7, 97, 641]);
                Require(whole.AsSpan().SequenceEqual(split), "Input partitions changed filter history or interpolation phase.");
                double expected = input.Length * (double)targetRate / sourceRate;
                Require(Math.Abs(split.Length - expected) <= 1, $"Duration drifted: {split.Length} output samples, expected {expected:F3}.");
                Require(split.All(float.IsFinite), "Resampling produced non-finite samples.");
                Require(split.Length > 0 && split.Max(MathF.Abs) > .1f, "Output audio disappeared.");
                foreach (int length in new[] { 0, 1, 2, 7, 159, 160, 161, 219, 220, 221, 479, 480, 481 })
                {
                    float[] shortInput = Tone(length, sourceRate, 700);
                    float[] shortWhole = Resample(shortInput, sourceRate, targetRate, [Math.Max(1, length)]);
                    float[] shortSplit = Resample(shortInput, sourceRate, targetRate, [1, 7, 37]);
                    Require(shortWhole.AsSpan().SequenceEqual(shortSplit), "A short stream changed at an input boundary.");
                    Require(Math.Abs(shortSplit.Length - length * (double)targetRate / sourceRate) <= 1,
                        $"Finalizing {length} input samples changed their duration.");
                }
            });
        }
        check("Downsampling preserves speech-band pitch and suppresses aliases", () =>
        {
            float[] low = Resample(Tone(22050, 22050, 1000), 22050, 8000, [37, 503]);
            float[] high = Resample(Tone(22050, 22050, 6000), 22050, 8000, [37, 503]);
            Require(ToneAmplitude(low, 8000, 1000) > .15, "A 1 kHz tone was shifted or excessively attenuated.");
            Require(Rms(high.AsSpan(400, high.Length - 800)) < .05 * Rms(low.AsSpan(400, low.Length - 800)),
                "High-frequency input folded into the speech band without sufficient attenuation.");
        });
        check("Incremental resampling drains once and rejects writes after completion", () =>
        {
            var resampler = new AudioResampler(22050, 48000);
            var output = new float[480];
            Require(resampler.Complete(output, output.Length) == 0 && resampler.Complete(output, output.Length) == 0,
                "An empty stream produced PCM or completion was not idempotent.");
            try { resampler.Process([.1f], output, output.Length); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("PCM was accepted after completion.");
        });
        check("Steady-state incremental resampling adds no managed allocations", () =>
        {
            var resampler = new AudioResampler(22050, 16000);
            var input = new float[1024];
            var output = new float[160];
            void Feed()
            {
                int offset = 0;
                while (offset < input.Length) offset += resampler.Process(input.AsSpan(offset), output, output.Length).Consumed;
            }
            for (int i = 0; i < 100; i++) Feed();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) Feed();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated == 0, $"The warmed resampler allocated {allocated} bytes.");
        });

        foreach (AudioFormat format in Enum.GetValues<AudioFormat>())
        foreach (int requested in new[] { 8000, 12345, 16000, 44100, 48000 })
        {
            if (format is AudioFormat.Mp3 or AudioFormat.B64Json && !native.Mp3Available) continue;
            check($"Encoded {format} declares the effective requested rate for {requested} Hz", () =>
            {
                int rate = AudioSampleRate.Resolve(requested, format);
                using var stream = new MemoryStream();
                using (var manager = new AudioStreamManager(format, rate, stream, enforceSampleRate: true))
                using (var resampler = new AudioOutputResampler(22050, rate))
                {
                    resampler.Write(Tone(2205, 22050, 700), manager, null);
                    resampler.Complete(manager);
                    Require(Math.Abs(manager.SamplesWritten - rate / 10.0) <= 1, "Encoder received the wrong PCM duration.");
                }
                byte[] data = stream.ToArray();
                bool correctRate = format == AudioFormat.Pcm
                    ? Math.Abs(data.Length / 2.0 - rate / 10.0) <= 1
                    : ReadEncodedRate(data, format) == rate;
                Require(data.Length > 0 && correctRate,
                    "The encoded header does not match the effective rate.");
            });
        }
    }

    internal static float[] Resample(float[] input, int sourceRate, int targetRate, int[] partitions)
    {
        var resampler = new AudioResampler(sourceRate, targetRate);
        var output = new float[Math.Max(1, targetRate / 100)];
        var result = new List<float>((int)Math.Ceiling(input.Length * (double)targetRate / sourceRate) + 16);
        int offset = 0, partition = 0;
        while (offset < input.Length)
        {
            int remaining = Math.Min(partitions[partition++ % partitions.Length], input.Length - offset);
            while (remaining > 0)
            {
                var block = resampler.Process(input.AsSpan(offset, remaining), output, output.Length);
                Require(block.Consumed > 0, "Incremental resampling stopped consuming input.");
                for (int i = 0; i < block.Written; i++) result.Add(output[i]);
                offset += block.Consumed;
                remaining -= block.Consumed;
            }
        }
        int written, flushes = 0;
        do
        {
            written = resampler.Complete(output, output.Length);
            for (int i = 0; i < written; i++) result.Add(output[i]);
            Require(++flushes < 100, "Resampling did not finish draining.");
        } while (written == output.Length);
        Require(resampler.Complete(output, output.Length) == 0, "Drained output was repeated.");
        return result.ToArray();
    }

    internal static int ReadEncodedRate(byte[] data, AudioFormat format)
    {
        if (format == AudioFormat.Pcm) return data.Length > 0 ? -1 : 0;
        if (format == AudioFormat.Wav) return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24, 4));
        if (format == AudioFormat.Flac) return (int)(BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(18, 8)) >> 44);
        if (format == AudioFormat.Aac)
        {
            int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000];
            Require(data[0] == 0xff && (data[1] & 0xf6) == 0xf0, "AAC does not contain a valid ADTS header.");
            return rates[(data[2] >> 2) & 15];
        }
        if (format == AudioFormat.Opus)
        {
            int header = data.AsSpan().IndexOf("OpusHead"u8);
            Require(header >= 0, "Opus identification header is missing.");
            return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(header + 12, 4));
        }
        using var stream = new MemoryStream(data);
        return Mp3Frame.LoadFromStream(stream)?.SampleRate ?? throw new InvalidOperationException("MP3 frame header is missing.");
    }

    internal static float[] Tone(int length, int rate, double frequency)
        => Enumerable.Range(0, length).Select(i => (float)(.2 * Math.Sin(2 * Math.PI * frequency * i / rate))).ToArray();

    private static double Rms(ReadOnlySpan<float> data)
    {
        double sum = 0;
        foreach (float sample in data) sum += sample * sample;
        return Math.Sqrt(sum / data.Length);
    }

    private static double ToneAmplitude(float[] data, int rate, double frequency)
    {
        double sine = 0, cosine = 0;
        int start = rate / 20, end = data.Length - start;
        for (int i = start; i < end; i++)
        {
            double phase = 2 * Math.PI * frequency * i / rate;
            sine += data[i] * Math.Sin(phase);
            cosine += data[i] * Math.Cos(phase);
        }
        return 2 * Math.Sqrt(sine * sine + cosine * cosine) / (end - start);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
