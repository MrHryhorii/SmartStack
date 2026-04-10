using Microsoft.AspNetCore.Mvc;
using ONNX_Runner.Models;
using ONNX_Runner.Services;
using System.Buffers;
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
var onnxConfig = builder.Configuration.GetSection("OnnxSettings").Get<OnnxSettings>() ?? new OnnxSettings();
var effectsConfig = builder.Configuration.GetSection("EffectsSettings").Get<EffectsSettings>() ?? new EffectsSettings();

// --- РЕЄСТРАЦІЯ СЕРВІСІВ ---
builder.Services.AddSingleton(streamConfig);
builder.Services.AddSingleton(onnxConfig);
builder.Services.AddSingleton(effectsConfig);

if (piperConfig != null && piperModelPath != null)
{
    var phonemizer = new PiperPhonemizer(piperConfig);
    builder.Services.AddSingleton<IPhonemizer>(phonemizer);

    // Реєструємо чанкер для розбиття довгих рядків із параметрами з конфігу
    var textChunker = new TextChunker(chunkerConfig);
    builder.Services.AddSingleton(textChunker);

    var runner = new PiperRunner(piperModelPath, piperConfig, phonemizer, onnxConfig);
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
                    var openVoice = new OpenVoiceRunner(extractPath, colorPath, toneConfig, onnxConfig);
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
                            Console.ResetColor();

                            int targetRate = toneConfig.Data.SamplingRate;

                            // Читаємо, зводимо в моно і ресемплимо прямо в орендований масив!
                            var normalizedAudio = audioProc.LoadAndNormalizeWav(wavPath, targetRate);
                            Console.WriteLine($"   -> Step 1 & 2: Loaded and normalized to {targetRate}Hz (Mono). Samples count: {normalizedAudio.Length}");

                            if (normalizedAudio.Length == 0)
                            {
                                ArrayPool<float>.Shared.Return(normalizedAudio.Buffer);
                                continue;
                            }

                            float[,] spec;
                            try
                            {
                                // Передаємо тільки корисну частину орендованого масиву
                                spec = audioProc.GetMagnitudeSpectrogram(normalizedAudio.Buffer.AsSpan(0, normalizedAudio.Length));
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"   -> [ERROR] Spectrogram generation failed: {ex.Message}");
                                continue;
                            }
                            finally
                            {
                                // Повертаємо масив з аудіо-даними у пул (GC відпочиває)
                                ArrayPool<float>.Shared.Return(normalizedAudio.Buffer);
                            }

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

