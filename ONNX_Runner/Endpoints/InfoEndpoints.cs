using System.Net;
using ONNX_Runner.Models;

namespace ONNX_Runner.Endpoints;

/// <summary>
/// Security filter that restricts endpoint access exclusively to the local machine (localhost).
/// Prevents external exposure of administrative or informational endpoints.
/// </summary>
public class LocalHostOnlyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var remoteIp = context.HttpContext.Connection.RemoteIpAddress;

        if (remoteIp == null || !IPAddress.IsLoopback(remoteIp))
        {
            return Results.Problem("Access Denied: This endpoint is restricted to local server access only.", statusCode: 403);
        }

        return await next(context);
    }
}

/// <summary>
/// Provides informational endpoints for auto-discovery of available server resources (voices, effects).
/// Designed for local dashboard/UI integration.
/// </summary>
public static class InfoEndpoints
{
    /// <summary>
    /// Dynamically scans the 'Voices' directory and returns all available voice fingerprints.
    /// Supports real-time discovery (e.g., when Docker volumes are updated).
    /// </summary>
    public static IResult GetVoices()
    {
        var voices = new List<string> { "piper_base" };
        string voicesDirectory = "Voices";

        try
        {
            if (Directory.Exists(voicesDirectory))
            {
                // Read all files with the .voice extension directly from the disk
                var voiceFiles = Directory.GetFiles(voicesDirectory, "*.voice");
                foreach (var file in voiceFiles)
                {
                    voices.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }
        catch (Exception ex)
        {
            // Protect against file system access permission issues
            return Results.Problem($"Failed to read voices directory: {ex.Message}", statusCode: 500);
        }

        // Distinct() removes potential duplicates, OrderBy() sorts alphabetically
        return Results.Ok(new { voices = voices.Distinct().OrderBy(v => v) });
    }

    /// <summary>
    /// Retrieves all available audio effects dynamically from the system enumeration.
    /// </summary>
    public static IResult GetEffects()
    {
        // Automatically extract all values from the VoiceEffectType enum
        var effects = Enum.GetNames(typeof(VoiceEffectType));

        return Results.Ok(new { effects });
    }

    /// <summary>
    /// Retrieves all available spatial environments dynamically from the system enumeration.
    /// </summary>
    public static IResult GetEnvironments()
    {
        var environments = Enum.GetNames(typeof(SpatialEnvironment));
        return Results.Ok(new { environments });
    }
}