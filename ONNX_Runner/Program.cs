using ONNX_Runner.Models;
using ONNX_Runner.Services;
using ONNX_Runner.Endpoints;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging.Console;

// Set console output encoding to UTF-8 to properly display international characters and phonemes in logs and diagnostics.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = "clean");
builder.Logging.AddConsoleFormatter<CleanConsoleFormatter, ConsoleFormatterOptions>();

// PiperRunner, OpenVoiceRunner, and PiperPhonemizer are constructed directly with 'new'
// below, before the DI container exists, so they can't resolve ILogger<T> the normal
// way yet. This factory is a small bootstrap bridge: same console provider the rest of
// the app gets by default, just available a few lines earlier than DI normally allows.
using var bootstrapLoggerFactory = LoggerFactory.Create(lb =>
{
    lb.AddConfiguration(builder.Configuration.GetSection("Logging"));

    lb.AddConsole(options => options.FormatterName = "clean");
    lb.AddConsoleFormatter<CleanConsoleFormatter, ConsoleFormatterOptions>();
});

/// =================================================================
// LINUX SELF-HEALING
// =================================================================
// This block addresses common issues with native library loading on Linux, especially in containerized environments.
// For the LAME MP3 encoder, if the Windows DLL is accidentally copied during publish, it will cause an ELF header error. 
// We check for this and remove it if found.
if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
{
    try
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // --- LAME MP3 ENCODER FIX ---
        string lameSymlinkPath = Path.Combine(baseDir, "libmp3lame.64.dll");

        // Remove the Windows DLL if it was copied during publish
        if (File.Exists(lameSymlinkPath) && !File.GetAttributes(lameSymlinkPath).HasFlag(FileAttributes.ReparsePoint))
        {
            File.Delete(lameSymlinkPath);
            Console.WriteLine("[SYSTEM] Removed Windows-specific LAME DLL to prevent ELF header errors.");
        }

        // Link to the real Linux LAME library
        if (!File.Exists(lameSymlinkPath))
        {
            string[] possibleLame = ["/usr/lib/x86_64-linux-gnu/libmp3lame.so.0", "/usr/lib64/libmp3lame.so.0", "/usr/lib/libmp3lame.so.0"];
            string? validLame = possibleLame.FirstOrDefault(File.Exists);
            if (validLame != null)
            {
                File.CreateSymbolicLink(lameSymlinkPath, validLame);
                Console.WriteLine($"[SYSTEM] Auto-linked LAME: {lameSymlinkPath} -> {validLame}");
            }
        }

        // --- ESPEAK-NG FIX ---
        // P/Invoke looks for 'libespeak-ng.so', but Linux usually only has 'libespeak-ng.so.1'
        string espeakSymlinkPath = Path.Combine(baseDir, "libespeak-ng.so");

        if (!File.Exists(espeakSymlinkPath))
        {
            string[] possibleEspeak = ["/usr/lib/x86_64-linux-gnu/libespeak-ng.so.1", "/usr/lib64/libespeak-ng.so.1", "/usr/lib/libespeak-ng.so.1"];
            string? validEspeak = possibleEspeak.FirstOrDefault(File.Exists);
            if (validEspeak != null)
            {
                File.CreateSymbolicLink(espeakSymlinkPath, validEspeak);
                Console.WriteLine($"[SYSTEM] Auto-linked Espeak-ng: {espeakSymlinkPath} -> {validEspeak}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARNING] Failed to auto-heal Linux libraries: {ex.Message}");
    }
}


// Add Swagger support for API documentation and easy testing
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

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

    var phonemizer = new PiperPhonemizer(piperConfig, bootstrapLoggerFactory.CreateLogger<PiperPhonemizer>());
    builder.Services.AddSingleton<IPhonemizer>(phonemizer);

    var textChunker = new TextChunker(chunkerConfig);
    builder.Services.AddSingleton(textChunker);

    var runner = new PiperRunner(piperModelPath, piperConfig, phonemizer, onnxConfig, hardwareConfig, bootstrapLoggerFactory.CreateLogger<PiperRunner>());
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
            await HuggingFaceDownloader.DownloadFileAsync(baseUrl + "tone_extract.onnx", extractPath, "tone_extract.onnx", desc);

        if (!File.Exists(colorPath))
            await HuggingFaceDownloader.DownloadFileAsync(baseUrl + "tone_color.onnx", colorPath, "tone_color.onnx", desc);

        if (!File.Exists(toneJsonPath))
            await HuggingFaceDownloader.DownloadFileAsync(baseUrl + "tone_config.json", toneJsonPath, "tone_config.json", desc);
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
                var openVoice = new OpenVoiceRunner(extractPath, colorPath, toneConfig, onnxConfig, hardwareConfig, bootstrapLoggerFactory.CreateLogger<OpenVoiceRunner>());
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

        mixedPhonemizer = new MixedLanguagePhonemizer(
            phonemizerConfig, 
            piperConfig.Espeak.Voice ?? "en", 
            bootstrapLoggerFactory.CreateLogger<MixedLanguagePhonemizer>()
        );
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
            // GPU: Combines the engine's technical concurrency limit (Capacity) 
            // with the user-defined concurrency limit from config (Policy) to prevent OOM.
            cr = Math.Min(piperSvc.ConcurrencyCapacity, Math.Max(1, hwConfig.MaxConcurrentGpuRequests));
            Console.WriteLine($"[SYSTEM] Running on GPU. Explicit Limit applied: {cr} concurrent tasks.");
        }
        else
        {
            // CPU
            int totalCores = Environment.ProcessorCount;
            int requestedCpuLimit = hwConfig.MaxConcurrentCpuRequests;

            if (requestedCpuLimit <= 0)
            {
                // Negative value protection and default behavior for 0
                cr = Math.Min(piperSvc.ConcurrencyCapacity, totalCores);
                Console.WriteLine($"[SYSTEM] Running on CPU ({totalCores} cores). Auto-Limit applied: {cr} concurrent tasks.");
            }
            else
            {
                // Protection against entering a number greater than the number of physical cores
                cr = Math.Min(piperSvc.ConcurrencyCapacity, Math.Clamp(requestedCpuLimit, 1, totalCores));
                Console.WriteLine($"[SYSTEM] Running on CPU ({totalCores} cores). Custom Limit applied: {cr} concurrent tasks.");
            }
        }
    }
    return new SemaphoreSlim(cr, cr);
});

