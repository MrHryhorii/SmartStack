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
        try
        {
            return Results.Ok(new { voices = GetAvailableVoiceNames() });
        }
        catch (Exception ex)
        {
            // Protect against file system access permission issues
            return Results.Problem($"Failed to read voices directory: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>
    /// Shared by GetVoices and GetServerStatus so both report the exact same voice list from
    /// a single source of truth instead of two independent directory scans drifting apart.
    /// </summary>
    private static IEnumerable<string> GetAvailableVoiceNames()
    {
        var voices = new List<string> { "piper_base" };
        // The 'Voices' directory is expected to be in the same location as the server executable.
        string voicesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Voices");

        if (Directory.Exists(voicesDirectory))
        {
            // Read all files with the .voice extension directly from the disk
            var voiceFiles = Directory.GetFiles(voicesDirectory, "*.voice");
            foreach (var file in voiceFiles)
            {
                voices.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        // Distinct() removes potential duplicates, OrderBy() sorts alphabetically
        return voices.Distinct().OrderBy(v => v);
    }

    /// <summary>
    /// Retrieves all available audio effects dynamically from the system enumeration.
    /// </summary>
    public static IResult GetEffects()
    {
        // Automatically extract all values from the VoiceEffectType enum
        var effects = Enum.GetNames<VoiceEffectType>();

        return Results.Ok(new { effects });
    }

    /// <summary>
    /// Retrieves all available spatial environments dynamically from the system enumeration.
    /// </summary>
    public static IResult GetEnvironments()
    {
        var environments = Enum.GetNames<SpatialEnvironment>();
        return Results.Ok(new { environments });
    }

    /// <summary>
    /// Simulates an OpenAI-style models endpoint for compatibility with clients that expect to query available TTS models.
    /// </summary>
    public static IResult GetModels()
    {
        return Results.Ok(new
        {
            @object = "list",
            data = new[]
            {
                // Since our API is designed to mimic OpenAI's TTS endpoint, 
                // we return a single "model" in the list for compatibility 
                // with clients that expect to query available models.
                new {
                    id = "tts-1",
                    @object = "model",
                    created = 1699043956,
                    owned_by = "system"
                }
            }
        });
    }

    /// <summary>
    /// Simulates an OpenAI-style model details endpoint for compatibility with clients that expect to query specific TTS model information.
    /// </summary>
    /// <param name="id"></param>
    public static IResult GetModelById(string id)
    {
        if (id != "tts-1")
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            id = "tts-1",
            @object = "model",
            created = 1699043956,
            owned_by = "system"
        });
    }

    /// <summary>
    /// Reports what's enabled server-side and its configured defaults, so a frontend can
    /// tailor its own UI (e.g. hide cloning controls entirely when ClonerSettings.EnableCloning
    /// is false) instead of showing menus with no effect on the actual synthesis result.
    /// Curated from appsettings.json — internal-only fields (hardware/ONNX/CORS tuning, exact
    /// model file paths) are intentionally left out as not relevant to a synthesis UI.
    /// </summary>
    public static IResult GetServerStatus(
        ApiSettings api,
        EffectsSettings effects,
        ClonerSettings cloner,
        DspSettings dsp,
        StreamSettings stream,
        PhonemizerSettings phonemizer,
        RateLimitSettings rateLimit,
        IServiceProvider services)
    {
        // PiperConfig is only registered if a base model loaded successfully at startup —
        // resolved manually so a missing model reports null here instead of throwing.
        var piperConfig = services.GetService<PiperConfig>();

        return Results.Ok(new
        {
            model = piperConfig == null ? null : new
            {
                baseVoiceDialect = piperConfig.Espeak.Voice,
                sampleRateHz = piperConfig.Audio.SampleRate
            },
            voiceCloning = new
            {
                enabled = cloner.EnableCloning,
                defaults = new
                {
                    cloneIntensity = cloner.CloneIntensity,
                    toneTemperature = cloner.ToneTemperature
                }
            },
            dsp = new
            {
                lowPassFilterEnabled = dsp.EnableLowPassFilter,
                lowPassCutoffHz = dsp.LowPassCutoffFrequency,
                lowPassQFactor = dsp.LowPassQFactor,
                defaultPitch = dsp.DefaultPitch,
                defaultVolume = dsp.DefaultVolume
            },
            effects = new
            {
                enabled = effects.EnableGlobalEffects,
                defaultEffect = effects.DefaultEffect,
                defaultIntensity = effects.DefaultIntensity,
                defaultEnvironment = effects.DefaultEnvironment,
                defaultEnvironmentIntensity = effects.DefaultEnvironmentIntensity,
                extendReverbTailOnFinish = effects.ExtendReverbTailOnFinish,
                available = Enum.GetNames<VoiceEffectType>(),
                availableEnvironments = Enum.GetNames<SpatialEnvironment>()
            },
            streaming = new
            {
                enabled = stream.EnableStreaming,
                flushAfterEachSentence = stream.FlushAfterEachSentence,
                minChunkSizeKb = stream.MinChunkSizeKb
            },
            language = new
            {
                autoDetectEnabled = phonemizer.UseLanguageDetector,
                supportedLanguages = phonemizer.SupportedLanguages
            },
            limits = new
            {
                // 0 means unlimited server-side (see ApiSettings) — surfaced as null so a
                // frontend doesn't misread it as "zero characters allowed".
                maxTextLength = api.MaxTextLength == 0 ? (int?)null : api.MaxTextLength,
                rateLimit = new
                {
                    permitLimit = rateLimit.PermitLimit,
                    windowSeconds = rateLimit.WindowSeconds,
                    queueLimit = rateLimit.QueueLimit
                }
            },
            availableVoices = GetAvailableVoiceNames()
        });
    }

    /// <summary>
    /// Health check endpoint to verify that the server is running and responsive.
    /// </summary>
    public static IResult GetHealth()
    {
        return Results.Ok(new
        {
            status = "ok",
            service = "Tsubaki TTS Engine",
            version = "1.0.8",
            timestamp = DateTimeOffset.UtcNow
        });
    }
}