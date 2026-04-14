using ONNX_Runner.Models;
using ONNX_Runner.Services;
using ONNX_Runner.Endpoints;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Додаємо підтримку Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Читаємо налаштування директорії ДО завантаження моделі
var modelConfig = builder.Configuration.GetSection("ModelSettings").Get<ModelSettings>() ?? new ModelSettings();

// --- ЗАВАНТАЖЕННЯ МОДЕЛІ ТА ЛОГУВАННЯ ---
string modelDirectory = modelConfig.ModelDirectory;
PiperConfig? piperConfig = null;
string? piperModelPath = null;

try
{
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
var clonerConfig = builder.Configuration.GetSection("ClonerSettings").Get<ClonerSettings>() ?? new ClonerSettings();

// --- РЕЄСТРАЦІЯ СЕРВІСІВ (DI) ---
builder.Services.AddSingleton(streamConfig);
builder.Services.AddSingleton(onnxConfig);
builder.Services.AddSingleton(effectsConfig);
builder.Services.AddSingleton(clonerConfig);
builder.Services.AddSingleton(hardwareConfig);
builder.Services.AddSingleton(chunkerConfig);
builder.Services.AddSingleton(dspConfig);

if (piperConfig != null && piperModelPath != null)
{
    builder.Services.AddSingleton(piperConfig); // Робимо конфіг глобальним

    var phonemizer = new PiperPhonemizer(piperConfig);
    builder.Services.AddSingleton<IPhonemizer>(phonemizer);

    var textChunker = new TextChunker(chunkerConfig);
    builder.Services.AddSingleton(textChunker);

    var runner = new PiperRunner(piperModelPath, piperConfig, phonemizer, onnxConfig);
    builder.Services.AddSingleton(runner);

    var punctuationMapper = new DynamicPunctuationMapper(piperConfig);
    builder.Services.AddSingleton(punctuationMapper);

    string dataPath = Path.GetFullPath("PiperNative");
    var mixedEspeak = new EspeakWrapper(dataPath, piperConfig.Espeak.Voice ?? "en");
    builder.Services.AddSingleton(mixedEspeak);

    // --- ПЕРЕВІРКА ТА ЗАВАНТАЖЕННЯ OPENVOICE (CLONER) ---
    string clonerDirectory = "Cloner";
    string voicesDirectory = "Voices";

    string extractPath = Path.Combine(clonerDirectory, "tone_extract.onnx");
    string colorPath = Path.Combine(clonerDirectory, "tone_color.onnx");
    string toneJsonPath = Path.Combine(clonerDirectory, "tone_config.json");

    if (!Directory.Exists(clonerDirectory))
    {
        Directory.CreateDirectory(clonerDirectory);
    }

    if (!File.Exists(extractPath) || !File.Exists(colorPath) || !File.Exists(toneJsonPath))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[INFO] Missing Voice Cloner models (OpenVoice) detected locally.");
        Console.WriteLine("[INFO] Initiating automatic download from Hugging Face...");
        Console.ResetColor();

        string baseUrl = "https://huggingface.co/Hinotsuba/OpenVoice-ONNX-v2/resolve/main/";
        string desc = "Voice Cloner";

        if (!File.Exists(extractPath))
            HuggingFaceDownloader.DownloadFileAsync(baseUrl + "tone_extract.onnx", extractPath, "tone_extract.onnx", desc).Wait();

        if (!File.Exists(colorPath))
            HuggingFaceDownloader.DownloadFileAsync(baseUrl + "tone_color.onnx", colorPath, "tone_color.onnx", desc).Wait();

        if (!File.Exists(toneJsonPath))
            HuggingFaceDownloader.DownloadFileAsync(baseUrl + "tone_config.json", toneJsonPath, "tone_config.json", desc).Wait();
    }

    if (File.Exists(extractPath) && File.Exists(colorPath) && File.Exists(toneJsonPath))
    {
        try
        {
            string toneJsonContent = File.ReadAllText(toneJsonPath);
            var toneConfig = System.Text.Json.JsonSerializer.Deserialize<ToneConfig>(toneJsonContent);

            if (toneConfig != null)
            {
                var openVoice = new OpenVoiceRunner(extractPath, colorPath, toneConfig, onnxConfig);
                var audioProc = new AudioProcessor(toneConfig);

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
                        var normalizedAudio = audioProc.LoadAndNormalizeWav(wavPath, targetRate);

                        if (normalizedAudio.Length == 0)
                        {
                            System.Buffers.ArrayPool<float>.Shared.Return(normalizedAudio.Buffer);
                            continue;
                        }

                        float[,] spec;
                        try
                        {
                            spec = audioProc.GetMagnitudeSpectrogram(normalizedAudio.Buffer.AsSpan(0, normalizedAudio.Length));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"   -> [ERROR] Spectrogram generation failed: {ex.Message}");
                            continue;
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<float>.Shared.Return(normalizedAudio.Buffer);
                        }

                        int frames = spec.GetLength(0);
                        if (frames == 0) continue;

                        var fingerprint = openVoice.ExtractToneColor(spec);
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
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to load OpenVoice/Voices: {ex.Message}");
        }
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("[ERROR] Voice Cloner models are missing. OpenVoice features will be unavailable.");
        Console.ResetColor();
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
        }

        fallbackMapper = new PhonemeFallbackMapper(phoiblePath, piperConfig);
        builder.Services.AddSingleton(fallbackMapper);

        mixedPhonemizer = new MixedLanguagePhonemizer(phonemizerConfig, piperConfig.Espeak.Voice ?? "en");
        builder.Services.AddSingleton(mixedPhonemizer);
    }

    var unifiedPhonemizer = new UnifiedPhonemizer(mixedEspeak, punctuationMapper, piperConfig, mixedPhonemizer, fallbackMapper);
    builder.Services.AddSingleton(unifiedPhonemizer);
}

