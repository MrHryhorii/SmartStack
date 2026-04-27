using ONNX_Runner.Models;
using ONNX_Runner.Services;
using ONNX_Runner.Endpoints;
using System.Threading.RateLimiting;

// Set console output encoding to UTF-8 to properly display international characters and phonemes in logs and diagnostics.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);


// =================================================================
// LINUX SELF-HEALING (NAudio.Lame Fix)
// =================================================================
// NAudio.Lame expects a Windows DLL path. On bare-metal Linux, we automatically 
// create the required symlink so the sysadmin doesn't have to do it manually.
if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
{
    try
    {
        string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes", "linux-x64", "native");
        string symlinkPath = Path.Combine(targetDir, "libmp3lame.64.dll");

        if (!File.Exists(symlinkPath))
        {
            Directory.CreateDirectory(targetDir);

            // Common locations for LAME on Ubuntu/Debian and CentOS/RHEL
            string[] possibleSystemLibs = [
                "/usr/lib/x86_64-linux-gnu/libmp3lame.so.0",
                "/usr/lib64/libmp3lame.so.0",
                "/usr/lib/libmp3lame.so.0"
            ];

            string? validSystemLib = possibleSystemLibs.FirstOrDefault(File.Exists);

            if (validSystemLib != null)
            {
                File.CreateSymbolicLink(symlinkPath, validSystemLib);
                Console.WriteLine($"[SYSTEM] Auto-created symlink for LAME MP3 Encoder: {symlinkPath} -> {validSystemLib}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[WARNING] libmp3lame.so.0 not found on the system. MP3 streaming may fail. Please run: apt-get install libmp3lame0");
                Console.ResetColor();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARNING] Failed to auto-heal LAME library link: {ex.Message}");
    }
}


// Add Swagger support for API documentation and easy testing
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Read the model directory configuration BEFORE attempting to load the model.
// This prevents CrashLoopBackOff in Docker if the folder doesn't exist yet.
var modelConfig = builder.Configuration.GetSection("ModelSettings").Get<ModelSettings>() ?? new ModelSettings();

// =================================================================
// MODEL LOADING & LOGGING
// =================================================================
string modelDirectory = modelConfig.ModelDirectory;
PiperConfig? piperConfig = null;
string? piperModelPath = null;

try
{
    // Graceful initialization: create directory if missing and warn the user,
    // allowing the server to start without crashing.
    if (!Directory.Exists(modelDirectory))
    {
        Directory.CreateDirectory(modelDirectory);
        Console.WriteLine($"[WARNING] Directory '{modelDirectory}' was created. Please put your .onnx and .json files there.");
    }
    else
    {
        var (onnxPath, config) = ModelLoader.LoadFromDirectory(modelConfig);
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

// Read configuration sections from appsettings.json
var apiConfig = builder.Configuration.GetSection("ApiSettings").Get<ApiSettings>() ?? new ApiSettings();
var corsConfig = builder.Configuration.GetSection("CorsSettings").Get<CorsSettings>() ?? new CorsSettings();
var phonemizerConfig = builder.Configuration.GetSection("PhonemizerSettings").Get<PhonemizerSettings>() ?? new PhonemizerSettings();
var chunkerConfig = builder.Configuration.GetSection("ChunkerSettings").Get<ChunkerSettings>() ?? new ChunkerSettings();
var hardwareConfig = builder.Configuration.GetSection("HardwareSettings").Get<HardwareSettings>() ?? new HardwareSettings();
var dspConfig = builder.Configuration.GetSection("DspSettings").Get<DspSettings>() ?? new DspSettings();
var streamConfig = builder.Configuration.GetSection("StreamSettings").Get<StreamSettings>() ?? new StreamSettings();
var onnxConfig = builder.Configuration.GetSection("OnnxSettings").Get<OnnxSettings>() ?? new OnnxSettings();
var effectsConfig = builder.Configuration.GetSection("EffectsSettings").Get<EffectsSettings>() ?? new EffectsSettings();
var clonerConfig = builder.Configuration.GetSection("ClonerSettings").Get<ClonerSettings>() ?? new ClonerSettings();
var rateLimitConfig = builder.Configuration.GetSection("RateLimitSettings").Get<RateLimitSettings>() ?? new RateLimitSettings();

// =================================================================
// SERVICE REGISTRATION (Dependency Injection)
// =================================================================
// Registering settings as Singletons so they can be injected into any service or endpoint.
builder.Services.AddSingleton(apiConfig);
builder.Services.AddSingleton(streamConfig);
builder.Services.AddSingleton(onnxConfig);
builder.Services.AddSingleton(effectsConfig);
builder.Services.AddSingleton(clonerConfig);
builder.Services.AddSingleton(hardwareConfig);
builder.Services.AddSingleton(chunkerConfig);
builder.Services.AddSingleton(dspConfig);
builder.Services.AddSingleton(rateLimitConfig);

// Only wire up the heavy services if the base Piper model was successfully loaded
if (piperConfig != null && piperModelPath != null)
{
    builder.Services.AddSingleton(piperConfig); // Make Piper config globally available

    var phonemizer = new PiperPhonemizer(piperConfig);
    builder.Services.AddSingleton<IPhonemizer>(phonemizer);

    var textChunker = new TextChunker(chunkerConfig);
    builder.Services.AddSingleton(textChunker);

    var runner = new PiperRunner(piperModelPath, piperConfig, phonemizer, onnxConfig);
    builder.Services.AddSingleton(runner);

    var punctuationMapper = new DynamicPunctuationMapper(piperConfig);
    builder.Services.AddSingleton(punctuationMapper);

    string dataPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "PiperNative"));
    var mixedEspeak = new EspeakWrapper(dataPath, piperConfig.Espeak.Voice ?? "en");
    builder.Services.AddSingleton(mixedEspeak);

    // =================================================================
    // OPENVOICE (CLONER) CHECK & AUTO-DOWNLOAD
    // =================================================================
    string clonerDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Cloner"));
    string voicesDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Voices"));

    string extractPath = Path.Combine(clonerDirectory, "tone_extract.onnx");
    string colorPath = Path.Combine(clonerDirectory, "tone_color.onnx");
    string toneJsonPath = Path.Combine(clonerDirectory, "tone_config.json");

    if (!Directory.Exists(clonerDirectory))
    {
        Directory.CreateDirectory(clonerDirectory);
    }

    // Auto-fetch missing OpenVoice models from Hugging Face
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

    // If models are present, load them into memory and process cached voices
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

                // Iterate through all .wav files in the voices directory to build the voice library
                var wavFiles = Directory.GetFiles(voicesDirectory, "*.wav");
                foreach (var wavPath in wavFiles)
                {
                    string voiceName = Path.GetFileNameWithoutExtension(wavPath);
                    string fingerprintPath = Path.Combine(voicesDirectory, voiceName + ".voice");

                    // Load pre-computed voice fingerprint if it exists to save startup time
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
                        // Extract new fingerprint from WAV file using the Tone Extractor model
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
                            // Always return rented arrays to the shared pool to prevent memory leaks
                            System.Buffers.ArrayPool<float>.Shared.Return(normalizedAudio.Buffer);
                        }

                        int frames = spec.GetLength(0);
                        if (frames == 0) continue;

                        var fingerprint = openVoice.ExtractToneColor(spec);
                        openVoice.SaveVoiceFingerprint(fingerprintPath, fingerprint);
                        openVoice.VoiceLibrary[voiceName] = fingerprint;

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"   [SUCCESS] Fingerprint saved: {voiceName}.voice");
                        Console.ResetColor();
                    }
                }

                // Register cloner services
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
        // Graceful degradation: The server will still run, but voice cloning will be disabled
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("[ERROR] Voice Cloner models are missing. OpenVoice features will be unavailable.");
        Console.ResetColor();
    }

    // =================================================================
    // PHONEMIZER & LANGUAGE DETECTION SETUP
    // =================================================================
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
// CORS & RATE LIMITING SETUP
// =================================================================
// Dynamic CORS allows integration with web frontends (e.g., React/Vue)
// Exposing custom headers is required for the client to read audio metadata.
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

