using Microsoft.AspNetCore.Mvc;
using STT_Runner.Services;

// =================================================================
// STT RUNNER - OPENAI API COMPATIBLE LOCAL SERVER
// =================================================================
// This service provides a lightweight, local alternative to cloud STT APIs.
// It utilizes Silero VAD (Voice Activity Detection) to filter out silence
// and Whisper.net (ggml) for high-performance transcription.

var builder = WebApplication.CreateBuilder(args);

// Add Swagger for easy local API testing and documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Local STT API", Version = "v1" });
});

var app = builder.Build();

// Enable Swagger UI in development mode
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// =================================================================
// PRE-FLIGHT CHECKS & MODEL INITIALIZATION
// =================================================================
// Before the server starts accepting requests, ensure the required .onnx 
// and .bin models exist locally. If missing, auto-download them.
using (var scope = app.Services.CreateScope())
{
    try
    {
        // This will create the Models folder and download files if needed
        await ModelManager.EnsureModelsExistAsync(app.Configuration);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FATAL] Failed to initialize models: {ex.Message}");
        Console.ResetColor();
        Environment.Exit(1); // Exit if models cannot be loaded/downloaded
    }
}

// =================================================================
// API ENDPOINTS (OPENAI COMPATIBLE)
// =================================================================

// Endpoint: POST /v1/audio/transcriptions
// Designed to be a drop-in replacement for OpenAI's Whisper API.
app.MapPost("/v1/audio/transcriptions", async (
    HttpContext context,
    [FromForm] IFormFile file,
    [FromForm] string? model,
    [FromForm] string? language,
    CancellationToken cancellationToken) =>
{
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "Audio file is required." });
    }

    try
    {
        // 1. Receive uploaded audio stream
        // 2. Convert to 16kHz, 16-bit Mono using NAudio (AudioProcessor)
        // 3. Run through Silero VAD (Discard silence)
        // 4. Send active speech chunks to Whisper
        // 5. Return JSON in OpenAI format

        return Results.Ok(new { text = "This is a placeholder response. STT Engine is under construction." });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Transcription failed");
        return Results.Problem("An error occurred during transcription.", statusCode: 500);
    }
})
.DisableAntiforgery() // Disabling Anti-Forgery for API endpoints handling direct file uploads
.WithName("CreateTranscription"); // Naming the endpoint for better Swagger documentation and potential future client generation

// Start listening for incoming requests
app.Run();