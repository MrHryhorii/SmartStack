using STT_Runner.Services;
using STT_Runner.Endpoints;

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

// ── Model path resolution ─────────────────────────────────────────────────────
string whisperPath;
string vadPath;

try
{
    Console.WriteLine("[SYSTEM] Running pre-flight checks for AI models...");
    (whisperPath, vadPath) = await ModelManager.EnsureModelsExistAsync(builder.Configuration);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[FATAL] Failed to initialise AI models: {ex.Message}");
    Console.ResetColor();
    Environment.Exit(1);
    return; // Unreachable, but satisfies the compiler's definite-assignment rules.
}

// ── Dependency injection ──────────────────────────────────────────────────────

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapTranscriptionEndpoints();

app.Run();