// Protect the API from spam and DDoS attacks using IP-based limits
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ip_limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                // Use values from configuration, allowing dynamic tuning without code changes
                PermitLimit = rateLimitConfig.PermitLimit,
                Window = TimeSpan.FromSeconds(rateLimitConfig.WindowSeconds),
                QueueLimit = rateLimitConfig.QueueLimit
            }));
});

// =================================================================
// DYNAMIC REQUEST QUEUE (SMART SEMAPHORE IN DI)
// =================================================================
// Calculates the maximum number of concurrent generation tasks based on available hardware.
// Prevents Out-Of-Memory (OOM) errors on GPUs and avoids heavy thread-blocking on CPUs.
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
            Console.WriteLine($"[SYSTEM] Running on GPU ({totalVramGb}GB VRAM). Limit: {cr} concurrent tasks.");
        }
        else
        {
            int totalCores = Environment.ProcessorCount;
            double cpuMultiplier = Math.Clamp(hwConfig.CpuCoresUsageMultiplier, 0.1, 1.0);
            cr = Math.Max(1, (int)(totalCores * cpuMultiplier));
            Console.WriteLine($"[SYSTEM] Running on CPU ({totalCores} cores). Limit: {cr} concurrent tasks.");
        }
    }
    return new SemaphoreSlim(cr, cr);
});