// SpeechSynthesisService is the single shared entry point for every TTS-producing endpoint
// (OpenAI-compatible, Tsubaki's extended one, and any future ones). It's safe to register
// unconditionally here, even if the model failed to load above: it only resolves PiperRunner
// and friends lazily, inside SynthesizeAsync, at request time — the same graceful "model not
// loaded" 500 response that already existed continues to work exactly as before.
builder.Services.AddSingleton<SpeechSynthesisService>();

var app = builder.Build();

// =================================================================
// ENABLE STATIC FILES (WEB UI HOSTING)
// =================================================================
// These middlewares allow ASP.NET to serve index.html from the 'wwwroot' folder
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    // Serve the OpenAPI spec at /openapi/v1.json instead of the default /swagger/v1/swagger.json 
    // for better consistency with our API versioning and cleaner URLs.
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        // Point Swagger UI to the custom OpenAPI endpoint that we serve, 
        // which is more intuitive and consistent with our API versioning.
        options.SwaggerEndpoint("/openapi/v1.json", "Tsubaki API v1");
    });
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

        // Pre-warm the color converter with the base fingerprint to reduce latency on the first cloning request
        openVoiceSvc.WarmUpColorConverter();
    }
}

// =================================================================
// API ENDPOINTS
// =================================================================

app.MapPost("/v1/audio/speech", SpeechEndpoint.HandleOpenAiRequest)
   .WithName("GetSpeech")
   .RequireRateLimiting("ip_limit");

app.MapPost("/tsbk/audio/speech", SpeechEndpoint.HandleTsubakiRequest)
   .WithName("GetSpeechExtended")
   .RequireRateLimiting("ip_limit");

app.MapPost("/tsbk/audio/phonemize", PhonemizeEndpoint.HandlePhonemizeRequest)
   .WithName("GetPhonemes")
   .RequireRateLimiting("ip_limit");

app.MapGet("/health", InfoEndpoints.GetHealth)
   .WithName("GetHealth");

// With limited access for security,
// these endpoints are designed for local dashboard integration and should not be exposed publicly.
app.MapGet("/tsbk/audio/voices", InfoEndpoints.GetVoices)
   .WithName("GetVoices");
//.AddEndpointFilter<LocalHostOnlyFilter>();

