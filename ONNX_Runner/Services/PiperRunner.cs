using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NAudio.Lame;
using ONNX_Runner.Models;
using System.Buffers;
using System.Numerics;

namespace ONNX_Runner.Services;

/// <summary>
/// The core engine responsible for executing the Piper ONNX model.
/// It handles hardware acceleration, tensor preparation, and the high-performance 
/// conversion of raw neural network output into playable audio formats.
/// </summary>
public class PiperRunner : IDisposable
{
    private readonly InferenceSession _session;
    private readonly IPhonemizer _phonemizer;
    private readonly PiperConfig _config;

    public bool IsUsingGPU { get; private set; }

    public PiperRunner(string modelPath, PiperConfig config, IPhonemizer phonemizer, OnnxSettings onnxSettings)
    {
        _phonemizer = phonemizer;
        _config = config;

        var (session, isGpu) = InitializeSession(modelPath, onnxSettings);
        _session = session;
        IsUsingGPU = isGpu;
    }

    /// <summary>
    /// Dynamically selects the best available hardware based on compile-time flags.
    /// Falls back to CPU if no compatible GPU is detected or if built as CPU-only.
    /// </summary>
    private static (InferenceSession, bool) InitializeSession(string modelPath, OnnxSettings onnxSettings)
    {
        // ====================================================================
        // GPU ACCELERATION BLOCK (Compiled ONLY if USE_CUDA or USE_DML is set)
        // ====================================================================
#if USE_CUDA || USE_DML
        int maxGpusToTry = 4;
        for (int deviceId = 0; deviceId < maxGpusToTry; deviceId++)
        {
            try
            {
                var gpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                onnxSettings.ApplyTo(gpuOptions); // Apply performance tuning from appsettings.json

#if USE_CUDA
                // CUDA (Linux / Docker with Nvidia Runtime)
                gpuOptions.AppendExecutionProvider_CUDA(deviceId);
                var session = new InferenceSession(modelPath, gpuOptions);
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[HARDWARE] Piper Model loaded successfully on GPU (CUDA, Device ID: {deviceId})");
                Console.ResetColor();
                return (session, true);
#elif USE_DML
                // DirectML (Windows)
                gpuOptions.AppendExecutionProvider_DML(deviceId);
                var session = new InferenceSession(modelPath, gpuOptions);
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[HARDWARE] Piper Model loaded successfully on GPU (DirectML, Device ID: {deviceId})");
                Console.ResetColor();
                return (session, true);
#endif
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[WARNING] Piper failed to load on GPU {deviceId}. Reason: {ex.Message}");
                Console.ResetColor();
            }
        }
        Console.WriteLine("[HARDWARE] GPU initialization failed or unavailable. Falling back to CPU.");

// ====================================================================
// CPU-ONLY BLOCK (Compiled if CpuOnly flag is used during build)
// ====================================================================
#else
        Console.WriteLine("[HARDWARE] Lightweight CPU-only build detected. Skipping GPU checks.");
#endif

        // FALLBACK / CPU EXECUTION
        var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
        onnxSettings.ApplyTo(cpuOptions);

        var fallbackSession = new InferenceSession(modelPath, cpuOptions);
        Console.WriteLine("[HARDWARE] Piper Model loaded successfully on CPU.");
        return (fallbackSession, false);
    }

