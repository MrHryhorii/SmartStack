using Microsoft.AspNetCore.Mvc;
using NAudio.Wave;
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
var phonemizerConfig = builder.Configuration.GetSection("PhonemizerSettings").Get<PhonemizerSettings>() ?? new PhonemizerSettings();
var chunkerConfig = builder.Configuration.GetSection("ChunkerSettings").Get<ChunkerSettings>() ?? new ChunkerSettings();
var hardwareConfig = builder.Configuration.GetSection("HardwareSettings").Get<HardwareSettings>() ?? new HardwareSettings();
var dspConfig = builder.Configuration.GetSection("DspSettings").Get<DspSettings>() ?? new DspSettings();

// --- РЕЄСТРАЦІЯ СЕРВІСІВ ---
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
// ЕНДПОІНТИ
// =================================================================

// ЕНДПОІНТ 1: Генерація голосу
app.MapPost("/v1/audio/speech", async (
    [FromBody] OpenAiSpeechRequest request,
    [FromServices] TextChunker textChunker,
    [FromServices] UnifiedPhonemizer unifiedPhonemizer,
    [FromServices] PiperRunner piperRunner,
    [FromServices] IServiceProvider services) =>
{
    if (string.IsNullOrWhiteSpace(request.Input)) return Results.BadRequest(new { error = "Input text cannot be empty." });
    if (piperConfig == null) return Results.Problem("Model is not loaded properly.", statusCode: 500);

    // --- СТРОГА ВАЛІДАЦІЯ ФОРМАТУ ---
    if (string.IsNullOrWhiteSpace(request.ResponseFormat) ||
       (!request.ResponseFormat.Equals("wav", StringComparison.OrdinalIgnoreCase) &&
        !request.ResponseFormat.Equals("mp3", StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new
        {
            error = $"Unsupported response_format: '{request.ResponseFormat}'. Supported formats are: wav, mp3."
        });
    }

    try
    {
        // СЕМАФОР: Захищає GPU від напливу десятків користувачів
        await gpuSemaphore.WaitAsync();

        try
        {
            var audioBytes = await Task.Run(async () =>
            {
                var textChunks = textChunker.Split(request.Input);

                bool useOpenVoice = !string.IsNullOrEmpty(request.Voice);
                var openVoice = services.GetService<OpenVoiceRunner>();
                var audioProc = services.GetService<AudioProcessor>();

                float[]? targetFingerprint = null;
                float[]? sourceFingerprint = null;

                bool canClone = useOpenVoice && openVoice != null && audioProc != null &&
                                openVoice.VoiceLibrary.TryGetValue(request.Voice, out targetFingerprint) &&
                                openVoice.VoiceLibrary.TryGetValue("piper_base", out sourceFingerprint);

                // ВИЗНАЧАЄМО ФІНАЛЬНУ ЧАСТОТУ (Для WAV та тиші)
                int outSampleRate = canClone ? openVoice!.GetTargetSamplingRate() : piperConfig.Audio.SampleRate;

                // ДИНАМІЧНА ТИША З ПРАВИЛЬНОЮ ЧАСТОТОЮ
                float currentSpeed = (request.Speed > 0.1f) ? request.Speed : 1.0f;
                float actualPauseSeconds = chunkerConfig.SentencePauseSeconds / currentSpeed;
                int silenceSamplesCount = (int)(outSampleRate * actualPauseSeconds);
                float[] absoluteSilence = new float[silenceSamplesCount];

                // ПІДГОТОВКА ФІЛЬТРА
                NAudio.Dsp.BiQuadFilter? filter = null;
                if (canClone && dspConfig.EnableLowPassFilter)
                {
                    filter = NAudio.Dsp.BiQuadFilter.LowPassFilter(
                        outSampleRate,
                        dspConfig.LowPassCutoffFrequency,
                        dspConfig.LowPassQFactor
                    );
                }

                // --- ІНІЦІАЛІЗУЄМО МЕНЕДЖЕР АУДІО ---
                // Він сам вирішить, чи це MP3, чи WAV, спираючись на request
                using var streamManager = new AudioStreamManager(request, outSampleRate);

                // Створюємо чергу для паралельної роботи (макс. 10 речень у пам'яті)
                var channel = System.Threading.Channels.Channel.CreateBounded<float[]>(10);

                // ==========================================
                // ПОТІК 1: ВИРОБНИК (Producer - Piper)
                // ==========================================
                // Завдання цього потоку: максимально швидко перетворювати текст на фонеми, 
                // генерувати сирий звук і закидати його в чергу (Channel). 
                // Він НЕ чекає на роботу OpenVoice або запис у файл.
                var producerTask = Task.Run(async () =>
                {
                    foreach (var chunk in textChunks)
                    {
                        string phonemes = unifiedPhonemizer.GetPhonemes(chunk);
                        float[] chunkSamples = piperRunner.SynthesizeAudioRaw(phonemes, request.Speed, request.NoiseScale, request.NoiseW);

                        // Відправляємо згенероване речення в чергу
                        await channel.Writer.WriteAsync(chunkSamples);
                    }
                    // Сигналізуємо Споживачу, що весь текст оброблено і нових даних не буде
                    channel.Writer.Complete();
                });

                // ==========================================
                // ПОТІК 2: СПОЖИВАЧ (Consumer - OpenVoice + Writer)
                // ==========================================
                // Завдання цього потоку: брати готові речення з черги по мірі їх появи,
                // накладати цільовий голос (клонування) і одразу конвертувати/писати у файл.
                // Працює паралельно з Виробником.
                var consumerTask = Task.Run(async () =>
                {
                    // Читаємо дані, поки Виробник не викличе Complete()
                    await foreach (var chunkSamples in channel.Reader.ReadAllAsync())
                    {
                        float[] processedSamples = chunkSamples;

                        // Якщо ввімкнено клонування голосу - трансформуємо
                        if (canClone)
                        {
                            float[] resampledSamples = audioProc!.Resample(chunkSamples, piperConfig.Audio.SampleRate, outSampleRate);
                            var specChunk = audioProc.GetMagnitudeSpectrogram(resampledSamples);

                            if (specChunk.GetLength(0) > 0)
                            {
                                processedSamples = openVoice!.ApplyToneColor(specChunk, sourceFingerprint!, targetFingerprint!);
                            }
                        }

                        // ПИШЕМО РЕЧЕННЯ ТА ТИШУ ЧЕРЕЗ МЕНЕДЖЕР
                        streamManager.WriteChunk(processedSamples, filter);
                        streamManager.WriteChunk(absoluteSilence, filter);
                    }
                });

                // Чекаємо завершення обох паралельних потоків
                await Task.WhenAll(producerTask, consumerTask);

                // Отримуємо готові байти з менеджера
                return streamManager.GetFinalAudioBytes();
            });

            // --- ПОВЕРТАЄМО ПРАВИЛЬНИЙ ФАЙЛ І MIME-ТИП ---
            // Використовуємо статичні методи без створення зайвих об'єктів
            string mimeType = AudioStreamManager.GetMimeType(request);
            string fileName = AudioStreamManager.GetFileName(request);

            return Results.File(audioBytes, mimeType, fileName);
        }
        finally
        {
            gpuSemaphore.Release();
        }
    }
    catch (Exception ex)
    {
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