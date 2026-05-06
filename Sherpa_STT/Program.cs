using Sherpa_STT.Services;
using Sherpa_STT.Endpoints;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

// =============================================================================
// SHERPA STT RUNNER — OpenAI-compatible local inference server
//
// Architecture:
//   AudioProcessor  →  [Channel<IMemoryOwner<float>>]
//   VadProcessor    →  [Channel<(IMemoryOwner<float>, int)>]
//   SherpaService   →  IAsyncEnumerable<string>  →  HTTP response
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// ── Swagger / API documentation ───────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Local STT API (Sherpa-ONNX)",
        Version = "v1",
        Description = "A drop-in local replacement for the OpenAI Audio API " +
                      "built on the Three-Body Channel pipeline and Sherpa-ONNX.",
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
        {
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        }
        else
        {
            policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader();
        }
    });
});

// ── Rate limiting configuration ──────────────────────────────────────────────
bool enableRateLimiting = builder.Configuration.GetValue<bool>("ServerSecurity:EnableRateLimiting", true);
if (enableRateLimiting)
{
    int maxRequests = builder.Configuration.GetValue<int>("ServerSecurity:RateLimitMaxRequests", 100);
    int windowSec = builder.Configuration.GetValue<int>("ServerSecurity:RateLimitWindowSeconds", 60);

    builder.Services.AddRateLimiter(options =>
    {
        options.AddFixedWindowLimiter("SttRateLimit", opt =>
        {
            opt.PermitLimit = maxRequests;
            opt.Window = TimeSpan.FromSeconds(windowSec);
            opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            opt.QueueLimit = 50; // Allow some queuing beyond the immediate limit, but reject if the queue is too long.
        });
        options.RejectionStatusCode = 429; // Too Many Requests
    });
}

// ── Dependency checks and initialisation ──────────────────────────────────────
string sherpaModelPath;
string sherpaTokensPath;
string vadPath;

try
{
    Console.WriteLine("[SYSTEM] Running pre-flight checks for dependencies...");

    // Check and download FFmpeg if not present, since it's required for audio preprocessing.
    await FfmpegManager.EnsureInitializedAsync();

    // Check and download AI models (Sherpa + VAD) if not present.
    (sherpaModelPath, sherpaTokensPath, vadPath) = await ModelManager.EnsureModelsExistAsync(builder.Configuration);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[FATAL] Failed to initialise dependencies: {ex.Message}");
    Console.ResetColor();
    Environment.Exit(1);
    return; // Unreachable, but satisfies the compiler's definite-assignment rules.
}

// ── Dependency injection ──────────────────────────────────────────────────────

// SemaphoreSlim to limit concurrent inference requests and prevent resource exhaustion.
int maxConcurrency = builder.Configuration.GetValue<int>("ServerSecurity:MaxConcurrentInference", 8);
builder.Services.AddSingleton(new SemaphoreSlim(maxConcurrency, maxConcurrency));

// Stage 1: stateless audio normaliser — safe to share across requests.
builder.Services.AddSingleton<AudioProcessor>();

// Stage 2: holds a single ONNX Runtime session; ONNX inference is thread-safe.
var vadProcessor = new VadProcessor(vadPath);
builder.Services.AddSingleton(vadProcessor);

// Stage 3: holds the OfflineRecognizer session; ONNX CTC inference is thread-safe.
var sherpaService = new SherpaService(sherpaModelPath, sherpaTokensPath);
builder.Services.AddSingleton(sherpaService);

// ── Model warm-up ─────────────────────────────────────────────────────────────
// Warm up both models before accepting traffic so the first real request is fast.
vadProcessor.WarmUp();
sherpaService.WarmUp();

// ── HTTP pipeline ─────────────────────────────────────────────────────────────
var app = builder.Build();

// CORS should be configured early to ensure all endpoints are covered.
app.UseCors("SttCorsPolicy");

// Rate limiting should be configured after CORS but before endpoint mapping.
if (enableRateLimiting)
{
    app.UseRateLimiter();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Map the transcription endpoints.
app.MapTranscriptionEndpoints();

app.Run();