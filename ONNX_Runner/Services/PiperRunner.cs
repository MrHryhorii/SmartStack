using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NAudio.Lame;
using ONNX_Runner.Models;
using System.Buffers;
using System.Numerics;

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
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[WARNING] Piper failed to load on GPU {deviceId}. Reason: {ex.Message}");
                Console.ResetColor();
            }
        }

        var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
        var fallbackSession = new InferenceSession(modelPath, cpuOptions);
        Console.WriteLine("[HARDWARE] Piper Model loaded successfully on CPU.");
        return (fallbackSession, false);
    }

    public (float[] Buffer, int Length) SynthesizeAudioRaw(string phonemes, float speed = 1.0f, float? requestNoiseScale = null, float? requestNoiseW = null)
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
        var outputNode = results.First(r => r.Name == "output");
        var outputTensor = outputNode.AsTensor<float>();

        int length = (int)outputTensor.Length;

        // ОРЕНДУЄМО МАСИВ
        float[] buffer = ArrayPool<float>.Shared.Rent(length);

        // Швидке копіювання з тензора напряму в орендований масив
        if (outputTensor is DenseTensor<float> denseTensor)
        {
            denseTensor.Buffer.Span.CopyTo(buffer);
        }
        else
        {
            // Фолбек для старих версій ONNX
            int index = 0;
            foreach (var val in outputTensor) buffer[index++] = val;
        }

        return (buffer, length);
    }

    public byte[] SynthesizeAudio(string phonemes, float speed = 1.0f, float? requestNoiseScale = null, float? requestNoiseW = null)
    {
        var rawResult = SynthesizeAudioRaw(phonemes, speed, requestNoiseScale, requestNoiseW);
        try
        {
            // Передаємо лише корисну частину масиву
            return ConvertToWav(rawResult.Buffer.AsSpan(0, rawResult.Length));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rawResult.Buffer);
        }
    }

    public byte[] ConvertToWav(ReadOnlySpan<float> audioSamples)
    {
        using var memoryStream = new MemoryStream();
        var waveFormat = new WaveFormat(_config.Audio.SampleRate, 16, 1);

        using (var writer = new WaveFileWriter(memoryStream, waveFormat))
        {
            int requiredBytes = audioSamples.Length * 2;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(requiredBytes);

            try
            {
                int vectorSize = Vector<float>.Count;
                int i = 0;

                var minVec = new Vector<float>(-1f);
                var maxVec = new Vector<float>(1f);
                var multVec = new Vector<float>(32767f);

                for (; i <= audioSamples.Length - vectorSize; i += vectorSize)
                {
                    var vSamples = new Vector<float>(audioSamples[i..]);
                    var vClamped = Vector.Max(minVec, Vector.Min(maxVec, vSamples));
                    var vScaled = vClamped * multVec;

                    for (int k = 0; k < vectorSize; k++)
                    {
                        short shortSample = (short)vScaled[k];
                        int bufferIndex = (i + k) * 2;
                        buffer[bufferIndex] = (byte)(shortSample & 0xFF);
                        buffer[bufferIndex + 1] = (byte)((shortSample >> 8) & 0xFF);
                    }
                }

                for (; i < audioSamples.Length; i++)
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

    public byte[] ConvertToMp3(ReadOnlySpan<float> audioSamples, int sampleRate)
    {
        using var memoryStream = new MemoryStream();
        var waveFormat = new WaveFormat(sampleRate, 16, 1);

        using (var writer = new LameMP3FileWriter(memoryStream, waveFormat, LAMEPreset.VBR_90))
        {
            int requiredBytes = audioSamples.Length * 2;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(requiredBytes);

            try
            {
                // --- SIMD ОПТИМІЗАЦІЯ ---
                int vectorSize = Vector<float>.Count;
                int i = 0;

                var minVec = new Vector<float>(-1f);
                var maxVec = new Vector<float>(1f);
                var multVec = new Vector<float>(32767f);

                for (; i <= audioSamples.Length - vectorSize; i += vectorSize)
                {
                    var vSamples = new Vector<float>(audioSamples[i..]);

                    var vClamped = Vector.Max(minVec, Vector.Min(maxVec, vSamples));
                    var vScaled = vClamped * multVec;

                    for (int k = 0; k < vectorSize; k++)
                    {
                        short shortSample = (short)vScaled[k];
                        int bufferIndex = (i + k) * 2;
                        buffer[bufferIndex] = (byte)(shortSample & 0xFF);
                        buffer[bufferIndex + 1] = (byte)((shortSample >> 8) & 0xFF);
                    }
                }

                // Хвіст
                for (; i < audioSamples.Length; i++)
                {
                    float sample = Math.Clamp(audioSamples[i], -1f, 1f) * 32767f;
                    short shortSample = (short)sample;
                    buffer[i * 2] = (byte)(shortSample & 0xFF);
                    buffer[i * 2 + 1] = (byte)((shortSample >> 8) & 0xFF);
                }
                // -------------------------

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