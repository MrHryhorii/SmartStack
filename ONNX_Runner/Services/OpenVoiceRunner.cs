using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices; // REQUIRED FOR MemoryMarshal (fast flat access to rectangular arrays)
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ONNX_Runner.Models;

#if USE_WEBGPU
using Microsoft.ML.OnnxRuntime.EP.WebGpu;
#endif

namespace ONNX_Runner.Services;

/// <summary>
/// Handles the execution of OpenVoice AI models for Voice Cloning.
/// It manages two distinct ONNX sessions:
/// 1. Tone Extractor: Extracts unique vocal characteristics (embeddings) from audio.
///    Used sequentially at startup only, so it stays a single shared session.
/// 2. Tone Color Converter: Applies a source tone color to a target voice using Latent Space Blending.
///    Called concurrently for every cloning request, so it follows the same
///    Shared Session (CPU/CUDA) vs Object Pool (DirectML) routing as PiperRunner.
/// </summary>
public partial class OpenVoiceRunner : IDisposable
{
    // --- EXTRACTOR SESSION STATE ---
    // The Extractor session is nullable because it can be unloaded from memory after 
    // the initial startup processing to save precious VRAM/RAM. It is only ever called
    // sequentially at startup (before app.Run() begins accepting requests), so it never
    // needs pooling under the current architecture.
    private InferenceSession? _extractSession;

    // --- COLOR CONVERTER SESSION STATE ---
    // Used for thread-safe execution providers (CPU, CUDA)
    private readonly InferenceSession? _sharedColorSession;

    // Used for execution providers that do not support concurrent execution (DirectML)
    private readonly ConcurrentQueue<InferenceSession>? _colorSessionPool;

    // Fast path routing flag for the hot loop
    private readonly bool _isUsingColorPool;

    // Blocking gate sized to match the color pool. Under normal operation this should
    // never actually block, since the caller (SpeechEndpoint) already limits overall
    // concurrency via its own gpuSemaphore before cloning is even known to be needed.
    // It exists as a cheap safety net in case that external limit ever drifts out of
    // sync with this pool's size.
    private readonly SemaphoreSlim? _colorGate;

    private readonly ToneConfig _config;
    private readonly ILogger<OpenVoiceRunner> _logger;

