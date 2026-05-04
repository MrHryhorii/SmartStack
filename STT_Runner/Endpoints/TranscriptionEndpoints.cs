using Microsoft.AspNetCore.Mvc;
using STT_Runner.Services;

namespace STT_Runner.Endpoints;

/// <summary>
/// Contains all endpoint definitions for Speech-to-Text operations.
/// Designed to be 1:1 compatible with the OpenAI API structure.
/// </summary>
public static class TranscriptionEndpoints
{
    public static void MapTranscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        // =================================================================
        // ENDPOINT 1: TRANSCRIPTIONS (Original Language)
        // =================================================================
        app.MapPost("/v1/audio/transcriptions", async (
            HttpContext context,
            IFormFile file,
            [FromForm] string? model,    // Accepted for API compatibility
            [FromForm] string? language, // Optional: ISO code to force a specific language
            [FromServices] AudioProcessor audioProcessor,
            [FromServices] Transcriptor transcriptor) =>
        {
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "Audio file is required." });

            try
            {
                float[] audioSamples = await audioProcessor.ProcessIncomingAudioAsync(file);

                // Pass the 'language' parameter to override default settings if provided
                string transcribedText = await transcriptor.ProcessAudioAsync(audioSamples, language, isTranslation: false);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n[TRANSCRIPTION SUCCESS]");
                Console.WriteLine($"-> {transcribedText}\n");
                Console.ResetColor();

                return Results.Ok(new { text = transcribedText });
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Transcription failed: {ex.Message}");
                Console.ResetColor();
                return Results.Problem("An internal error occurred during transcription.", statusCode: 500);
            }
        })
        .DisableAntiforgery()
        .WithName("CreateTranscription")
        .WithSummary("Transcribes audio into the input language.");

        // =================================================================
        // ENDPOINT 2: TRANSLATIONS (Translate to English)
        // =================================================================
        app.MapPost("/v1/audio/translations", async (
            HttpContext context,
            IFormFile file,
            [FromForm] string? model,    // Accepted for API compatibility
            [FromForm] string? language, // While rarely used for translations, accepted for consistency
            [FromServices] AudioProcessor audioProcessor,
            [FromServices] Transcriptor transcriptor) =>
        {
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "Audio file is required." });

            try
            {
                float[] audioSamples = await audioProcessor.ProcessIncomingAudioAsync(file);

                string translatedText = await transcriptor.ProcessAudioAsync(audioSamples, language, isTranslation: true);

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n[TRANSLATION SUCCESS]");
                Console.WriteLine($"-> {translatedText}\n");
                Console.ResetColor();

                return Results.Ok(new { text = translatedText });
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Translation failed: {ex.Message}");
                Console.ResetColor();
                return Results.Problem("An internal error occurred during translation.", statusCode: 500);
            }
        })
        .DisableAntiforgery()
        .WithName("CreateTranslation")
        .WithSummary("Translates audio into English.");
    }
}