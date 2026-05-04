using STT_Runner.Services;
using STT_Runner.Endpoints;

// =================================================================
// STT RUNNER - OPENAI API COMPATIBLE LOCAL SERVER
// =================================================================
// This is the entry point of the application. It configures the Web API,
// ensures AI models are downloaded and loaded into memory, and maps
// the OpenAI-compatible endpoints for speech-to-text processing.

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------
// SWAGGER & API DOCUMENTATION
// -----------------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Local STT API",
        Version = "v1",
        Description = "A drop-in, local replacement for the OpenAI Whisper API. Powered by Whisper.net and Silero VAD."
    });
});

// -----------------------------------------------------------------
// PRE-FLIGHT CHECKS & MODEL INITIALIZATION
// -----------------------------------------------------------------
// Before starting the HTTP server, we must ensure the .onnx (VAD) 
// and .bin (Whisper) models exist on disk.
string whisperPath = string.Empty;
string vadPath = string.Empty;

try
{
    Console.WriteLine("[SYSTEM] Running pre-flight checks for AI models...");

    // ModelManager will verify file existence and auto-download them from 
    // HuggingFace if they are missing, preventing crash-loops in Docker.
    (whisperPath, vadPath) = await ModelManager.EnsureModelsExistAsync(builder.Configuration);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[FATAL] Failed to initialize AI models: {ex.Message}");
    Console.ResetColor();

    // Hard exit. The server cannot function without its neural networks.
    Environment.Exit(1);
}

// -----------------------------------------------------------------
// DEPENDENCY INJECTION (DI) CONTAINER
// -----------------------------------------------------------------

// AudioProcessor handles resampling and mono-conversion.
// It holds no state, so it's safe to register as a Singleton.
builder.Services.AddSingleton<AudioProcessor>();

// Transcriptor is the core engine. It loads the heavy Whisper model into 
// RAM/VRAM. We MUST register it as a Singleton so the model is only loaded 
// once during startup, rather than on every HTTP request.
var transcriptor = new Transcriptor(builder.Configuration, whisperPath, vadPath);
transcriptor.Initialize(); // Unpack models into memory
await transcriptor.WarmUpAsync();  // Run a dummy inference to ensure everything is loaded and ready before accepting requests
builder.Services.AddSingleton(transcriptor);

// -----------------------------------------------------------------
// SERVER BUILD & PIPELINE CONFIGURATION
// -----------------------------------------------------------------
var app = builder.Build();

// Enable Swagger UI if running in development mode
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Map the transcription and translation endpoints to the application pipeline
app.MapTranscriptionEndpoints();

// Start accepting HTTP requests
app.Run();