    // A dictionary acting as a 'Voice Library': 
    // Key - Voice name (e.g., "MorganFreeman"), Value - Tonal fingerprint (256-float embedding).
    public Dictionary<string, float[]> VoiceLibrary { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the sample rate expected by the OpenVoice models.
    /// </summary>
    public int GetTargetSamplingRate() => _config.Data.SamplingRate;

    // Represents the Color Converter's technical concurrency limit (pool size for DML,
    // int.MaxValue for CUDA/CPU). Single source of truth, mirroring PiperRunner.ConcurrencyCapacity.
    public int ColorConcurrencyCapacity { get; private set; }

    public OpenVoiceRunner(string extractPath, string colorPath, ToneConfig config, OnnxSettings onnxSettings, HardwareSettings hwSettings, ILogger<OpenVoiceRunner> logger)
    {
        _config = config;
        _logger = logger;

        var (ExtractSession, SharedColorSession, ColorSessionPool, IsUsingColorPool, Capacity) = InitializeSessions(extractPath, colorPath, onnxSettings, hwSettings, logger);

        _extractSession = ExtractSession;
        _sharedColorSession = SharedColorSession;
        _colorSessionPool = ColorSessionPool;
        _isUsingColorPool = IsUsingColorPool;
        ColorConcurrencyCapacity = Capacity;

        if (_isUsingColorPool)
        {
            _colorGate = new SemaphoreSlim(ColorConcurrencyCapacity, ColorConcurrencyCapacity);
        }

        PrintModelMetadata();
    }

    /// <summary>
    /// Dynamically selects the best available hardware based on compile-time flags.
    /// The Extractor always gets a single session (sequential startup use only).
    /// The Color Converter gets a Shared Session on CPU/CUDA, or an Object Pool on
    /// DirectML, since DirectML crashes on concurrent execution of a single session.
    /// </summary>
    private static (InferenceSession? ExtractSession, InferenceSession? SharedColorSession, ConcurrentQueue<InferenceSession>? ColorSessionPool, bool IsUsingColorPool, int Capacity) InitializeSessions(string extractPath, string colorPath, OnnxSettings onnxSettings, HardwareSettings hwSettings, ILogger<OpenVoiceRunner> logger)
    {
        // ====================================================================
        // GPU ACCELERATION BLOCK (Compiled when USE_CUDA, USE_DML, or USE_WEBGPU is set)
        // ====================================================================
#if USE_CUDA || USE_DML || USE_WEBGPU
        // Protection against negative numbers in config
        int startingDeviceId = Math.Max(0, hwSettings.OpenVoiceGpuDeviceId);
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
                // CUDA supports concurrent execution on a single session, so both models
                // stay as simple shared sessions.
                gpuOptions.AppendExecutionProvider_CUDA(deviceId);

                var extract = new InferenceSession(extractPath, gpuOptions);
                var color = new InferenceSession(colorPath, gpuOptions);

                OnnxHardwareDiagnostics.LogCudaDevice(logger, "OpenVoice Models", deviceId);
                return (extract, color, null, false, int.MaxValue);
#elif USE_DML
                // DirectML crashes on concurrent execution.
                gpuOptions.AppendExecutionProvider_DML(deviceId);

                // Extractor: always called sequentially at startup, before the server
                // accepts requests. No pooling needed — a single session is sufficient.
                var extract = new InferenceSession(extractPath, gpuOptions);

                // Color Converter: called concurrently for every cloning request during
                // normal operation, so it needs the same fixed-size Object Pool as Piper.
                int poolSize = Math.Max(1, hwSettings.MaxConcurrentGpuRequests);
                var colorPool = new ConcurrentQueue<InferenceSession>();

                try
                {
                    for (int i = 0; i < poolSize; i++)
                    {
                        colorPool.Enqueue(new InferenceSession(colorPath, gpuOptions));
                    }
                }
                catch
                {
                    // Memory Leak Protection: dispose the already-loaded Extractor and any
                    // successfully created Color sessions if a subsequent one fails (e.g. OOM)
                    extract.Dispose();
                    while (colorPool.TryDequeue(out var leakedSession))
                    {
                        leakedSession.Dispose();
                    }
                    throw;
                }

                OnnxHardwareDiagnostics.LogDirectMlDevice(logger, "OpenVoice Models", deviceId);
                return (extract, null, colorPool, true, poolSize);
#elif USE_WEBGPU
                // WebGPU is provided as a plugin Execution Provider.
                var env = OrtEnv.Instance();
                const string registrationName = "webgpu_ep";
                string webGpuEpName = WebGpuEp.GetEpName();

                // OrtEnv is process-wide. Register the WebGPU plugin only once.
                bool isRegistered = false;

                foreach (var device in env.GetEpDevices())
                {
                    if (string.Equals(
                        device.EpName,
                        webGpuEpName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        isRegistered = true;
                        break;
                    }
                }

                if (!isRegistered)
                {
                    try
                    {
                        env.RegisterExecutionProviderLibrary(
                            registrationName,
                            WebGpuEp.GetLibraryPath());
                    }
                    catch (Exception ex) when (
                        ex.Message.Contains(
                            "already registered",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // Another initialization path registered the process-wide plugin.
                    }
                }

                OrtEpDevice? webGpuDevice = null;

                foreach (var device in env.GetEpDevices())
                {
                    if (!string.Equals(
                        device.EpName,
                        webGpuEpName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (device.HardwareDevice.Type != OrtHardwareDeviceType.GPU)
                    {
                        continue;
                    }

                    // On Windows this is the DXGI adapter index.
                    // It matches the deviceId semantics used by DirectML.
                    if (!device.HardwareDevice.Metadata.Entries.TryGetValue(
                        "DxgiAdapterNumber",
                        out string? adapterNumberText))
                    {
                        continue;
                    }

                    if (!int.TryParse(
                        adapterNumberText,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int adapterNumber))
                    {
                        continue;
                    }

                    if (adapterNumber != deviceId)
                    {
                        continue;
                    }

                    webGpuDevice = device;
                    break;
                }

                if (webGpuDevice == null)
                {
                    throw new InvalidOperationException(
                        $"No WebGPU-compatible GPU was found for DXGI adapter index {deviceId}.");
                }

                // The OrtEpDevice already identifies the physical GPU.
                // Do not use the WebGPU "deviceId" option here:
                // that value is a WebGPU context ID, not a GPU adapter index.
                gpuOptions.AppendExecutionProvider(
                    env,
                    new[] { webGpuDevice },
                    new Dictionary<string, string>());

                // Extractor runs sequentially during startup.
                var extract = new InferenceSession(
                    extractPath,
                    gpuOptions);

                // Current WebGPU implementation must execute Color Converter inference
                // strictly one request at a time.
                int poolSize = 1;
                var colorPool = new ConcurrentQueue<InferenceSession>();

                try
                {
                    colorPool.Enqueue(
                        new InferenceSession(
                            colorPath,
                            gpuOptions));
                }
                catch
                {
                    extract.Dispose();

                    while (colorPool.TryDequeue(out var leakedSession))
                    {
                        leakedSession.Dispose();
                    }

                    throw;
                }

                OnnxHardwareDiagnostics.LogWebGpuDevice(
                    logger,
                    "OpenVoice Models",
                    deviceId,
                    webGpuDevice);

                return (
                    extract,
                    null,
                    colorPool,
                    true,
                    poolSize);
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
        using var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
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

        OnnxHardwareDiagnostics.LogCpuDevice(logger, "OpenVoice Models");

        // CPU supports concurrent execution on a single session.
        return (cpuExtract, cpuColor, null, false, int.MaxValue);
    }

    // --- EMBEDDING EXTRACTION & FINGERPRINT MANAGEMENT ---

    /// <summary>
    /// Extracts a 256-dimensional tone embedding (fingerprint) from a provided audio spectrogram.
    /// This acts as the mathematical 'DNA' of a specific voice.
    /// NOTE: Only ever called sequentially at startup under the current architecture,
    /// so it talks directly to the single _extractSession without any pool routing.
    /// </summary>
    public float[] ExtractToneColor(float[,] spectrogram)
    {
        ArgumentNullException.ThrowIfNull(spectrogram);

        if (_extractSession == null)
            throw new InvalidOperationException("Tone Extractor has been unloaded from memory.");

        int frames = spectrogram.GetLength(0);
        int bins = spectrogram.GetLength(1);

        if (frames <= 0 || bins <= 0)
            throw new ArgumentException("Tone Extractor spectrogram must contain at least one frame and one bin.", nameof(spectrogram));

        int tensorSize = checked(frames * bins);

        // The exported extractor already consumes OpenVoice ref_enc layout: [1, frames, bins].
        float[] rentedInput = ArrayPool<float>.Shared.Rent(tensorSize);
        try
        {
            // Rectangular float arrays are contiguous and already match [frames, bins] row order.
            System.Diagnostics.Debug.Assert(tensorSize == spectrogram.Length,
                "Spectrogram element count must match frames * bins.");

            ReadOnlySpan<float> flatSpectrogram = MemoryMarshal.CreateSpan(ref spectrogram[0, 0], tensorSize);
            flatSpectrogram.CopyTo(rentedInput.AsSpan(0, tensorSize));

            // Map the rented array to a Tensor without copying data.
            var memory = new Memory<float>(rentedInput, 0, tensorSize);
            var inputTensor = new DenseTensor<float>(memory, [1, frames, bins]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor)
            };

            using var results = _extractSession.Run(inputs);
            float[] embedding = [.. results.First(r => r.Name == "tone_embedding").AsEnumerable<float>()];

            if (embedding.Length != _config.Model.GinChannels)
                throw new InvalidDataException(
                    $"Tone Extractor returned {embedding.Length} channels, expected {_config.Model.GinChannels}.");

            return embedding;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedInput);
        }
    }

    /// <summary>
    /// Persists a tone-color embedding for reuse without re-running the extractor.
    /// </summary>
    public void SaveVoiceFingerprint(string path, float[] embedding)
    {
        byte[] result = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, result, 0, result.Length);
        File.WriteAllBytes(path, result);
    }

    /// <summary>
    /// Loads a previously persisted tone-color embedding.
    /// </summary>
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

        // Inspecting a pooled session is safe here: this runs from the constructor,
        // strictly before app.Run() starts accepting requests, so no concurrent
        // caller can be mid-dequeue while we peek at one session and hand it back.
        if (_isUsingColorPool)
        {
            if (_colorSessionPool!.TryDequeue(out var sampleSession))
            {
                InspectSession("TONE COLOR CONVERTER", sampleSession);
                _colorSessionPool.Enqueue(sampleSession);
            }
        }
        else if (_sharedColorSession != null)
        {
            InspectSession("TONE COLOR CONVERTER", _sharedColorSession);
        }
    }

    // Logs ONNX input and output metadata for one session.
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
    /// Applies tone-color conversion using a flat spectrogram already arranged in the
    /// [bins, frames] layout required by the ONNX model. Ownership remains with the caller.
    /// </summary>
    public (float[] Buffer, int Length) ApplyToneColor(
        float[] transposedSpectrogram,
        int frames,
        int bins,
        float[] srcFingerprint,
        float[] destFingerprint,
        float tau = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(transposedSpectrogram);
        ArgumentNullException.ThrowIfNull(srcFingerprint);
        ArgumentNullException.ThrowIfNull(destFingerprint);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bins);

        int channels = _config.Model.GinChannels;
        if (srcFingerprint.Length != channels)
            throw new ArgumentException($"Source tone embedding must contain exactly {channels} values.", nameof(srcFingerprint));

        if (destFingerprint.Length != channels)
            throw new ArgumentException($"Destination tone embedding must contain exactly {channels} values.", nameof(destFingerprint));

        int tensorSize = checked(frames * bins);
        if (transposedSpectrogram.Length < tensorSize)
        {
            throw new ArgumentException("Spectrogram buffer is smaller than frames * bins.", nameof(transposedSpectrogram));
        }

        return ApplyToneColorCore(
            transposedSpectrogram,
            tensorSize,
            frames,
            bins,
            srcFingerprint,
            destFingerprint,
            tau);
    }

