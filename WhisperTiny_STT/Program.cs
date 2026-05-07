using WhisperTiny_STT.Services;
using WhisperTiny_STT.Endpoints;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// =============================================================================
// STT RUNNER — OpenAI-compatible local inference server 
// built on Sherpa-ONNX's Whisper Tiny and Silero VAD.
//
// Architecture:
//   AudioProcessor  →  [Channel<IMemoryOwner<float>>]
//   VadProcessor    →  [Channel<(float[]?, bool)>]
//   Transcriptor    →  IAsyncEnumerable<string>  →  HTTP response
// =============================================================================

// ── Swagger / API documentation ───────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Local STT API (Sherpa-ONNX Whisper Tiny)",
        Version = "v1",
        Description = "A drop-in local replacement for the OpenAI Whisper API built on the Three-Body Channel pipeline with Two-Tier VAD.",
    });
});

// ── CORS configuration ──────────────────────────────────────────────────────
bool allowAnyOrigin = builder.Configuration.GetValue("ServerSecurity:CorsAllowAnyOrigin", true);
string[] allowedOrigins = builder.Configuration.GetSection("ServerSecurity:CorsAllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("SttCorsPolicy", policy =>
    {
        if (allowAnyOrigin)
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        else
            policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader();
    });
});

// ── Rate Limiting configuration ───────────────────────────────────────────────
bool enableRateLimiting = builder.Configuration.GetValue("ServerSecurity:EnableRateLimiting", true);
if (enableRateLimiting)
{
    int maxRequests = builder.Configuration.GetValue("ServerSecurity:RateLimitMaxRequests", 100);
    int windowSeconds = builder.Configuration.GetValue("ServerSecurity:RateLimitWindowSeconds", 60);

    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: partition => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = maxRequests,
                    QueueLimit = 0,
                    Window = TimeSpan.FromSeconds(windowSeconds)
                }));
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });
}

// ── Dependency checks and initialisation ──────────────────────────────────────
string encoderPath, decoderPath, tokensPath, vadPath;

try
{
    Console.WriteLine("[SYSTEM] Running pre-flight checks for dependencies...");
    await FfmpegManager.EnsureInitializedAsync();

    // ModelManager now returns 4 paths required for Whisper-ONNX and VAD
    (encoderPath, decoderPath, tokensPath, vadPath) = await ModelManager.EnsureModelsExistAsync(builder.Configuration);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[FATAL] Failed to initialise dependencies: {ex.Message}");
    Console.ResetColor();
    Environment.Exit(1);
    return;
}

// ── Dependency injection ──────────────────────────────────────────────────────

// 1. Concurrency Guard (SemaphoreSlim)
int maxConcurrency = builder.Configuration.GetValue<int>("ServerSecurity:MaxConcurrentInference", 4);
builder.Services.AddSingleton(new SemaphoreSlim(maxConcurrency, maxConcurrency));

// 2. AudioProcessor (Stateless pipeline component)
builder.Services.AddSingleton<AudioProcessor>();

// 3. VadProcessor (Stateful, thread-safe model wrapper)
var vadProcessor = new VadProcessor(builder.Configuration, vadPath);
vadProcessor.WarmUp(); // Force ONNX Runtime JIT compilation
builder.Services.AddSingleton(vadProcessor);

// 4. Conditional Transcriptor Initialization (Keyed Services)
bool enableTranscription = builder.Configuration.GetValue("Endpoints:EnableTranscription", true);
bool enableTranslation = builder.Configuration.GetValue("Endpoints:EnableTranslation", false);

if (enableTranscription)
{
    var transcriber = new Transcriptor(builder.Configuration, encoderPath, decoderPath, tokensPath, translateToEnglish: false);
    transcriber.WarmUp(); // Prime the GPU/CPU execution graph
    builder.Services.AddKeyedSingleton("transcribe", transcriber);
}

if (enableTranslation)
{
    var translator = new Transcriptor(builder.Configuration, encoderPath, decoderPath, tokensPath, translateToEnglish: true);
    translator.WarmUp(); // Prime the GPU/CPU execution graph
    builder.Services.AddKeyedSingleton("translate", translator);
}

// ── HTTP pipeline ─────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseCors("SttCorsPolicy");

if (enableRateLimiting)
{
    app.UseRateLimiter();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Map the transcription endpoints dynamically based on configuration
app.MapTranscriptionEndpoints(enableTranscription, enableTranslation);

// Start listening for requests
app.Run();