using Microsoft.AspNetCore.Mvc;
using ONNX_Runner.Models;
using ONNX_Runner.Services;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Додаємо підтримку Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- ЗАВАНТАЖЕННЯ МОДЕЛІ ТА ЛОГУВАННЯ ---
string modelDirectory = "Model";
PiperConfig? piperConfig = null;
string? piperModelPath = null;

try
{
    // Створюємо папку, якщо її ще немає, щоб програма не падала при першому запуску
    if (!Directory.Exists(modelDirectory))
    {
        Directory.CreateDirectory(modelDirectory);
        Console.WriteLine($"[WARNING] Directory '{modelDirectory}' was created. Please put your .onnx and .json files there.");
    }
    else
    {
        var (onnxPath, config) = ModelLoader.LoadFromDirectory(modelDirectory);
        piperModelPath = onnxPath;
        piperConfig = config;

        // Вивід даних у консоль
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("=========================================");
        Console.WriteLine("        MODEL LOADED SUCCESSFULLY        ");
        Console.WriteLine("=========================================");
        Console.ResetColor();
        Console.WriteLine($"Model Path:      {onnxPath}");
        Console.WriteLine($"Sample Rate:     {config.Audio.SampleRate} Hz");
        Console.WriteLine($"Espeak Voice:    {config.Espeak.Voice}");
        Console.WriteLine($"Noise Scale:     {config.Inference.NoiseScale}");
        Console.WriteLine($"Length Scale:    {config.Inference.LengthScale}");
        Console.WriteLine($"Noise W:         {config.Inference.NoiseW}");
        Console.WriteLine($"Total Phonemes:  {config.PhonemeIdMap.Count} unique sounds mapped");
        Console.WriteLine("=========================================\n");
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ERROR] Failed to load model: {ex.Message}");
    Console.ResetColor();
}

// Читаємо налаштування з appsettings.json
var corsConfig = builder.Configuration.GetSection("CorsSettings").Get<CorsSettings>() ?? new CorsSettings();
var phonemizerConfig = builder.Configuration.GetSection("PhonemizerSettings").Get<PhonemizerSettings>() ?? new PhonemizerSettings();
var chunkerConfig = builder.Configuration.GetSection("ChunkerSettings").Get<ChunkerSettings>() ?? new ChunkerSettings();
var hardwareConfig = builder.Configuration.GetSection("HardwareSettings").Get<HardwareSettings>() ?? new HardwareSettings();
var dspConfig = builder.Configuration.GetSection("DspSettings").Get<DspSettings>() ?? new DspSettings();
var streamConfig = builder.Configuration.GetSection("StreamSettings").Get<StreamSettings>() ?? new StreamSettings();

// --- РЕЄСТРАЦІЯ СЕРВІСІВ ---
builder.Services.AddSingleton(streamConfig);