app.MapGet("/tsbk/audio/effects", InfoEndpoints.GetEffects)
   .WithName("GetEffects");
//.AddEndpointFilter<LocalHostOnlyFilter>();

app.MapGet("/tsbk/audio/environments", InfoEndpoints.GetEnvironments)
   .WithName("GetEnvironments");
//.AddEndpointFilter<LocalHostOnlyFilter>();

// Endpoints to imitate OpenAI's model listing for better compatibility with existing tools and dashboards that expect this format.
app.MapGet("/v1/models", InfoEndpoints.GetModels)
   .WithName("GetModels");
app.MapGet("/v1/models/{id}", InfoEndpoints.GetModelById)
   .WithName("GetModelById");
app.MapGet("/v1/health", InfoEndpoints.GetHealth)
   .WithName("GetV1Health");

// =================================================================
// PIPELINE WARM-UP & AUTO-OPEN BROWSER
// =================================================================

// Start the server in the background so Kestrel begins listening for requests
await app.StartAsync();

string? url = app.Urls.FirstOrDefault(u => u.StartsWith("http://"));
if (!string.IsNullOrEmpty(url))
{
    string baseUrl = url
        .Replace("[::]", "localhost")
        .Replace("0.0.0.0", "localhost")
        .Replace("+", "localhost");

    // Silent warm-up request to pre-compile JIT, ONNX shaders, serializers, and DSP
    try
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("\n[SYSTEM] Initiating pipeline warm-up sequence...");
        Console.ResetColor();

        using var client = new HttpClient();
        
        // Hit the Tsubaki endpoint with a maximized parameter payload to trigger 
        // the full middleware, JSON deserializers, DSP effects, and routing pipeline.
        var warmupRequest = new
        {
            model = "tts-1",
            input = "System warm-up sequence complete.",
            voice = "female", // Triggers OpenVoice if present, falls back safely if not
            response_format = "mp3",
            speed = 1.0f,
            stream = false,
            noise_scale = 0.667f,
            noise_w = 0.8f,
            effect = "None",
            effect_intensity = 1.0f,
            environment = "None",
            environment_intensity = 1.0f,
            pitch = 1.0f,
            volume = 1.0f,
            language = "en", // Forces the language router to initialize
            clone_intensity = 1.0f,
            tone_temperature = 0.7f
        };

        var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(warmupRequest), 
            System.Text.Encoding.UTF8, 
            "application/json"
        );

        // Dispatch the request to the local instance
        var response = await client.PostAsync($"{baseUrl}/tsbk/audio/speech", content);

        if (response.IsSuccessStatusCode)
        {
            // Read the byte array to ensure the server streams the response, then immediately discard it for GC
            _ = await response.Content.ReadAsByteArrayAsync();
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[SYSTEM] Pipeline is fully operational. JIT and Shaders cached.");
            Console.ResetColor();
        }
    }
    catch (Exception ex)
    {
        // The warm-up sequence should never crash the server if network loopback fails
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WARNING] Warm-up sequence skipped: {ex.Message}");
        Console.ResetColor();
    }

    // Draw the ready banner and open the browser only after the pipeline is primed
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("  ╔═════════════════════════════════════════════════════╗");
    Console.WriteLine("  ║             TSUBAKI TTS ENGINE IS READY             ║");
    Console.WriteLine("  ╚═════════════════════════════════════════════════════╝");
    Console.ResetColor();
    
    Console.WriteLine($"    [Web Dashboard]       {baseUrl}");
    Console.WriteLine($"    [OpenAI Base URL]     {baseUrl}/v1");
    Console.WriteLine($"    [Speech Endpoint]     {baseUrl}/v1/audio/speech");
    Console.WriteLine($"    [Tsubaki Base URL]    {baseUrl}/tsbk");
    Console.WriteLine($"    [Extended Endpoint]   {baseUrl}/tsbk/audio/speech");
    Console.WriteLine();

    try
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(baseUrl) { UseShellExecute = true });
        }
        else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
        {
            System.Diagnostics.Process.Start("xdg-open", baseUrl);
        }
        else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
        {
            System.Diagnostics.Process.Start("open", baseUrl);
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WARNING] Could not auto-open browser (headless environment?): {ex.Message}");
        Console.ResetColor();
    }
}

// Block the main thread so the server continues running until interrupted
await app.WaitForShutdownAsync();