// Helper method for applying frequency filters (EQ) to effects
void ApplyEffectEq(Span<float> buffer, NAudio.Dsp.BiQuadFilter? hp, NAudio.Dsp.BiQuadFilter? lp)
{
    if (hp == null && lp == null) return;

    for (int i = 0; i < buffer.Length; i++)
    {
        float sample = buffer[i];
        if (hp != null) sample = hp.Transform(sample);
        if (lp != null) sample = lp.Transform(sample);
        buffer[i] = sample;
    }
}

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

                // Створюємо ізольований двигун ефектів ТІЛЬКИ для цього запиту!
                var effectsEngine = new AudioEffectsEngine(effectsConfig, finalSampleRate);

                // --- ARTISTIC FILTERS (EQ) SETUP ---
                NAudio.Dsp.BiQuadFilter? effectHighPass = null;
                NAudio.Dsp.BiQuadFilter? effectLowPass = null;

                if (Enum.TryParse(request.Effect, true, out VoiceEffectType parsedEffect))
                {
                    // Calculate physical limit (Nyquist limit minus 2% for mathematical safety margin)
                    float nyquistLimit = finalSampleRate / 2.0f * 0.98f;
                    // Get administrative limit from appsettings (DSP config)
                    float dspLimit = (float)dspConfig.LowPassCutoffFrequency;
                    // Absolute ceiling is the strictest of both limits to prevent NaN crashes
                    float absoluteMaxFreq = Math.Min(nyquistLimit, dspLimit);
                    // Local safety function: ensures target frequency NEVER exceeds the absolute max
                    float SafeFreq(float targetFreq) => Math.Min(targetFreq, absoluteMaxFreq);

                    switch (parsedEffect)
                    {
                        case VoiceEffectType.Telephone:
                            // Mimics G.711 telecom standard. 
                            // 300Hz-3400Hz is the exact bandwidth of classic phone lines.
                            // Q=1.2 adds a slight resonance mimicking a cheap plastic earpiece.
                            effectHighPass = NAudio.Dsp.BiQuadFilter.HighPassFilter(finalSampleRate, Math.Min(300f, absoluteMaxFreq - 100f), 1.2f);
                            effectLowPass = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, SafeFreq(3400f), 1.2f);
                            break;

                        case VoiceEffectType.VintageRadio:
                            // Mimics old paper cone speakers inside a wooden cabinet.
                            // Bandwidth is slightly narrower than telephone (400Hz-3500Hz).
                            // Q=1.5 emphasizes the cutoff frequencies, creating a classic "boxy" sound.
                            effectHighPass = NAudio.Dsp.BiQuadFilter.HighPassFilter(finalSampleRate, Math.Min(400f, absoluteMaxFreq - 100f), 1.5f);
                            effectLowPass = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, SafeFreq(3500f), 1.5f);
                            break;

                        case VoiceEffectType.Megaphone:
                            // Mimics a metallic horn. 
                            // Horns project mid-frequencies (500Hz-3000Hz) extremely well but lack bass/treble.
                            // Extreme Q=2.0 creates a sharp, piercing resonance at the edges.
                            effectHighPass = NAudio.Dsp.BiQuadFilter.HighPassFilter(finalSampleRate, Math.Min(500f, absoluteMaxFreq - 100f), 2.0f);
                            effectLowPass = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, SafeFreq(3000f), 2.0f);
                            break;

                        case VoiceEffectType.Overdrive:
                            // Walkie-Talkie / Military radio effect.
                            // Heavily restricted (600Hz-2800Hz) to cut through battlefield noise.
                            effectHighPass = NAudio.Dsp.BiQuadFilter.HighPassFilter(finalSampleRate, Math.Min(600f, absoluteMaxFreq - 100f), 1.5f);
                            effectLowPass = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, SafeFreq(2800f), 1.5f);
                            break;

                        case VoiceEffectType.VinylRecord:
                            // Analog turntables struggle with extreme sub-bass (causes needle skipping).
                            // HighPass at 80Hz removes turntable motor rumble. 
                            // LowPass at 12000Hz rolls off digital harshness for analog "warmth".
                            effectHighPass = NAudio.Dsp.BiQuadFilter.HighPassFilter(finalSampleRate, 80f, 0.707f);
                            effectLowPass = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, SafeFreq(12000f), 0.5f);
                            break;

                        case VoiceEffectType.Arcade:
                            // Mimics a tiny 8-bit console piezo speaker (e.g., GameBoy).
                            // No bass (300Hz cut) and muffled highs (4000Hz cut) to emphasize the 3-bit crush.
                            effectHighPass = NAudio.Dsp.BiQuadFilter.HighPassFilter(finalSampleRate, Math.Min(300f, absoluteMaxFreq - 100f), 1.0f);
                            effectLowPass = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, SafeFreq(4000f), 1.0f);
                            break;

                        case VoiceEffectType.Whisper:
                            // Whispering relies on air (high frequencies) and lacks vocal cord engagement (low frequencies).
                            // HighPass at 250Hz removes the "chest boom" to make it sound purely breathy.
                            effectHighPass = NAudio.Dsp.BiQuadFilter.HighPassFilter(finalSampleRate, 250f, 0.707f);
                            break;

                        case VoiceEffectType.Robot:
                            // Foldback distortion generates extreme high-frequency artifacts (aliasing).
                            // LowPass at 6000Hz tames the digital harshness. 
                            // HighPass at 150Hz removes "mud" to keep the metallic voice clear.
                            effectHighPass = NAudio.Dsp.BiQuadFilter.HighPassFilter(finalSampleRate, 150f, 0.707f);
                            effectLowPass = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, SafeFreq(6000f), 0.707f);
                            break;

                        case VoiceEffectType.Alien:
                            // Sine phase-distortion multiplies harmonics infinitely.
                            // A strict LowPass at 8000Hz keeps the spacey/watery effect but prevents ear-bleeding highs.
                            effectLowPass = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, SafeFreq(8000f), 0.707f);
                            break;
                    }
                }

                float currentSpeed = (request.Speed > 0.1f) ? request.Speed : 1.0f;
                int silenceSamplesCount = (int)(finalSampleRate * (chunkerConfig.SentencePauseSeconds / currentSpeed));
                float[] absoluteSilence = new float[silenceSamplesCount];

                NAudio.Dsp.BiQuadFilter? filter = null;
                if (canClone && dspConfig.EnableLowPassFilter)
                    filter = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, dspConfig.LowPassCutoffFrequency, dspConfig.LowPassQFactor);

                using (var streamManager = new AudioStreamManager(request, finalSampleRate, targetStream))
                {
                    var channel = System.Threading.Channels.Channel.CreateBounded<(float[] Buffer, int Length)>(10);

                    // PRODUCER: Generates audio (rents arrays)
                    var producerTask = Task.Run(async () =>
                    {
                        foreach (var chunk in textChunks)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string phonemes = unifiedPhonemizer.GetPhonemes(chunk);
                            // SynthesizeAudioRaw returns (Buffer, Length)
                            var rawResult = piperRunner.SynthesizeAudioRaw(phonemes, request.Speed, request.NoiseScale, request.NoiseW);

                            await channel.Writer.WriteAsync(rawResult, cancellationToken);
                        }
                        channel.Writer.Complete();
                    }, cancellationToken);

                    // CONSUMER: Processes audio and safely returns arrays to the pool
                    var consumerTask = Task.Run(async () =>
                    {
                        await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            float[] currentBuffer = chunk.Buffer;
                            int currentLength = chunk.Length;

                            float[]? rentedBuffer1 = null;
                            float[]? rentedBuffer2 = null;
                            float[]? rentedBuffer3 = null;

                            try
                            {
                                if (canClone && targetFingerprint != null && sourceFingerprint != null)
                                {
                                    var r1 = audioProc!.Resample(currentBuffer, currentLength, piperConfig.Audio.SampleRate, outSampleRate);
                                    rentedBuffer1 = r1.Buffer;

                                    var specChunk = audioProc.GetMagnitudeSpectrogram(rentedBuffer1.AsSpan(0, r1.Length));
                                    if (specChunk.GetLength(0) > 0)
                                    {
                                        var rClone = openVoice!.ApplyToneColor(specChunk, sourceFingerprint, targetFingerprint);
                                        rentedBuffer3 = rClone.Buffer;

                                        currentBuffer = rentedBuffer3;
                                        currentLength = rClone.Length;
                                    }
                                    else
                                    {
                                        currentBuffer = rentedBuffer1;
                                        currentLength = r1.Length;
                                    }
                                }

                                if (outSampleRate != finalSampleRate)
                                {
                                    var r2 = audioProc!.Resample(currentBuffer, currentLength, outSampleRate, finalSampleRate);
                                    rentedBuffer2 = r2.Buffer;
                                    currentBuffer = rentedBuffer2;
                                    currentLength = r2.Length;
                                }

                                // ====== EFFECT FOR VOICE ======
                                // Apply hardware frequency constraints (EQ)
                                ApplyEffectEq(currentBuffer.AsSpan(0, currentLength), effectHighPass, effectLowPass);

                                // Apply saturation, noise, and digital distortions
                                effectsEngine.ApplyEffect(
                                    currentBuffer.AsSpan(0, currentLength),
                                    request.Effect,
                                    request.EffectIntensity
                                );

                                streamManager.WriteChunk(currentBuffer.AsSpan(0, currentLength), filter);

                                // ====== EFFECT FOR PAUSE (SILENCE) ======
                                // Always clear the silence array to reset any accumulated noise
                                Array.Clear(absoluteSilence, 0, absoluteSilence.Length);

                                // Apply EQ to silence so the background noise is also frequency-limited
                                ApplyEffectEq(absoluteSilence.AsSpan(), effectHighPass, effectLowPass);

                                // Apply the mathematical effect to absolute zero to generate matching background noise
                                effectsEngine.ApplyEffect(
                                    absoluteSilence.AsSpan(),
                                    request.Effect,
                                    request.EffectIntensity
                                );

                                streamManager.WriteChunk(absoluteSilence.AsSpan(), filter);

                                if (useStreaming && streamConfig.FlushAfterEachSentence)
                                {
                                    targetStream.Flush();
                                }
                            }
                            finally
                            {
                                // --- CLEANUP ---
                                ArrayPool<float>.Shared.Return(chunk.Buffer);

                                if (rentedBuffer1 != null) ArrayPool<float>.Shared.Return(rentedBuffer1);
                                if (rentedBuffer2 != null) ArrayPool<float>.Shared.Return(rentedBuffer2);
                                if (rentedBuffer3 != null) ArrayPool<float>.Shared.Return(rentedBuffer3);
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