using STT_Runner.Services;
using STT_Runner.Endpoints;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

// =============================================================================
// STT RUNNER — OpenAI-compatible local inference server (Three-Body Pipeline)
//
// Architecture:
//   AudioProcessor  →  [Channel<IMemoryOwner<float>>]
//   VadProcessor    →  [Channel<(IMemoryOwner<float>, int)>]
//   Transcriptor    →  IAsyncEnumerable<string>  →  HTTP response
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// ── Swagger / API documentation ───────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Local STT API",
        Version = "v1",
        Description = "A drop-in local replacement for the OpenAI Whisper API " +
                      "built on the Three-Body Channel pipeline.",
    });
});

// ── CORS configuration ──────────────────────────────────────────────────────
bool allowAnyOrigin = builder.Configuration.GetValue<bool>("ServerSecurity:CorsAllowAnyOrigin", true);
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
string whisperPath;
string vadPath;

try
{
    Console.WriteLine("[SYSTEM] Running pre-flight checks for dependencies...");
    // Check and download FFmpeg if not present, since it's required for audio preprocessing. 
    // This also ensures the correct version is available for the host OS.
    await FfmpegManager.EnsureInitializedAsync();
    // Check and download AI models if not present.
    (whisperPath, vadPath) = await ModelManager.EnsureModelsExistAsync(builder.Configuration);
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
int maxConcurrency = builder.Configuration.GetValue<int>("ServerSecurity:MaxConcurrentInference", 2);
builder.Services.AddSingleton(new SemaphoreSlim(maxConcurrency, maxConcurrency));

// Stage 1: stateless audio normaliser — safe to share across requests.
builder.Services.AddSingleton<AudioProcessor>();

// Stage 2: holds a single ONNX Runtime session; ONNX inference is thread-safe.
var vadProcessor = new VadProcessor(vadPath);
builder.Services.AddSingleton(vadProcessor);

// Stage 3: holds the GGML weight matrix in RAM/VRAM; WhisperFactory is thread-safe,
// but each ProcessWhisperChannelAsync call creates its own WhisperProcessor internally.
var transcriptor = new Transcriptor(whisperPath, builder.Configuration);
builder.Services.AddSingleton(transcriptor);

// ── Model warm-up ─────────────────────────────────────────────────────────────
// Warm up both models before accepting traffic so the first real request is fast.
// VAD: triggers ONNX Runtime JIT graph compilation.
// Whisper: triggers CUDA/Vulkan shader compilation and cuBLAS plan caching.
vadProcessor.WarmUp();
await transcriptor.WarmUpAsync();

// ── HTTP pipeline ─────────────────────────────────────────────────────────────
var app = builder.Build();

// CORS should be configured early to ensure all endpoints are covered, including error responses.
app.UseCors("SttCorsPolicy");
// Rate limiting should be configured after CORS but before authentication/authorization and endpoint mapping.
if (enableRateLimiting)
{
    app.UseRateLimiter();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Map the transcription endpoints, which implement the Three-Body Channel pipeline internally.
app.MapTranscriptionEndpoints();

app.Run();