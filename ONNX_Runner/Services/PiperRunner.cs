using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NAudio.Lame;
using ONNX_Runner.Models;
using System.Buffers;
using System.Numerics;
using System.Collections.Concurrent;

#if USE_WEBGPU
using Microsoft.ML.OnnxRuntime.EP.WebGpu;
#endif

namespace ONNX_Runner.Services;

/// <summary>
/// The core engine responsible for executing the Piper ONNX model.
/// It handles hardware acceleration, tensor preparation, and the high-performance 
/// conversion of raw neural network output into playable audio formats.
/// Supports both monolithic execution (CPU/CUDA) and Object Pooling (DirectML).
/// </summary>
public partial class PiperRunner : IDisposable
{
    // --- SESSION STATE MANAGEMENT ---
    // Used for thread-safe execution providers (CPU, CUDA)
    private readonly InferenceSession? _sharedSession;
    
    // Used for execution providers that do not support concurrent execution (DirectML)
    private readonly ConcurrentQueue<InferenceSession>? _isolatedSessionPool;
    
    // Fast path routing flag for the hot loop
    private readonly bool _isUsingPool;

    private readonly IPhonemizer _phonemizer;
    private readonly PiperConfig _config;
    private readonly ILogger<PiperRunner> _logger;

    public bool IsUsingGPU { get; private set; }
    
    // Represents the engine's technical concurrency limit (pool size for DML, int.MaxValue for CUDA/CPU)
    public int ConcurrencyCapacity { get; private set; }

    public PiperRunner(string modelPath, PiperConfig config, IPhonemizer phonemizer, OnnxSettings onnxSettings, HardwareSettings hwSettings, ILogger<PiperRunner> logger)
    {
        _phonemizer = phonemizer;
        _config = config;
        _logger = logger;

        var initResult = InitializeSession(modelPath, onnxSettings, hwSettings, logger);
        
        _sharedSession = initResult.SharedSession;
        _isolatedSessionPool = initResult.SessionPool;
        IsUsingGPU = initResult.IsUsingGPU;
        _isUsingPool = initResult.IsUsingPool;
        ConcurrencyCapacity = initResult.Capacity;
    }

