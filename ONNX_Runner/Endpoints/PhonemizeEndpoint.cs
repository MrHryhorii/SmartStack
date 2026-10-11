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
    /// <summary>Validates and limits input, then returns the processed text and its phonemes without generating audio.</summary>
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
        // Apply the configured input length limit before language analysis and phonemization.
        if (apiSettings.MaxTextLength > 0 && request.Input.Length > apiSettings.MaxTextLength)
        {
            request.Input = request.Input[..apiSettings.MaxTextLength];
        }

        try
        {
            // =================================================================
            // CPU-BOUND TASK OFFLOADING
            // =================================================================
            // Execute CPU-bound analysis and native phonemization on the thread pool.
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