    /// <summary>
    /// Performs raw inference. Converts phonemes into a float array of audio samples.
    /// Logic:
    /// 1. Maps speed to 'Length Scale' (Neural networks don't have a "speed" slider, they have a "duration" multiplier).
    /// 2. Prepares three input tensors: input (phonemes), input_lengths, and scales (noise parameters).
    /// 3. Executes the model and rents a buffer from ArrayPool to store the result.
    /// </summary>
    public (float[] Buffer, int Length) SynthesizeAudioRaw(string phonemes, float speed = 1.0f, float? requestNoiseScale = null, float? requestNoiseW = null)
    {
        float safeSpeed = Math.Clamp(speed, 0.1f, 10.0f);

        // ARCHITECTURAL LOGIC: LengthScale controls the duration of phonemes. 
        // A lower scale means shorter duration = faster speech.
        float targetLengthScale = _config.Inference.LengthScale / safeSpeed;

        float safeNoiseScale = requestNoiseScale ?? _config.Inference.NoiseScale;
        float safeNoiseW = requestNoiseW ?? _config.Inference.NoiseW;

        // The 'scales' tensor controls the 'robotic vs natural' variance and the speed.
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

        // ZERO-ALLOCATION: Rent a buffer instead of creating a new array to save GC cycles.
        float[] buffer = ArrayPool<float>.Shared.Rent(length);

        // Directly copy the memory block from the ONNX tensor to our rented array.
        if (outputTensor is DenseTensor<float> denseTensor)
        {
            denseTensor.Buffer.Span.CopyTo(buffer);
        }
        else
        {
            // Legacy fallback for non-dense tensors
            int index = 0;
            foreach (var val in outputTensor) buffer[index++] = val;
        }

        return (buffer, length);
    }

    /// <summary>
    /// A high-level wrapper that produces a standard WAV byte array.
    /// </summary>
    public byte[] SynthesizeAudio(string phonemes, float speed = 1.0f, float? requestNoiseScale = null, float? requestNoiseW = null)
    {
        var rawResult = SynthesizeAudioRaw(phonemes, speed, requestNoiseScale, requestNoiseW);
        try
        {
            // Convert the raw neural float samples (-1.0 to 1.0) into a standard WAV file.
            return ConvertToWav(rawResult.Buffer.AsSpan(0, rawResult.Length));
        }
        finally
        {
            // Always return the rented buffer to the pool after use.
            ArrayPool<float>.Shared.Return(rawResult.Buffer);
        }
    }

    /// <summary>
    /// Converts raw float samples to 16-bit PCM WAV data using SIMD (Single Instruction, Multiple Data).
    /// </summary>
    public byte[] ConvertToWav(ReadOnlySpan<float> audioSamples)
    {
        using var memoryStream = new MemoryStream();
        var waveFormat = new WaveFormat(_config.Audio.SampleRate, 16, 1);

        using (var writer = new WaveFileWriter(memoryStream, waveFormat))
        {
            // 16-bit audio requires 2 bytes per sample.
            int requiredBytes = audioSamples.Length * 2;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(requiredBytes);

            try
            {
                // --- HARDWARE ACCELERATION (SIMD) ---
                // Process 4 or 8 samples in a single CPU operation.
                int vectorSize = Vector<float>.Count;
                int i = 0;

                var minVec = new Vector<float>(-1f);
                var maxVec = new Vector<float>(1f);
                var multVec = new Vector<float>(32767f); // Multiplier for 16-bit range

                for (; i <= audioSamples.Length - vectorSize; i += vectorSize)
                {
                    var vSamples = new Vector<float>(audioSamples[i..]);

                    // Clamp values to [-1, 1] to prevent "clipping" artifacts (loud popping sounds)
                    var vClamped = Vector.Max(minVec, Vector.Min(maxVec, vSamples));
                    var vScaled = vClamped * multVec;

                    for (int k = 0; k < vectorSize; k++)
                    {
                        short shortSample = (short)vScaled[k];
                        int bufferIndex = (i + k) * 2;
                        // Manual byte-packing (Little Endian)
                        buffer[bufferIndex] = (byte)(shortSample & 0xFF);
                        buffer[bufferIndex + 1] = (byte)((shortSample >> 8) & 0xFF);
                    }
                }

                // Process the remaining samples (the "tail") that didn't fit into a SIMD vector.
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

    /// <summary>
    /// Encodes float samples into high-quality MP3 using the LAME encoder and SIMD scaling.
    /// </summary>
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
                // Identical SIMD logic to ConvertToWav to ensure maximum performance 
                // when converting floats to the shorts expected by the MP3 encoder.
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

    public void Dispose()
    {
        _session?.Dispose();
        GC.SuppressFinalize(this);
    }
}