if (piperConfig != null && piperModelPath != null)
{
    var phonemizer = new PiperPhonemizer(piperConfig);
    builder.Services.AddSingleton<IPhonemizer>(phonemizer);

    // Реєструємо чанкер для розбиття довгих рядків із параметрами з конфігу
    var textChunker = new TextChunker(chunkerConfig);
    builder.Services.AddSingleton(textChunker);

    var runner = new PiperRunner(piperModelPath, piperConfig, phonemizer);
    builder.Services.AddSingleton(runner);

    // Реєструємо мапер пунктуації
    var punctuationMapper = new DynamicPunctuationMapper(piperConfig);
    builder.Services.AddSingleton(punctuationMapper);

    string dataPath = Path.GetFullPath("PiperNative");
    var mixedEspeak = new EspeakWrapper(dataPath, piperConfig.Espeak.Voice ?? "en");
    builder.Services.AddSingleton(mixedEspeak);

    // --- ЗАВАНТАЖЕННЯ OPENVOICE (CLONER) ---
    string clonerDirectory = "Cloner";
    string voicesDirectory = "Voices";

    if (Directory.Exists(clonerDirectory))
    {
        try
        {
            string extractPath = Path.Combine(clonerDirectory, "tone_extract.onnx");
            string colorPath = Path.Combine(clonerDirectory, "tone_color.onnx");
            string toneJsonPath = Path.Combine(clonerDirectory, "tone_config.json");

            if (File.Exists(extractPath) && File.Exists(colorPath) && File.Exists(toneJsonPath))
            {
                string toneJsonContent = File.ReadAllText(toneJsonPath);
                var toneConfig = System.Text.Json.JsonSerializer.Deserialize<ToneConfig>(toneJsonContent);

                if (toneConfig != null)
                {
                    var openVoice = new OpenVoiceRunner(extractPath, colorPath, toneConfig);
                    var audioProc = new AudioProcessor(toneConfig);

                    // --- СКАНУВАННЯ ТА ГЕНЕРАЦІЯ ГОЛОСІВ ---
                    if (!Directory.Exists(voicesDirectory)) Directory.CreateDirectory(voicesDirectory);

                    var wavFiles = Directory.GetFiles(voicesDirectory, "*.wav");
                    foreach (var wavPath in wavFiles)
                    {
                        string voiceName = Path.GetFileNameWithoutExtension(wavPath);
                        string fingerprintPath = Path.Combine(voicesDirectory, voiceName + ".voice");

                        if (File.Exists(fingerprintPath))
                        {
                            var fingerprint = openVoice.LoadVoiceFingerprint(fingerprintPath);
                            openVoice.VoiceLibrary[voiceName] = fingerprint;
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.WriteLine($"[VOICE] Loaded from cache: {voiceName}");
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.DarkYellow;
                            Console.WriteLine($"\n[VOICE] processing: {voiceName}...");

                            var (rawSamples, fileRate) = audioProc.LoadWav(wavPath);
                            Console.WriteLine($"   -> Step 1: Read {rawSamples.Length} samples at {fileRate}Hz");

                            if (rawSamples.Length == 0) continue;

                            float[] readySamples = audioProc.Resample(rawSamples, fileRate, toneConfig.Data.SamplingRate);
                            Console.WriteLine($"   -> Step 2: Resampled to {toneConfig.Data.SamplingRate}Hz. New count: {readySamples.Length}");

                            var spec = audioProc.GetMagnitudeSpectrogram(readySamples);
                            int frames = spec.GetLength(0);
                            Console.WriteLine($"   -> Step 3: Spectrogram frames: {frames}");

                            if (frames == 0) continue;

                            var fingerprint = openVoice.ExtractToneColor(spec);
                            Console.WriteLine($"   -> Step 4: Fingerprint extracted (Size: {fingerprint.Length})");

                            openVoice.SaveVoiceFingerprint(fingerprintPath, fingerprint);
                            openVoice.VoiceLibrary[voiceName] = fingerprint;

                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"   [SUCCESS] Zlipok saved: {voiceName}.voice");
                            Console.ResetColor();
                        }
                    }

                    builder.Services.AddSingleton(openVoice);
                    builder.Services.AddSingleton(audioProc);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to load OpenVoice/Voices: {ex.Message}");
        }
    }

    MixedLanguagePhonemizer? mixedPhonemizer = null;
    PhonemeFallbackMapper? fallbackMapper = null;

    if (phonemizerConfig != null && phonemizerConfig.UseLanguageDetector)
    {
        string phoibleDirectory = "PHOIBLE";
        string phoiblePath = Path.Combine(phoibleDirectory, "phoible.csv");

        if (!Directory.Exists(phoibleDirectory))
        {
            Directory.CreateDirectory(phoibleDirectory);
            Console.WriteLine($"[WARNING] Directory '{phoibleDirectory}' was created. Please put your 'phoible.csv' file there.");
        }

        fallbackMapper = new PhonemeFallbackMapper(phoiblePath, piperConfig);
        builder.Services.AddSingleton(fallbackMapper);

        mixedPhonemizer = new MixedLanguagePhonemizer(
            phonemizerConfig,
            piperConfig.Espeak.Voice ?? "en"
        );
        builder.Services.AddSingleton(mixedPhonemizer);
    }

    var unifiedPhonemizer = new UnifiedPhonemizer(
        mixedEspeak,
        punctuationMapper,
        piperConfig,
        mixedPhonemizer,
        fallbackMapper
    );
    builder.Services.AddSingleton(unifiedPhonemizer);
}

// =================================================================
// НАЛАШТУВАННЯ CORS (ДЛЯ ВЕБ-КЛІЄНТІВ)
// =================================================================
builder.Services.AddCors(options =>
{
    options.AddPolicy("DynamicCorsPolicy", policy =>
    {
        if (corsConfig.AllowAnyOrigin)
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .WithExposedHeaders("X-Audio-Sample-Rate", "Content-Disposition"); // Дозволяємо фронтенду бачити ці заголовки
        }
        else
        {
            policy.WithOrigins(corsConfig.AllowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .WithExposedHeaders("X-Audio-Sample-Rate", "Content-Disposition");
        }
    });
});

// =================================================================
// ЗАХИСТ ВІД БОТІВ ТА DDOS (RATE LIMITING)
// =================================================================
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ip_limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 5,
                Window = TimeSpan.FromSeconds(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("DynamicCorsPolicy"); // Вмикаємо CORS
app.UseRateLimiter();

// =================================================================
// АВТОМАТИЧНА ГЕНЕРАЦІЯ БАЗОВОГО ЗЛІПКУ PIPER
// =================================================================
using (var scope = app.Services.CreateScope())
{
    var openVoiceSvc = scope.ServiceProvider.GetService<OpenVoiceRunner>();
    var audioProcSvc = scope.ServiceProvider.GetService<AudioProcessor>();
    var unifiedPhonemizerSvc = scope.ServiceProvider.GetService<UnifiedPhonemizer>();
    var piperRunnerSvc = scope.ServiceProvider.GetService<PiperRunner>();

    if (openVoiceSvc != null && audioProcSvc != null && unifiedPhonemizerSvc != null && piperRunnerSvc != null && piperConfig != null)
    {
        var baseGenerator = new BaseVoiceGenerator(
            unifiedPhonemizerSvc,
            piperRunnerSvc,
            audioProcSvc,
            openVoiceSvc,
            piperConfig
        );

        baseGenerator.GenerateAndCacheBaseFingerprint();
        openVoiceSvc.UnloadExtractor();
    }
}

// =================================================================
// ДИНАМІЧНА ЧЕРГА ЗАПИТІВ (РОЗУМНИЙ СЕМАФОР)
// =================================================================
int concurrentRequests = 1;

using (var scope = app.Services.CreateScope())
{
    var piperSvc = scope.ServiceProvider.GetService<PiperRunner>();
    if (piperSvc != null)
    {
        if (piperSvc.IsUsingGPU)
        {
            double totalVramGb = hardwareConfig.TotalVramGb;
            double vramPerRequest = hardwareConfig.VramPerRequestGb;
            concurrentRequests = Math.Max(1, (int)(totalVramGb / vramPerRequest));

            Console.WriteLine($"[SYSTEM] Running on GPU ({totalVramGb}GB VRAM allocated from config).");
            Console.WriteLine($"[SYSTEM] Concurrent ONNX tasks mathematically limited to: {concurrentRequests}");
        }
        else
        {
            int totalCores = Environment.ProcessorCount;
            double cpuMultiplier = Math.Clamp(hardwareConfig.CpuCoresUsageMultiplier, 0.1, 1.0);
            concurrentRequests = Math.Max(1, (int)(totalCores * cpuMultiplier));

            Console.WriteLine($"[SYSTEM] Running on CPU (Total cores: {totalCores}).");
            Console.WriteLine($"[SYSTEM] Concurrent ONNX tasks limited to {cpuMultiplier * 100}%: {concurrentRequests}");
        }
    }
}

var gpuSemaphore = new SemaphoreSlim(concurrentRequests, concurrentRequests);

// =================================================================
// ENDPOINTS
// =================================================================

// Global set of allowed audio formats for validation
var allowedFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wav", "mp3", "opus", "pcm" };

// ENDPOINT 1: Text-to-Speech Generation
app.MapPost("/v1/audio/speech", async (
    HttpContext httpContext,
    [FromBody] OpenAiSpeechRequest request,
    [FromServices] TextChunker textChunker,
    [FromServices] UnifiedPhonemizer unifiedPhonemizer,
    [FromServices] PiperRunner piperRunner,
    [FromServices] IServiceProvider services,
    CancellationToken cancellationToken) => // Injected by ASP.NET to track client disconnects
{
    // --- Request Validation ---
    if (string.IsNullOrWhiteSpace(request.Input)) return Results.BadRequest(new { error = "Input text cannot be empty." });
    if (piperConfig == null) return Results.Problem("Model is not loaded properly.", statusCode: 500);

    if (string.IsNullOrWhiteSpace(request.ResponseFormat) || !allowedFormats.Contains(request.ResponseFormat))
    {
        return Results.BadRequest(new
        {
            error = $"Unsupported response_format: '{request.ResponseFormat}'. Supported formats are: {string.Join(", ", allowedFormats)}."
        });
    }

    try
    {
        // --- Concurrency Control (Semaphore Pattern) ---
        // Prevents OutOfMemoryExceptions by queuing requests. 
        // Aborts immediately if the client disconnects while waiting in the queue.
        await gpuSemaphore.WaitAsync(cancellationToken);

        try
        {
            // --- Pipeline Configuration ---
            var streamConfig = services.GetRequiredService<StreamSettings>();
            bool shouldStream = request.Stream ?? streamConfig.EnableStreaming;
            bool isWav = request.ResponseFormat.Equals("wav", StringComparison.OrdinalIgnoreCase);

            // WAV requires a file size in its header, so chunked streaming is conceptually impossible.
            bool useStreaming = shouldStream && !isWav;

            bool useOpenVoice = !string.IsNullOrEmpty(request.Voice);
            var openVoice = services.GetService<OpenVoiceRunner>();
            var audioProc = services.GetService<AudioProcessor>();
            bool canClone = useOpenVoice && openVoice != null && audioProc != null;

            // Determine target sample rates based on requested format and pipeline capabilities
            int outSampleRate = canClone ? openVoice!.GetTargetSamplingRate() : piperConfig.Audio.SampleRate;
            int finalSampleRate = outSampleRate;
            bool isOpus = request.ResponseFormat.Equals("opus", StringComparison.OrdinalIgnoreCase);

            if (isOpus)
            {
                // Ogg Opus strictly requires specific sample rates (8, 12, 16, 24, or 48 kHz)
                int[] validOpusRates = [8000, 12000, 16000, 24000, 48000];
                finalSampleRate = validOpusRates.OrderBy(r => Math.Abs(r - outSampleRate)).First();
            }

            int displaySampleRate = isOpus ? 48000 : finalSampleRate;

            // --- Network Gateway Setup (Bridging & Backpressure) ---
            Stream targetStream;
            System.Threading.Channels.Channel<byte[]>? networkChannel = null;
            Task? networkSenderTask = null;

            if (useStreaming)
            {
                // Send HTTP headers before any generation starts to initiate Chunked Transfer Encoding
                httpContext.Response.ContentType = AudioStreamManager.GetMimeType(request);
                httpContext.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{AudioStreamManager.GetFileName(request)}\"");
                httpContext.Response.Headers.Append("X-Audio-Sample-Rate", displaySampleRate.ToString());
                await httpContext.Response.StartAsync(cancellationToken);

                // Backpressure configuration: limit buffer size.
                // If the client network is too slow, the pipeline will pause to prevent RAM exhaustion.
                var channelOptions = new System.Threading.Channels.BoundedChannelOptions(50)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
                };
                networkChannel = System.Threading.Channels.Channel.CreateBounded<byte[]>(channelOptions);

                int chunkSize = streamConfig.MinChunkSizeKb * 1024;
                targetStream = new BridgingStream(networkChannel.Writer, chunkSize);

                // Asynchronous Network Courier: Pushes buffered bytes to the client
                networkSenderTask = Task.Run(async () =>
                {
                    await foreach (var chunk in networkChannel.Reader.ReadAllAsync(cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await httpContext.Response.Body.WriteAsync(chunk, cancellationToken);
                        await httpContext.Response.Body.FlushAsync(cancellationToken);
                    }
                }, cancellationToken);
            }
            else
            {
                // Synchronous fallback: buffer everything in memory
                targetStream = new MemoryStream(1024 * 1024); // Pre-allocate 1 MB
            }

            // --- Asynchronous Audio Generation (Producer-Consumer Pattern) ---
            byte[]? finalAudioBytes = null;

            await Task.Run(async () =>
            {
                var textChunks = textChunker.Split(request.Input);

                float[]? targetFingerprint = null;
                float[]? sourceFingerprint = null;
                if (canClone)
                {
                    openVoice!.VoiceLibrary.TryGetValue(request.Voice, out targetFingerprint);
                    openVoice.VoiceLibrary.TryGetValue("piper_base", out sourceFingerprint);
                }

                float currentSpeed = (request.Speed > 0.1f) ? request.Speed : 1.0f;
                int silenceSamplesCount = (int)(finalSampleRate * (chunkerConfig.SentencePauseSeconds / currentSpeed));
                float[] absoluteSilence = new float[silenceSamplesCount];

                NAudio.Dsp.BiQuadFilter? filter = null;
                if (canClone && dspConfig.EnableLowPassFilter)
                    filter = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, dspConfig.LowPassCutoffFrequency, dspConfig.LowPassQFactor);

                using (var streamManager = new AudioStreamManager(request, finalSampleRate, targetStream))
                {
                    var channel = System.Threading.Channels.Channel.CreateBounded<float[]>(10);

                    // PRODUCER: Text -> Phonemes -> Raw Audio (GPU/CPU bound)
                    var producerTask = Task.Run(async () =>
                    {
                        foreach (var chunk in textChunks)
                        {
                            // Graceful Cancellation: Abort immediately if the client disconnects
                            cancellationToken.ThrowIfCancellationRequested();

                            string phonemes = unifiedPhonemizer.GetPhonemes(chunk);
                            float[] chunkSamples = piperRunner.SynthesizeAudioRaw(phonemes, request.Speed, request.NoiseScale, request.NoiseW);

                            await channel.Writer.WriteAsync(chunkSamples, cancellationToken);
                        }
                        channel.Writer.Complete();
                    }, cancellationToken);

                    // CONSUMER: Tone Cloning -> Resampling -> Encoding (MP3/Opus) -> Network Buffer
                    var consumerTask = Task.Run(async () =>
                    {
                        await foreach (var chunkSamples in channel.Reader.ReadAllAsync(cancellationToken))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            float[] processedSamples = chunkSamples;

                            if (canClone && targetFingerprint != null && sourceFingerprint != null)
                            {
                                float[] resampledSamples = audioProc!.Resample(chunkSamples, piperConfig.Audio.SampleRate, outSampleRate);
                                var specChunk = audioProc.GetMagnitudeSpectrogram(resampledSamples);
                                if (specChunk.GetLength(0) > 0)
                                    processedSamples = openVoice!.ApplyToneColor(specChunk, sourceFingerprint, targetFingerprint);
                            }

                            if (outSampleRate != finalSampleRate)
                                processedSamples = audioProc!.Resample(processedSamples, outSampleRate, finalSampleRate);

                            streamManager.WriteChunk(processedSamples, filter);
                            if (filter != null) Array.Clear(absoluteSilence, 0, absoluteSilence.Length);
                            streamManager.WriteChunk(absoluteSilence, filter);

                            if (useStreaming && streamConfig.FlushAfterEachSentence)
                            {
                                targetStream.Flush();
                            }
                        }
                    }, cancellationToken);

                    // Await both stages of the generation pipeline
                    await Task.WhenAll(producerTask, consumerTask);

                    if (!useStreaming)
                    {
                        finalAudioBytes = streamManager.GetFinalAudioBytes();
                    }
                } // streamManager.Dispose() ensures MP3/Ogg footers are written

                // --- Finalize Network Stream ---
                if (useStreaming)
                {
                    try
                    {
                        targetStream.Flush(); // Push remaining encoder buffers
                    }
                    catch (ObjectDisposedException) { /* Encoder already closed the stream */ }

                    networkChannel?.Writer.Complete(); // Signal Courier to exit
                }
            }, cancellationToken);

            // --- Finalize HTTP Response ---
            if (useStreaming)
            {
                if (networkSenderTask != null) await networkSenderTask;
                return Results.Empty; // Data already sent via Response.Body
            }
            else
            {
                // Synchronous Mode: return full buffered file
                httpContext.Response.Headers.Append("X-Audio-Sample-Rate", displaySampleRate.ToString());
                return Results.File(finalAudioBytes!, AudioStreamManager.GetMimeType(request), AudioStreamManager.GetFileName(request));
            }
        }
        finally
        {
            gpuSemaphore.Release(); // Guarantee semaphore slot is freed
        }
    }
    catch (OperationCanceledException)
    {
        // Graceful exit when client disconnects (closes tab/aborts request)
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [INFO] Client disconnected. Generation stopped to save resources.");
        Console.ResetColor();
        return Results.Empty;
    }
    catch (Exception ex)
    {
        if (httpContext.Response.HasStarted)
        {
            // Failsafe for errors during an active stream (headers already sent)
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR] Stream aborted unexpectedly: {ex.Message}");
            Console.ResetColor();
            return Results.Empty;
        }

        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.WithName("GetSpeech")
.WithOpenApi()
.RequireRateLimiting("ip_limit");

// ЕНДПОІНТ 2: Отримання фонем
app.MapPost("/v1/audio/phonemize", async (
    [FromBody] PhonemizeRequest request,
    [FromServices] UnifiedPhonemizer unifiedPhonemizer) =>
{
    if (string.IsNullOrWhiteSpace(request.Input)) return Results.BadRequest(new { error = "Input text cannot be empty." });

    try
    {
        string phonemes = await Task.Run(() => unifiedPhonemizer.GetPhonemes(request.Input));
        return Results.Ok(new { text = request.Input, phonemes });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.WithName("GetPhonemes")
.WithOpenApi()
.RequireRateLimiting("ip_limit");

app.Run();