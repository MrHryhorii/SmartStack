using Microsoft.AspNetCore.Mvc;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

namespace ONNX_Runner.Endpoints;

public static class PhonemizeEndpoint
{
    public static async Task<IResult> HandlePhonemizeRequest(
        [FromBody] PhonemizeRequest request,
        [FromServices] UnifiedPhonemizer unifiedPhonemizer)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
            return Results.BadRequest(new { error = "Input text cannot be empty." });

        try
        {
            string phonemes = await Task.Run(() => unifiedPhonemizer.GetPhonemes(request.Input));
            return Results.Ok(new { text = request.Input, phonemes });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }
}