var app = builder.Build();

// =================================================================
// ENABLE STATIC FILES (WEB UI HOSTING)
// =================================================================
// These middlewares allow ASP.NET to serve index.html from the 'wwwroot' folder
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("DynamicCorsPolicy");
app.UseRateLimiter();

// =================================================================
// AUTOMATIC BASE FINGERPRINT GENERATION
// =================================================================
// We use a temporary scope to generate the base Piper voice fingerprint at startup.
// Once generated and cached, the heavy Tone Extractor model is unloaded from memory to free up VRAM/RAM.
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

        // Free up memory since extraction is only needed once at startup
        openVoiceSvc.UnloadExtractor();
    }
}

// =================================================================
// API ENDPOINTS
// =================================================================

app.MapPost("/v1/audio/speech", SpeechEndpoint.HandleSpeechRequest)
   .WithName("GetSpeech")
   .WithOpenApi()
   .RequireRateLimiting("ip_limit");

app.MapPost("/v1/audio/phonemize", PhonemizeEndpoint.HandlePhonemizeRequest)
   .WithName("GetPhonemes")
   .WithOpenApi()
   .RequireRateLimiting("ip_limit");

// With limited access for security,
// these endpoints are designed for local dashboard integration and should not be exposed publicly.
app.MapGet("/v1/audio/voices", InfoEndpoints.GetVoices)
   .WithName("GetVoices")
   .WithOpenApi();
//.AddEndpointFilter<LocalHostOnlyFilter>();

app.MapGet("/v1/audio/effects", InfoEndpoints.GetEffects)
   .WithName("GetEffects")
   .WithOpenApi();
//.AddEndpointFilter<LocalHostOnlyFilter>();

app.MapGet("/v1/audio/environments", InfoEndpoints.GetEnvironments)
   .WithName("GetEnvironments")
   .WithOpenApi();
//.AddEndpointFilter<LocalHostOnlyFilter>();

// =================================================================
// AUTO-OPEN BROWSER (LOCAL DASHBOARD)
// =================================================================
// Listen for the application started event to open the dashboard automatically
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        // Retrieve the local URL where the server is listening (e.g., http://localhost:5045)
        string? url = app.Urls.FirstOrDefault(u => u.StartsWith("http://"));

        // If a valid URL is found, attempt to open the default web browser
        if (!string.IsNullOrEmpty(url))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[SYSTEM] Launching Tsubaki Dashboard in default browser: {url}");
            Console.ResetColor();

            // Cross-platform browser launch logic
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            {
                System.Diagnostics.Process.Start("xdg-open", url);
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                System.Diagnostics.Process.Start("open", url);
            }
        }
    }
    catch (Exception ex)
    {
        // Graceful degradation: If running in a headless environment (e.g., Docker, Linux server),
        // we just log a warning instead of crashing the application.
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WARNING] Could not auto-open browser (headless environment?): {ex.Message}");
        Console.ResetColor();
    }
});

app.Run();