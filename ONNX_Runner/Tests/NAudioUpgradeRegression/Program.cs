using System.Buffers;
using System.Text.Json;
using NAudio.Dsp;
using NAudio.Wave;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

int passed = 0, failed = 0;
var native = NativeAudioDependencies.Detect();
SampleRateChecks.Run(Check, native);

foreach (int size in new[] { 64, 512, 1024, 4096 })
{
    Check($"FFT {size} preserves normalization and matches an independent DFT", () =>
    {
        var random = new Random(size);
        float[] input = Enumerable.Range(0, size).Select(_ => (float)(random.NextDouble() - .5)).ToArray();
        Complex[] spectrum = input.Select(x => new Complex { X = x }).ToArray();
        FastFourierTransform.FFT(true, int.Log2(size), spectrum);
        foreach (int k in new[] { 0, 1, size / 4, size / 2 - 1, size / 2 })
        {
            double re = 0, im = 0;
            for (int n = 0; n < size; n++)
            {
                double phase = -2 * Math.PI * k * n / size;
                re += input[n] * Math.Cos(phase) / size;
                im += input[n] * Math.Sin(phase) / size;
            }
            Require(Math.Abs(spectrum[k].X - re) < 1e-6 && Math.Abs(spectrum[k].Y - im) < 1e-6,
                "FFT scaling or high-frequency precision changed.");
        }
        FastFourierTransform.FFT(false, int.Log2(size), spectrum);
        Require(Enumerable.Range(0, size).All(i => Math.Abs(spectrum[i].X - input[i]) < 2e-6),
            "FFT roundtrip changed the time-domain samples.");
    });
}

Check("OpenVoice spectrogram layouts retain identical values and magnitude scaling", () =>
{
    var processor = new AudioProcessor(new ToneConfig
    { Data = new ToneDataConfig { FilterLength = 1024, HopLength = 256, SamplingRate = 22050 } });
    float[] input = Enumerable.Range(0, 5000).Select(i => .2f * MathF.Sin(2 * MathF.PI * 700 * i / 22050)).ToArray();
    float[,] standard = processor.GetMagnitudeSpectrogram(input);
    var transposed = processor.GetColorizerSpectrogram(input);
    try
    {
        Require(transposed.Buffer != null && standard.GetLength(0) == transposed.Frames &&
            standard.GetLength(1) == transposed.Bins, "OpenVoice tensor dimensions changed.");
        for (int f = 0; f < transposed.Frames; f++)
        for (int k = 0; k < transposed.Bins; k++)
            Require(standard[f, k] == transposed.Buffer![k * transposed.Frames + f], "OpenVoice layouts disagree.");
        float[,] silence = processor.GetMagnitudeSpectrogram(new float[5000]);
        Require(Math.Abs(silence[0, 0] - .001f) < 1e-9, "OpenVoice magnitude epsilon was rescaled.");
    }
    finally { if (transposed.Buffer != null) ArrayPool<float>.Shared.Return(transposed.Buffer); }
});

Check("WAV output finalizes its header and preserves a caller-owned stream", () =>
{
    using var stream = new MemoryStream();
    float[] input = [.1f, -.2f, .3f, -.4f, .5f];
    using (var manager = new AudioStreamManager(AudioFormat.Wav, 22050, stream)) manager.WriteChunk(input);
    Require(stream.CanWrite, "WAV finalization closed the caller-owned stream.");
    stream.Position = 0;
    using var reader = new WaveFileReader(stream);
    Require(reader.WaveFormat.SampleRate == 22050 && reader.WaveFormat.Channels == 1 &&
        reader.WaveFormat.BitsPerSample == 16 && reader.Length == input.Length * 2,
        "WAV output header or valid PCM duration changed.");
    var provider = reader.ToSampleProvider();
    var decoded = new float[input.Length];
    Require(provider.Read(decoded.AsSpan()) == input.Length, "WAV roundtrip lost samples.");
    for (int i = 0; i < input.Length; i++)
        Require(Math.Abs(decoded[i] - input[i]) <= 2f / 32768, "WAV PCM values changed beyond the existing quantization bound.");
});

foreach (int channels in new[] { 1, 2 })
{
    Check($"Reference WAV load/downmix/resample preserves {channels}-channel content", () =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"tsubaki-naudio-{Guid.NewGuid():N}.wav");
        try
        {
            float[] input = Enumerable.Range(0, 22087).Select(i => .2f * MathF.Sin(2 * MathF.PI * 700 * i / 22050)).ToArray();
            using (var writer = new WaveFileWriter(path, new WaveFormat(22050, 16, channels)))
                foreach (float x in input)
                    for (int c = 0; c < channels; c++) writer.WriteSample(x);
            var processor = new AudioProcessor(new ToneConfig
            { Data = new ToneDataConfig { FilterLength = 1024, HopLength = 256 } });
            var loaded = processor.LoadAndNormalizeWav(path, 16000);
            try
            {
                Require(Math.Abs(loaded.Length - input.Length * 16000.0 / 22050) <= 1,
                    "Reference audio duration changed during rate conversion.");
                var expected = new AudioResampler(22050, 16000).Resample(input, input.Length);
                try
                {
                    Require(expected.Length == loaded.Length, "Reference WAV resampling length differs from direct PCM.");
                    double error = 0;
                    for (int i = 0; i < loaded.Length; i++)
                        error = Math.Max(error, Math.Abs(loaded.Buffer[i] - expected.Buffer[i]));
                    Require(error < 2f / 32768, "Reference WAV loading changed content beyond PCM quantization.");
                }
                finally { ArrayPool<float>.Shared.Return(expected.Buffer); }
            }
            finally { ArrayPool<float>.Shared.Return(loaded.Buffer); }
        }
        finally { File.Delete(path); }
    });
}

Console.WriteLine($"NAudio upgrade: {passed}/{passed + failed} checks passed.");
if (args.Length == 2 && args[0] == "--report")
    File.WriteAllText(args[1], JsonSerializer.Serialize(new { Passed = passed, Failed = failed,
        Mp3Tested = native.Mp3Available, Note = "Managed DSP, rate conversion, reference WAV loading and output encoding; no ONNX model inference." },
        new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;

void Check(string name, Action action)
{
    try { action(); passed++; }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
