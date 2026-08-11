using System.Buffers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Handles the execution of OpenVoice AI models for Voice Cloning.
/// It manages two distinct ONNX sessions:
/// 1. Tone Extractor: Extracts unique vocal characteristics (embeddings) from audio.
/// 2. Tone Color Converter: Applies a source tone color to a target voice using Latent Space Blending.
/// </summary>
public partial class OpenVoiceRunner : IDisposable
{
    // The Extractor session is nullable because it can be unloaded from memory after 
    // the initial startup processing to save precious VRAM/RAM.
    private InferenceSession? _extractSession;
    private readonly InferenceSession _colorSession;
    private readonly ToneConfig _config;
    private readonly ILogger<OpenVoiceRunner> _logger;

    // A dictionary acting as a 'Voice Library': 
    // Key - Voice name (e.g., "MorganFreeman"), Value - Tonal fingerprint (256-float embedding).
    public Dictionary<string, float[]> VoiceLibrary { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int GetTargetSamplingRate() => _config.Data.SamplingRate;

    public OpenVoiceRunner(string extractPath, string colorPath, ToneConfig config, OnnxSettings onnxSettings, ILogger<OpenVoiceRunner> logger)
    {
        _config = config;
        _logger = logger;
        (_extractSession, _colorSession) = InitializeSessions(extractPath, colorPath, onnxSettings, logger);
        PrintModelMetadata();
    }

    /// <summary>
    /// Dynamically selects the best available hardware based on compile-time flags.
    /// Falls back to CPU if no compatible GPU is detected or if built as CPU-only.
    /// </summary>
    private static (InferenceSession, InferenceSession) InitializeSessions(string extractPath, string colorPath, OnnxSettings onnxSettings, ILogger<OpenVoiceRunner> logger)
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
                var gpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
                {
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                    GraphOptimizationLevel = onnxSettings.EnableGraphOptimization 
                        ? GraphOptimizationLevel.ORT_ENABLE_ALL 
                        : GraphOptimizationLevel.ORT_DISABLE_ALL
                };
                
                // We apply a profile specifically for the GPU
                onnxSettings.Gpu.ApplyTo(gpuOptions);

#if USE_CUDA
                // CUDA (Linux / Docker with Nvidia Runtime)
                gpuOptions.AppendExecutionProvider_CUDA(deviceId);
                var extract = new InferenceSession(extractPath, gpuOptions);
                var color = new InferenceSession(colorPath, gpuOptions);

                LogCudaLoaded(logger, deviceId);
                return (extract, color);
#elif USE_DML
                // DirectML (Windows)
                gpuOptions.AppendExecutionProvider_DML(deviceId);
                var extract = new InferenceSession(extractPath, gpuOptions);
                var color = new InferenceSession(colorPath, gpuOptions);

                LogDmlLoaded(logger, deviceId);
                return (extract, color);
#endif
            }
            catch (Exception ex)
            {
                LogGpuInitFailed(logger, ex, deviceId);
            }
        }
        logger.LogInformation("[HARDWARE] GPU initialization failed or unavailable. Falling back to CPU.");

        // ====================================================================
        // CPU-ONLY BLOCK (Compiled if CpuOnly flag is used during build)
        // ====================================================================
#else
        logger.LogInformation("[HARDWARE] Lightweight CPU-only build detected. Skipping GPU checks.");