// =================================================================
// НАЛАШТУВАННЯ CORS ТА RATE LIMITING
// =================================================================
builder.Services.AddCors(options =>
{
    options.AddPolicy("DynamicCorsPolicy", policy =>
    {
        if (corsConfig.AllowAnyOrigin)
        {
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("X-Audio-Sample-Rate", "Content-Disposition");
        }
        else
        {
            policy.WithOrigins(corsConfig.AllowedOrigins).AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("X-Audio-Sample-Rate", "Content-Disposition");
        }
    });
});

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
                QueueLimit = 0
            }));
});

// =================================================================
// ДИНАМІЧНА ЧЕРГА ЗАПИТІВ (РОЗУМНИЙ СЕМАФОР У DI)
// =================================================================
builder.Services.AddSingleton(sp =>
{
    var hwConfig = sp.GetRequiredService<HardwareSettings>();
    var piperSvc = sp.GetService<PiperRunner>();
    int cr = 1;

    if (piperSvc != null)
    {
        if (piperSvc.IsUsingGPU)
        {
            double totalVramGb = hwConfig.TotalVramGb;
            double vramPerRequest = hwConfig.VramPerRequestGb;
            cr = Math.Max(1, (int)(totalVramGb / vramPerRequest));
            Console.WriteLine($"[SYSTEM] Running on GPU ({totalVramGb}GB VRAM). Limit: {cr} tasks.");
        }
        else
        {
            int totalCores = Environment.ProcessorCount;
            double cpuMultiplier = Math.Clamp(hwConfig.CpuCoresUsageMultiplier, 0.1, 1.0);
            cr = Math.Max(1, (int)(totalCores * cpuMultiplier));
            Console.WriteLine($"[SYSTEM] Running on CPU ({totalCores} cores). Limit: {cr} tasks.");
        }
    }
    return new SemaphoreSlim(cr, cr);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("DynamicCorsPolicy");
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
    var pipConfig = scope.ServiceProvider.GetService<PiperConfig>();

    if (openVoiceSvc != null && audioProcSvc != null && unifiedPhonemizerSvc != null && piperRunnerSvc != null && pipConfig != null)
    {
        var baseGenerator = new BaseVoiceGenerator(unifiedPhonemizerSvc, piperRunnerSvc, audioProcSvc, openVoiceSvc, pipConfig);
        baseGenerator.GenerateAndCacheBaseFingerprint();
        openVoiceSvc.UnloadExtractor();
    }
}

// =================================================================
// ENDPOINTS
// =================================================================

app.MapPost("/v1/audio/speech", SpeechEndpoint.HandleSpeechRequest)
   .WithName("GetSpeech")
   .WithOpenApi()
   .RequireRateLimiting("ip_limit");

app.MapPost("/v1/audio/phonemize", PhonemizeEndpoint.HandlePhonemizeRequest)
   .WithName("GetPhonemes")
   .WithOpenApi()
   .RequireRateLimiting("ip_limit");

app.Run();