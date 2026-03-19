using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using ONNX_Runner.Models;
using System.Buffers;

namespace ONNX_Runner.Services;

public class PiperRunner : IDisposable
{
    private readonly InferenceSession _session;
    private readonly IPhonemizer _phonemizer;
    private readonly PiperConfig _config;

    public bool IsUsingGPU { get; private set; }

    public PiperRunner(string modelPath, PiperConfig config, IPhonemizer phonemizer)
    {
        _phonemizer = phonemizer;
        _config = config;

        var (session, isGpu) = InitializeSession(modelPath);
        _session = session;
        IsUsingGPU = isGpu;
    }

    private static (InferenceSession, bool) InitializeSession(string modelPath)
    {
        int maxGpusToTry = 4;
        for (int deviceId = 0; deviceId < maxGpusToTry; deviceId++)
        {
            try
            {
                var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
                options.AppendExecutionProvider_DML(deviceId);
                var session = new InferenceSession(modelPath, options);
                Console.WriteLine($"[HARDWARE] Piper Model loaded successfully on GPU (DirectML, Device ID: {deviceId})");
                return (session, true);
            }
            catch { }
        }

        var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
        var fallbackSession = new InferenceSession(modelPath, cpuOptions);
        Console.WriteLine("[HARDWARE] Piper Model loaded successfully on CPU.");
        return (fallbackSession, false);
    }

    public float[] SynthesizeAudioRaw(string phonemes, float speed = 1.0f, float? requestNoiseScale = null, float? requestNoiseW = null)
    {
        float safeSpeed = Math.Clamp(speed, 0.1f, 10.0f);
        float targetLengthScale = _config.Inference.LengthScale / safeSpeed;
        float safeNoiseScale = requestNoiseScale ?? _config.Inference.NoiseScale;
        float safeNoiseW = requestNoiseW ?? _config.Inference.NoiseW;

        var scalesTensor = new DenseTensor<float>(new float[] { safeNoiseScale, targetLengthScale, safeNoiseW }, [3]);

        long[] phonemeIds = _phonemizer.PhonemesToIds(phonemes);
        var inputTensor = new DenseTensor<long>(phonemeIds, [1, phonemeIds.Length]);
        var inputLengthsTensor = new DenseTensor<long>(new[] { (long)phonemeIds.Length }, [1]);

        var inputs = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("input", inputTensor),
        NamedOnnxValue.CreateFromTensor("input_lengths", inputLengthsTensor),
        NamedOnnxValue.CreateFromTensor("scales", scalesTensor)
    };

        using var results = _session.Run(inputs);
        return results.First(r => r.Name == "output").AsEnumerable<float>().ToArray();
    }

    public byte[] SynthesizeAudio(string phonemes, float speed = 1.0f, float? requestNoiseScale = null, float? requestNoiseW = null)
    {
        float[] rawSamples = SynthesizeAudioRaw(phonemes, speed, requestNoiseScale, requestNoiseW);
        return ConvertToWav(rawSamples);
    }

    public byte[] ConvertToWav(float[] audioSamples)
    {
        using var memoryStream = new MemoryStream();
        var waveFormat = new WaveFormat(_config.Audio.SampleRate, 16, 1);

        using (var writer = new WaveFileWriter(memoryStream, waveFormat))
        {
            int requiredBytes = audioSamples.Length * 2;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(requiredBytes);

            try
            {
                for (int i = 0; i < audioSamples.Length; i++)
                {
                    float sample = Math.Clamp(audioSamples[i], -1f, 1f) * 32767f;
                    short shortSample = (short)sample;
                    buffer[i * 2] = (byte)(shortSample & 0xFF);
                    buffer[i * 2 + 1] = (byte)((shortSample >> 8) & 0xFF);
                }
                writer.Write(buffer, 0, requiredBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return memoryStream.ToArray();
    }

    public void Dispose()
    {
        _session?.Dispose();
        GC.SuppressFinalize(this);
    }
}