    /// <summary>
    /// Dynamically selects the best available hardware based on compile-time flags.
    /// Returns a routing configuration determining if the engine should use a Shared Session or an Object Pool.
    /// </summary>
    private static (InferenceSession? SharedSession, ConcurrentQueue<InferenceSession>? SessionPool, bool IsUsingGPU, bool IsUsingPool, int Capacity) InitializeSession(string modelPath, OnnxSettings onnxSettings, HardwareSettings hwSettings, ILogger<PiperRunner> logger)
    {
        // ====================================================================
        // GPU ACCELERATION BLOCK (Compiled ONLY if USE_CUDA or USE_DML is set)
        // ====================================================================
#if USE_CUDA || USE_DML || USE_WEBGPU
        if (hwSettings.ForcePiperToCpu)
        {
            logger.LogWarning("[HARDWARE] Config override: ForcePiperToCpu is TRUE. Piper model execution is forcefully redirected to the CPU.");
        }
        else
        {
            // Protection against negative numbers in config
            int startingDeviceId = Math.Max(0, hwSettings.PiperGpuDeviceId);
            // Try the desired device + the next 3 as a fallback
            int maxGpusToTry = startingDeviceId + 4; 
            
            for (int deviceId = startingDeviceId; deviceId < maxGpusToTry; deviceId++)
            {
                try
                {
                    // 'using' ensures the native SessionOptions handle is disposed immediately after session creation
                    using var gpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
                    {
                        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                        GraphOptimizationLevel = onnxSettings.EnableGraphOptimization 
                            ? GraphOptimizationLevel.ORT_ENABLE_ALL 
                            : GraphOptimizationLevel.ORT_DISABLE_ALL
                    };
                    
                    // We apply a profile specifically for the GPU
                    onnxSettings.Gpu.ApplyTo(gpuOptions); 

#if USE_CUDA
                    // CUDA supports concurrent execution on a single session.
                    gpuOptions.AppendExecutionProvider_CUDA(deviceId);
                    var session = new InferenceSession(modelPath, gpuOptions);
                    
                    LogCudaLoaded(logger, deviceId); 
                    
                    return (session, null, true, false, int.MaxValue);
#elif USE_DML
                    // DirectML crashes on concurrent execution. We create a fixed-size Object Pool.
                    gpuOptions.AppendExecutionProvider_DML(deviceId);
                    
                    int poolSize = Math.Max(1, hwSettings.MaxConcurrentGpuRequests);
                    var pool = new ConcurrentQueue<InferenceSession>();
                    
                    try
                    {
                        for (int i = 0; i < poolSize; i++)
                        {
                            pool.Enqueue(new InferenceSession(modelPath, gpuOptions));
                        }
                    }
                    catch
                    {
                        // Memory Leak Protection: Dispose successfully created sessions if a subsequent one fails (e.g. OOM)
                        while (pool.TryDequeue(out var leakedSession))
                        {
                            leakedSession.Dispose();
                        }
                        throw;
                    }

                    LogDmlLoaded(logger, deviceId);
                    return (null, pool, true, true, poolSize);
#elif USE_WEBGPU
                    // [HYBRID ARCHITECTURE] 
                    // WebGPU is currently not thread-safe for concurrent executions.
                    // To maintain high throughput, we use a Hybrid approach:
                    // Piper (lightweight) runs on CPU using CPU settings and CPU concurrency limits.
                    // OpenVoice (heavy) runs exclusively on WebGPU with a fixed pool size of 1.
                    logger.LogInformation("[HARDWARE] WebGPU HYBRID mode: Piper routed to CPU for optimal parallel performance.");

                    // The name is "hybridCpuOptions" to avoid conflict with the global fallback
                    using var hybridCpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
                    {
                        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                        GraphOptimizationLevel = onnxSettings.EnableGraphOptimization 
                            ? GraphOptimizationLevel.ORT_ENABLE_ALL 
                            : GraphOptimizationLevel.ORT_DISABLE_ALL
                    };
                    
                    // Honestly apply CPU threading settings (e.g., 4 threads) to maximize CPU speed
                    onnxSettings.Cpu.ApplyTo(hybridCpuOptions);
                    var session = new InferenceSession(modelPath, hybridCpuOptions);

                    // IsUsingGPU = false ensures Program.cs strictly applies MaxConcurrentCpuRequests 
                    // for the global pipeline semaphore.
                    return (session, null, false, false, int.MaxValue);
#endif
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[WARNING] Piper failed to load on GPU {DeviceId}", deviceId);
                }
            }
            logger.LogInformation("[HARDWARE] GPU initialization failed or unavailable. Falling back to CPU.");
        }
        // ====================================================================
        // CPU-ONLY BLOCK (Compiled if CpuOnly flag is used during build)
        // ====================================================================
#else
        logger.LogInformation("[HARDWARE] Lightweight CPU-only build detected. Skipping GPU checks.");
#endif

        // FALLBACK / CPU EXECUTION
        using var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            GraphOptimizationLevel = onnxSettings.EnableGraphOptimization 
                ? GraphOptimizationLevel.ORT_ENABLE_ALL 
                : GraphOptimizationLevel.ORT_DISABLE_ALL
        };
        
        // We apply a profile specifically for the CPU
        onnxSettings.Cpu.ApplyTo(cpuOptions);

        var fallbackSession = new InferenceSession(modelPath, cpuOptions);
        logger.LogInformation("[HARDWARE] Piper Model loaded successfully on CPU.");
        
