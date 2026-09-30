using STT_Runner.Services;
using STT_Runner.Endpoints;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Whisper.net.LibraryLoader;

// One process owns the model sessions. Each request runs audio decoding, VAD,
// and Whisper through bounded channels so uploading and inference can overlap.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 512L * 1024 * 1024);

// Swagger describes multipart uploads without binding the entire file in memory.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Mwandishi STT API",
        Version = "v1",
        Description = "A local Whisper audio API with OpenAI-compatible responses and SSE transcription events.",
    });
    c.OperationFilter<TranscriptionUploadOperationFilter>();
});

// Browser clients may use the API across origins unless the owner narrows CORS.
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

// This fixed-window request budget is separate from the concurrent-request queue.
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
            opt.QueueLimit = 0; // Queueing is handled by RequestSlots instead.
        });
        options.RejectionStatusCode = 429; // Too Many Requests
    });
}

// Resolve external binaries and models before allocating native inference sessions.
string whisperPath;
string vadPath;

try
{
    Console.WriteLine("[SYSTEM] Running pre-flight checks for dependencies...");
    await FfmpegManager.EnsureInitializedAsync();
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

// Admission bounds the number of live decoding/VAD pipelines. Whisper has its
// own semaphore because multiple admitted requests may share one GPU.
int maxConcurrency = Math.Max(1, builder.Configuration.GetValue<int>("ServerSecurity:MaxConcurrentRequests", 4));
int maxWhisper = Math.Max(1, builder.Configuration.GetValue<int>("ServerSecurity:MaxConcurrentWhisper", 2));
int queueLimit = builder.Configuration.GetValue<int>("ServerSecurity:MaxQueuedRequests", 8);
int queueWaitSeconds = builder.Configuration.GetValue<int>("ServerSecurity:QueueWaitSeconds", 120);
var requestSlots = new RequestSlots(maxConcurrency, queueLimit, queueWaitSeconds);
builder.Services.AddSingleton(requestSlots);
var whisperSlots = new SemaphoreSlim(maxWhisper, maxWhisper);

// AudioProcessor has no per-request state outside its method calls.
builder.Services.AddSingleton(new AudioProcessor(builder.Configuration));

// Keep the ONNX session and GGML model alive across requests.
var vadProcessor = new VadProcessor(vadPath, builder.Configuration);
builder.Services.AddSingleton(vadProcessor);

// Each request creates its own bounded set of WhisperProcessors from the shared factory.
var transcriptor = new Transcriptor(whisperPath, builder.Configuration, whisperSlots);
builder.Services.AddSingleton(transcriptor);
WhisperModelDetails? modelDetails = WhisperModelDetails.TryRead(whisperPath);
Console.WriteLine(modelDetails is null
    ? "[SYSTEM] GGML model architecture could not be identified."
    : $"[SYSTEM] GGML model architecture: {modelDetails.Family}, multilingual: {modelDetails.Multilingual}.");

// Do not accept traffic until both inference paths have completed warm-up.
// The speech sample exercises transcription and translation on the selected backend.
vadProcessor.WarmUp();
await transcriptor.WarmUpAsync();

var app = builder.Build();

// The browser UI and WebSocket endpoint share an origin for microphone capture.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

// Apply CORS before the rate limiter so rejected requests receive CORS headers.
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

// Endpoint handlers keep the request body as a stream for both raw and multipart audio.
app.MapTranscriptionEndpoints(enableRateLimiting);
app.MapLiveTranscriptionEndpoints();

// Readiness is true only after the models and both warm-up tasks complete.
IResult GetHealth() => Results.Ok(new
{
    status = "ok",
    model = "whisper-1",
    loaded_model = Path.GetFileName(whisperPath),
    model_family = modelDetails?.Family,
    multilingual = modelDetails?.Multilingual,
    backend = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown",
    queue = requestSlots.GetStatus()
});
app.MapGet("/health", GetHealth).WithTags("Server");
app.MapGet("/v1/health", GetHealth).WithTags("Server");

// Both API names point to the same loaded weights; the runner provides SSE.
var modelInfos = new[] { "whisper-1", "gpt-4o-transcribe" }.Select(id => new
{
    id,
    @object = "model",
    created = new DateTimeOffset(File.GetLastWriteTimeUtc(whisperPath)).ToUnixTimeSeconds(),
    owned_by = "local",
    loaded_model = Path.GetFileName(whisperPath),
    model_family = modelDetails?.Family,
    multilingual = modelDetails?.Multilingual,
    supports_streaming = true
}).ToArray();
app.MapGet("/v1/models", () => Results.Ok(new { @object = "list", data = modelInfos }))
    .WithTags("Models");
app.MapGet("/v1/models/{id}", (string id) => modelInfos.FirstOrDefault(model => model.id == id) is { } model
    ? Results.Ok(model)
    : Results.Json(new { error = new { message = $"The model '{id}' does not exist.", type = "invalid_request_error" } }, statusCode: 404))
    .WithTags("Models");

// Print useful client addresses only after Kestrel successfully starts listening.
app.Lifetime.ApplicationStarted.Register(() => StartupLinks.ShowAndOpen(app));
app.Run();