    // Executes the color-converter inference using the already prepared tensor buffer.
    private (float[] Buffer, int Length) ApplyToneColorCore(
        float[] tensorBuffer,
        int tensorSize,
        int frames,
        int bins,
        float[] srcFingerprint,
        float[] destFingerprint,
        float tau)
    {
        int channels = _config.Model.GinChannels;

        // --- SESSION ROUTING ---
        InferenceSession activeSession;
        bool releaseGate = false;

        if (_isUsingColorPool)
        {
            // DirectML/WebGPU object pool. Blocking here is intentional: it is the final
            // hardware-level safety gate if outer request concurrency ever exceeds pool capacity.
            _colorGate!.Wait();
            releaseGate = true;

            if (!_colorSessionPool!.TryDequeue(out activeSession!))
            {
                _colorGate.Release();
                releaseGate = false;
                throw new InvalidOperationException("OpenVoice color session pool is unexpectedly empty.");
            }
        }
        else
        {
            activeSession = _sharedColorSession!;
        }

        try
        {
            var memory = new Memory<float>(tensorBuffer, 0, tensorSize);
            var audioTensor = new DenseTensor<float>(memory, [1, bins, frames]);

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

            using var results = activeSession.Run(inputs);
            var outputNode = results.First(r => r.Name == "converted_audio");
            var outputTensor = outputNode.AsTensor<float>();

            int outLength = (int)outputTensor.Length;
            float[] outBuffer = ArrayPool<float>.Shared.Rent(outLength);

            try
            {
                if (outputTensor is DenseTensor<float> denseTensor)
                {
                    denseTensor.Buffer.Span.CopyTo(outBuffer);
                }
                else
                {
                    int index = 0;
                    foreach (var value in outputTensor)
                    {
                        outBuffer[index++] = value;
                    }
                }

                return (outBuffer, outLength);
            }
            catch
            {
                ArrayPool<float>.Shared.Return(outBuffer);
                throw;
            }
        }
        finally
        {
            if (releaseGate)
            {
                _colorSessionPool!.Enqueue(activeSession);
                _colorGate!.Release();
            }
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
            // Build directly in the [bins, frames] layout used by the runtime colorizer.
            var dummySpectrogram = new float[checked(frames * bins)];
            for (int i = 0; i < dummySpectrogram.Length; i++)
            {
                dummySpectrogram[i] = (float)(rng.NextDouble() * 0.1);
            }
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
                var result = ApplyToneColor(dummySpectrogram, frames, bins, dummySrcFingerprint, dummyDestFingerprint, 1.0f);
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

    /// <summary>
    /// Releases all OpenVoice ONNX sessions and concurrency gates.
    /// </summary>
    public void Dispose()
    {
        _extractSession?.Dispose();
        _sharedColorSession?.Dispose();

        if (_colorSessionPool != null)
        {
            while (_colorSessionPool.TryDequeue(out var session))
            {
                session.Dispose();
            }
        }

        _colorGate?.Dispose();
        GC.SuppressFinalize(this);
    }

    // =========================================================================
    // HIGH-PERFORMANCE SOURCE GENERATED LOGGERS (Zero-Allocation)
    // =========================================================================

    [LoggerMessage(Level = LogLevel.Information, Message = "[HARDWARE] OpenVoice Models loaded on GPU (CUDA, Device ID: {DeviceId})")]
    private static partial void LogCudaLoaded(ILogger logger, int deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[HARDWARE] OpenVoice Models loaded on GPU (DirectML, Device ID: {DeviceId})")]
    private static partial void LogDmlLoaded(ILogger logger, int deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[HARDWARE] OpenVoice Models loaded on GPU (WebGPU, Device ID: {DeviceId})")]
    private static partial void LogWebGpuLoaded(ILogger logger, int deviceId);

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
