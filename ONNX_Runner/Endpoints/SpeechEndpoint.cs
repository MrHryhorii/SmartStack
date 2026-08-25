using Microsoft.AspNetCore.Mvc;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

namespace ONNX_Runner.Endpoints;

/// <summary>
/// Thin HTTP endpoint handlers. Each one only validates the wire-specific request shape,
/// adapts it into a SynthesisRequest, and delegates the actual work to
/// SpeechSynthesisService — which is where the semaphore, streaming, and generation
/// pipeline all live, shared across every endpoint. New external API shapes should follow
/// the same two-line pattern: adapt, then delegate. Never call the service's internals
/// directly from here, and never duplicate the semaphore/generation logic in a new handler.
/// </summary>
public static class SpeechEndpoint
{
    public static async Task<IResult> HandleOpenAiRequest(
        HttpContext httpContext,
        [FromBody] OpenAiSpeechRequest request,
        [FromServices] SpeechSynthesisService synthesisService,
        CancellationToken cancellationToken)
    {
        // =================================================================
        // REQUEST VALIDATION
        // =================================================================
        if (string.IsNullOrWhiteSpace(request.Input))
            return Results.BadRequest(new { error = "Input text cannot be empty." });

        var (synthesisRequest, formatError) = OpenAiRequestAdapter.ToSynthesisRequest(request);
        if (formatError != null)
            return Results.BadRequest(new { error = formatError });

        return await synthesisService.SynthesizeAsync(synthesisRequest!, httpContext, cancellationToken);
    }

    public static async Task<IResult> HandleTsubakiRequest(
        HttpContext httpContext,
        [FromBody] TsubakiSpeechRequest request,
        [FromServices] SpeechSynthesisService synthesisService,
        CancellationToken cancellationToken)
    {
        // =================================================================
        // REQUEST VALIDATION
        // =================================================================
        if (string.IsNullOrWhiteSpace(request.Input))
            return Results.BadRequest(new { error = "Input text cannot be empty." });

        var (synthesisRequest, formatError) = TsubakiRequestAdapter.ToSynthesisRequest(request);
        if (formatError != null)
            return Results.BadRequest(new { error = formatError });

        return await synthesisService.SynthesizeAsync(synthesisRequest!, httpContext, cancellationToken);
    }
}