        // CPU supports concurrent execution on a single session.
        return (fallbackSession, null, false, false, int.MaxValue);
    }

    /// <summary>
    /// Performs raw inference. Converts phonemes into a float array of audio samples.
    /// Handles zero-allocation tensor extraction and safe Object Pool routing.
    /// </summary>
    public (float[] Buffer, int Length) SynthesizeAudioRaw(string phonemes, bool isContinuation = false, bool isFinished = true, float speed = 1.0f, float? requestNoiseScale = null, float? requestNoiseW = null)
    {
        float safeSpeed = Math.Clamp(speed, 0.1f, 10.0f);

        // ARCHITECTURAL LOGIC: LengthScale controls the duration of phonemes. 
        // A lower scale means shorter duration = faster speech.
        float targetLengthScale = _config.Inference.LengthScale / safeSpeed;

        float safeNoiseScale = requestNoiseScale ?? _config.Inference.NoiseScale;
        float safeNoiseW = requestNoiseW ?? _config.Inference.NoiseW;

        // The 'scales' tensor controls the 'robotic vs natural' variance and the speed.
        var scalesTensor = new DenseTensor<float>(new float[] { safeNoiseScale, targetLengthScale, safeNoiseW }, [3]);

        // Passing streaming flags to the phonemizer
        long[] phonemeIds = _phonemizer.PhonemesToIds(phonemes, isContinuation, isFinished);
        var inputTensor = new DenseTensor<long>(phonemeIds, [1, phonemeIds.Length]);
        var inputLengthsTensor = new DenseTensor<long>(new[] { (long)phonemeIds.Length }, [1]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("input_lengths", inputLengthsTensor),
            NamedOnnxValue.CreateFromTensor("scales", scalesTensor)
        };

        // --- SESSION ROUTING ---
        InferenceSession activeSession;
        bool returnToPool = false;

        if (_isUsingPool)
        {
            // DirectML Object Pool
            if (!_isolatedSessionPool!.TryDequeue(out activeSession!))
                throw new InvalidOperationException("DirectML Session Pool is exhausted. Check your global Semaphore limits.");
            
            returnToPool = true;
        }
        else
        {
            // Shared Execution (CUDA/CPU)
            activeSession = _sharedSession!;
        }

        try
        {
            using var results = activeSession.Run(inputs);
            var outputNode = results.First(r => r.Name == "output");
            var outputTensor = outputNode.AsTensor<float>();

            int length = (int)outputTensor.Length;

            // ZERO-ALLOCATION: Rent a buffer instead of creating a new array to save GC cycles.
            float[] buffer = ArrayPool<float>.Shared.Rent(length);

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
        finally
        {
            // Ensure the locked session is returned to the queue even if inference fails
            if (returnToPool)
            {
                _isolatedSessionPool!.Enqueue(activeSession);
            }
        }
    }

    /// <summary>
    /// A high-level wrapper that produces a standard WAV byte array.
    /// </summary>
    public byte[] SynthesizeAudio(string phonemes, bool isContinuation = false, bool isFinished = true, float speed = 1.0f, float? requestNoiseScale = null, float? requestNoiseW = null)
    {
        var rawResult = SynthesizeAudioRaw(phonemes, isContinuation, isFinished, speed, requestNoiseScale, requestNoiseW);
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
        _sharedSession?.Dispose();

        if (_isolatedSessionPool != null)
        {
            while (_isolatedSessionPool.TryDequeue(out var session))
            {
                session.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[HARDWARE] Piper Model loaded successfully on GPU (CUDA, Device ID: {DeviceId})")]
    private static partial void LogCudaLoaded(ILogger logger, int deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[HARDWARE] Piper Model loaded successfully on GPU (DirectML, Device ID: {DeviceId})")]
    private static partial void LogDmlLoaded(ILogger logger, int deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[HARDWARE] Piper Model loaded successfully on GPU (WebGPU, Device ID: {DeviceId})")]
    private static partial void LogWebGpuLoaded(ILogger logger, int deviceId);
}