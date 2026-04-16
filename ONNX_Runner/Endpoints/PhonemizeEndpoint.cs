using Microsoft.AspNetCore.Mvc;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

namespace ONNX_Runner.Endpoints;

/// <summary>
/// Handles isolated text-to-phoneme conversion requests.
/// This is highly useful for client-side debugging, caching phonemes, 
/// or verifying how the language detection and pronunciation rules handle specific words.
/// </summary>
public static class PhonemizeEndpoint
{
    public static async Task<IResult> HandlePhonemizeRequest(
        [FromBody] PhonemizeRequest request,
        [FromServices] UnifiedPhonemizer unifiedPhonemizer,
        [FromServices] ApiSettings apiSettings)
    {
        // =================================================================
        // REQUEST VALIDATION
        // =================================================================
        if (string.IsNullOrWhiteSpace(request.Input))
            return Results.BadRequest(new { error = "Input text cannot be empty." });

        // =================================================================
        // TEXT LENGTH LIMITATION (RESOURCE PROTECTION)
        // =================================================================
        // Even though phonemization doesn't use the GPU, heavy Regex operations 
        // and deep dictionary lookups on massive texts can cause CPU thread starvation.
        // We gracefully truncate the input to the configured maximum length.
        if (apiSettings.MaxTextLength > 0 && request.Input.Length > apiSettings.MaxTextLength)
        {
            request.Input = request.Input[..apiSettings.MaxTextLength];
        }

        try
        {
            // =================================================================
            // CPU-BOUND TASK OFFLOADING
            // =================================================================
            // Phonemization can involve language detection, regex parsing, and heavy dictionary lookups.
            // We wrap it in Task.Run to offload this CPU-bound work to the background thread pool, 
            // ensuring the primary ASP.NET Core request threads remain responsive under high load.
            string phonemes = await Task.Run(() => unifiedPhonemizer.GetPhonemes(request.Input));

            return Results.Ok(new { text = request.Input, phonemes });
        }
        catch (Exception ex)
        {
            // Catch any unexpected parsing or dictionary mapping errors and return a clean HTTP 500
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }
}