#endif

        // FALLBACK / CPU EXECUTION
        var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            GraphOptimizationLevel = onnxSettings.EnableGraphOptimization 
                ? GraphOptimizationLevel.ORT_ENABLE_ALL 
                : GraphOptimizationLevel.ORT_DISABLE_ALL
        };
        
        // We apply a profile specifically for the CPU
        onnxSettings.Cpu.ApplyTo(cpuOptions);

        var cpuExtract = new InferenceSession(extractPath, cpuOptions);
        var cpuColor = new InferenceSession(colorPath, cpuOptions);

        logger.LogInformation("[HARDWARE] OpenVoice Models loaded on CPU.");
        return (cpuExtract, cpuColor);
    }

    // --- EMBEDDING EXTRACTION & FINGERPRINT MANAGEMENT ---

    /// <summary>
    /// Extracts a 256-dimensional tone embedding (fingerprint) from a provided audio spectrogram.
    /// This acts as the mathematical 'DNA' of a specific voice.
    /// </summary>
    public float[] ExtractToneColor(float[,] spectrogram)
    {
        if (_extractSession == null)
            throw new InvalidOperationException("Tone Extractor has been unloaded from memory.");

        int frames = spectrogram.GetLength(0);
        int bins = spectrogram.GetLength(1); // Expected to be 513 for standard STFT
        int tensorSize = frames * bins;

        // ZERO-ALLOCATION PATTERN: Rent memory from a shared pool to avoid GC pressure
        float[] rentedInput = ArrayPool<float>.Shared.Rent(tensorSize);
        try
        {
            // Map the rented array to a Tensor without copying data
            var memory = new Memory<float>(rentedInput, 0, tensorSize);
            var inputTensor = new DenseTensor<float>(memory, [1, frames, bins]);

            for (int i = 0; i < frames; i++)
            {
                for (int j = 0; j < bins; j++)
                {
                    inputTensor[0, i, j] = spectrogram[i, j];
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor)
            };

            using var results = _extractSession.Run(inputs);
            // Resulting embedding is small (256 floats ~ 1KB)
            return [.. results.First(r => r.Name == "tone_embedding").AsEnumerable<float>()];
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedInput);
        }
    }

    public void SaveVoiceFingerprint(string path, float[] embedding)
    {
        byte[] result = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, result, 0, result.Length);
        File.WriteAllBytes(path, result);
    }

    public float[] LoadVoiceFingerprint(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        float[] embedding = new float[data.Length / sizeof(float)];
        Buffer.BlockCopy(data, 0, embedding, 0, data.Length);
        return embedding;
    }

    /// <summary>
    /// Frees the Tone Extractor model from memory. 
    /// This is a memory-saving optimization since extraction is typically only needed once at startup.
    /// </summary>
    public void UnloadExtractor()
    {
        _extractSession?.Dispose();
        _extractSession = null;
        _logger.LogInformation("[INFO] OpenVoice Tone Extractor has been unloaded to free up system resources.");
    }

    // --- MODEL INSPECTION & DSP ---

    private void PrintModelMetadata()
    {
        _logger.LogInformation("=========================================");
        _logger.LogInformation("       OPENVOICE MODELS INSPECTION       ");
        _logger.LogInformation("=========================================");

        _logger.LogInformation(">>> CONFIG SETTINGS:");
        LogSampleRate(_config.Data.SamplingRate);
        LogFilterLength(_config.Data.FilterLength);
        LogHopLength(_config.Data.HopLength);
        LogGinChannels(_config.Model.GinChannels);

        if (_extractSession != null) InspectSession("TONE EXTRACTOR", _extractSession);
        InspectSession("TONE COLOR CONVERTER", _colorSession);
    }

    private void InspectSession(string name, InferenceSession session)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;

        LogSessionName(name);

        _logger.LogInformation("  Inputs:");
        foreach (var input in session.InputMetadata)
        {
            var shape = string.Join(" x ", input.Value.Dimensions.Select(d => d == -1 ? "Batch" : d.ToString()));
            LogTensorMetadata(input.Key, shape, input.Value.ElementType);
        }

        _logger.LogInformation("  Outputs:");
        foreach (var output in session.OutputMetadata)
        {
            var shape = string.Join(" x ", output.Value.Dimensions.Select(d => d == -1 ? "Batch" : d.ToString()));
            LogTensorMetadata(output.Key, shape, output.Value.ElementType);
        }
    }

    /// <summary>
    /// Applies the destination tone color to a source audio spectrogram.
    /// This is the core logic of Voice Cloning.
    /// </summary>
    public (float[] Buffer, int Length) ApplyToneColor(float[,] spectrogram, float[] srcFingerprint, float[] destFingerprint, float tau = 1.0f)
    {
        int frames = spectrogram.GetLength(0);
        int bins = spectrogram.GetLength(1);
        int channels = _config.Model.GinChannels;
        int tensorSize = frames * bins;

        // Rent memory for input audio tensor
        float[] rentedInput = ArrayPool<float>.Shared.Rent(tensorSize);
        try
        {
            var memory = new Memory<float>(rentedInput, 0, tensorSize);
            var audioTensor = new DenseTensor<float>(memory, [1, bins, frames]);

            for (int i = 0; i < frames; i++)
            {
                for (int j = 0; j < bins; j++)
                {
                    audioTensor[0, j, i] = spectrogram[i, j];
                }
            }

            // Prepare voice fingerprints and parameters for ONNX inference
            var srcTensor = new DenseTensor<float>(srcFingerprint, [1, channels, 1]);
            var destTensor = new DenseTensor<float>(destFingerprint, [1, channels, 1]);
            var lengthTensor = new DenseTensor<long>(new[] { (long)frames }, [1]);
            var tauTensor = new DenseTensor<float>(new[] { tau }, [1]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("audio", audioTensor),
                NamedOnnxValue.CreateFromTensor("audio_length", lengthTensor),
                NamedOnnxValue.CreateFromTensor("src_tone", srcTensor),
                NamedOnnxValue.CreateFromTensor("dest_tone", destTensor),
                NamedOnnxValue.CreateFromTensor("tau", tauTensor)
            };

            using var results = _colorSession.Run(inputs);

            // Extract the converted audio result into a rented buffer
            var outputNode = results.First(r => r.Name == "converted_audio");
            var outputTensor = outputNode.AsTensor<float>();

            int outLength = (int)outputTensor.Length;
            float[] outBuffer = ArrayPool<float>.Shared.Rent(outLength);

            if (outputTensor is DenseTensor<float> denseTensor)
            {
                denseTensor.Buffer.Span.CopyTo(outBuffer);
            }
            else
            {
                int index = 0;
                foreach (var val in outputTensor) outBuffer[index++] = val;
            }

            return (outBuffer, outLength);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedInput);
        }
    }

    /// <summary>
    /// Performs a dummy inference pass through the Tone Color Converter to trigger JIT compilation and graph optimization.
    /// </summary>
    public void WarmUpColorConverter()
    {
        try
        {
            _logger.LogInformation("[AUTO-BASE] Warming up OpenVoice Color Converter (Cold Start Prevention)...");
            // Create dummy inputs that match the expected dimensions of the model to trigger JIT compilation and graph optimization.
            int frames = 300;
            int bins = (_config.Data.FilterLength / 2) + 1;
            int channels = _config.Model.GinChannels;
            // Use a fixed seed for reproducibility in dummy data generation
            var rng = new Random(42);
            // Generate a dummy spectrogram with small random values to simulate real input
            //  without causing extreme activations in the model.
            var dummySpectrogram = new float[frames, bins];
            for (int i = 0; i < frames; i++)
                for (int j = 0; j < bins; j++)
                    dummySpectrogram[i, j] = (float)(rng.NextDouble() * 0.1);
            // Generate dummy source and destination fingerprints with small random values to simulate real embeddings.
            var dummySrcFingerprint = Enumerable.Range(0, channels)
                .Select(_ => (float)(rng.NextDouble() * 0.1))
                .ToArray();
            // The destination fingerprint can be the same as the source for this warmup, 
            // since we're just triggering the model's execution path.
            var dummyDestFingerprint = Enumerable.Range(0, channels)
                .Select(_ => (float)(rng.NextDouble() * 0.1))
                .ToArray();
            // Perform multiple passes to ensure all parts of the model are warmed up, including any dynamic graph optimizations.
            for (int pass = 0; pass < 2; pass++)
            {
                var result = ApplyToneColor(dummySpectrogram, dummySrcFingerprint, dummyDestFingerprint, 1.0f);
                ArrayPool<float>.Shared.Return(result.Buffer);
            }
            // If we reach this point without exceptions, the warmup is successful.
            _logger.LogInformation("[AUTO-BASE] OpenVoice warmup complete. System is fully ready.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WARNING] OpenVoice warmup failed, first request may be slower");
        }
    }

    public void Dispose()
    {
        _extractSession?.Dispose();
        _colorSession?.Dispose();
        GC.SuppressFinalize(this);
    }

    // =========================================================================
    // HIGH-PERFORMANCE SOURCE GENERATED LOGGERS (Zero-Allocation)
    // =========================================================================

    [LoggerMessage(Level = LogLevel.Information, Message = "[HARDWARE] OpenVoice Models loaded on GPU (CUDA, Device ID: {DeviceId})")]
    private static partial void LogCudaLoaded(ILogger logger, int deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[HARDWARE] OpenVoice Models loaded on GPU (DirectML, Device ID: {DeviceId})")]
    private static partial void LogDmlLoaded(ILogger logger, int deviceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DEBUG] OpenVoice failed on GPU {DeviceId}")]
    private static partial void LogGpuInitFailed(ILogger logger, Exception ex, int deviceId);


    [LoggerMessage(Level = LogLevel.Information, Message = "  Target Sample Rate: {SampleRate} Hz")]
    private partial void LogSampleRate(int sampleRate);

    [LoggerMessage(Level = LogLevel.Information, Message = "  Filter Length:      {FilterLength}")]
    private partial void LogFilterLength(int filterLength);

    [LoggerMessage(Level = LogLevel.Information, Message = "  Hop Length:         {HopLength}")]
    private partial void LogHopLength(int hopLength);

    [LoggerMessage(Level = LogLevel.Information, Message = "  Gin Channels:       {GinChannels}")]
    private partial void LogGinChannels(int ginChannels);

    [LoggerMessage(Level = LogLevel.Information, Message = "    - {Name}: [{Shape}] ({ElementType})")]
    private partial void LogTensorMetadata(string name, string shape, Type elementType);

    [LoggerMessage(Level = LogLevel.Information, Message = ">>> {SessionName}:")]
    private partial void LogSessionName(string sessionName);
}