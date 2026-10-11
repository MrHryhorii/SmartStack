using System.Net;
using System.Reflection;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

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
    private const string ServiceName = "Tsubaki TTS Engine";
    private static readonly string ServiceVersion =
        typeof(InfoEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0]
        ?? typeof(InfoEndpoints).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";
    private static readonly string[] SupportedStreamFormats = ["audio", "sse"];
    /// <summary>
    /// Returns voices usable by the initialized synthesis pipeline.
    /// Voice fingerprints are loaded at startup; adding one requires a restart.
    /// </summary>
    public static IResult GetVoices(
        ClonerSettings cloner,
        NativeAudioDependencies nativeAudioDependencies,
        IServiceProvider services)
    {
        bool synthesisReady = IsSynthesisReady(services, nativeAudioDependencies);
        var openVoice = services.GetService<OpenVoiceRunner>();
        bool cloningAvailable = IsCloningAvailable(services, cloner, openVoice, synthesisReady);
        return Results.Ok(new { voices = GetAvailableVoiceNames(synthesisReady, cloningAvailable, openVoice) });
    }
    /// <summary>
    /// Shared by voice discovery and server status; unreadable or disabled fingerprints
    /// must not be advertised as usable voices.
    /// </summary>
    private static IReadOnlyList<string> GetAvailableVoiceNames(
        bool synthesisReady, bool cloningAvailable, OpenVoiceRunner? openVoice)
    {
        if (!synthesisReady) return Array.Empty<string>();
        if (!cloningAvailable || openVoice == null) return ["piper_base"];

        return openVoice.VoiceLibrary.Keys.Append("piper_base")
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToArray();
    }

    private static bool IsSynthesisReady(
        IServiceProvider services, NativeAudioDependencies nativeAudioDependencies)
    {
        return nativeAudioDependencies.EspeakAvailable &&
            services.GetService<PiperConfig>() != null &&
            services.GetService<PiperRunner>() != null &&
            services.GetService<UnifiedPhonemizer>() != null;
    }

    private static bool IsCloningAvailable(
        IServiceProvider services, ClonerSettings cloner, OpenVoiceRunner? openVoice, bool synthesisReady)
    {
        return synthesisReady && cloner.EnableCloning && openVoice != null &&
            services.GetService<AudioProcessor>() != null &&
            openVoice.VoiceLibrary.ContainsKey("piper_base");
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
    /// Reports engine version, initialized capabilities, and configured synthesis defaults.
    /// Enabled settings remain distinct from runtime availability. Internal hardware and
    /// filesystem paths are omitted; detector tuning is included for configuration diagnostics.
    /// </summary>
    public static IResult GetServerStatus(
        ApiSettings api,
        EffectsSettings effects,
        ClonerSettings cloner,
        DspSettings dsp,
        StreamSettings stream,
        ChunkerSettings chunker,
        PhonemizerSettings phonemizer,
        RateLimitSettings rateLimit,
        NativeAudioDependencies nativeAudioDependencies,
        IServiceProvider services)
    {
        // PiperConfig is only registered if a base model loaded successfully at startup —
        // resolved manually so a missing model reports null here instead of throwing.
        var piperConfig = services.GetService<PiperConfig>();
        bool synthesisReady = IsSynthesisReady(services, nativeAudioDependencies);
        var openVoice = services.GetService<OpenVoiceRunner>();
        bool cloningAvailable = IsCloningAvailable(services, cloner, openVoice, synthesisReady);
        var mixedPhonemizer = services.GetService<MixedLanguagePhonemizer>();
        string detectionMode = !phonemizer.UseLanguageDetector ? "disabled" :
            !synthesisReady || mixedPhonemizer == null ? "unavailable" :
            mixedPhonemizer.UsesStatisticalDetection ? "lingua" : "model_script";

        return Results.Ok(new
        {
            service = ServiceName,
            version = ServiceVersion,
            status = synthesisReady ? "ready" : "degraded",
            synthesisReady,
            model = piperConfig == null ? null : new
            {
                baseVoiceDialect = piperConfig.Espeak.Voice,
                sampleRateHz = piperConfig.Audio.SampleRate
            },
            voiceCloning = new
            {
                enabled = cloner.EnableCloning,
                available = cloningAvailable,
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
                defaultVolume = dsp.DefaultVolume,
                // Server baseline applied to requests; exposed so clients can determine the exact gain offset.
                volumeBoosterDb = dsp.VolumeBoosterDb,
                // Guard: Log10(0) produces -Infinity, which breaks System.Text.Json serialization.
                defaultTotalGainDb = dsp.DefaultVolume > 0f
                    ? 20f * MathF.Log10(dsp.DefaultVolume) + dsp.VolumeBoosterDb
                    : (float?)null
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
                minChunkSizeKb = stream.MinChunkSizeKb,
                formats = SupportedStreamFormats
            },
            formats = new
            {
                wav = nativeAudioDependencies.IsFormatAvailable(AudioFormat.Wav),
                mp3 = nativeAudioDependencies.IsFormatAvailable(AudioFormat.Mp3),
                opus = nativeAudioDependencies.IsFormatAvailable(AudioFormat.Opus),
                aac = nativeAudioDependencies.IsFormatAvailable(AudioFormat.Aac),
                flac = nativeAudioDependencies.IsFormatAvailable(AudioFormat.Flac),
                pcm = nativeAudioDependencies.IsFormatAvailable(AudioFormat.Pcm),
                b64_json = nativeAudioDependencies.IsFormatAvailable(AudioFormat.B64Json)
            },
            chunking = new
            {
                earlySplit = chunker.EarlySplit,
                maxChunkLength = chunker.EffectiveMaxChunkLength,
                sentencePauseSeconds = chunker.SentencePauseSeconds
            },
            language = new
            {
                autoDetectEnabled = phonemizer.UseLanguageDetector,
                detectionMode,
                supportedLanguages = phonemizer.SupportedLanguages,
                detectionLanguages = mixedPhonemizer?.DetectionLanguages ?? Array.Empty<string>(),
                tuning = new
                {
                    localWinnerProbabilityFloor = phonemizer.LocalWinnerProbabilityFloor,
                    localWinnerMarginFloor = phonemizer.LocalWinnerMarginFloor,
                    reliabilityProbabilityThreshold = phonemizer.ReliabilityProbabilityThreshold,
                    foreignValidationMaxLetters = phonemizer.ForeignValidationMaxLetters,
                    maxBonusMultiplier = phonemizer.MaxBonusMultiplier,
                    bonusMinLetterCount = phonemizer.BonusMinLetterCount,
                    bonusMaxLetterCount = phonemizer.BonusMaxLetterCount,
                    mixedLanguageOverrideThreshold = phonemizer.MixedLanguageOverrideThreshold,
                    minSentenceLengthForOverride = phonemizer.MinSentenceLengthForOverride
                }
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
            availableVoices = GetAvailableVoiceNames(synthesisReady, cloningAvailable, openVoice)
        });
    }
    /// <summary>
    /// Liveness probe: HTTP 200 confirms the server is responsive. Synthesis readiness is
    /// reported separately so a missing model does not look like a dead HTTP process.
    /// </summary>
    public static IResult GetHealth(
        NativeAudioDependencies nativeAudioDependencies, IServiceProvider services)
    {
        return Results.Ok(new
        {
            status = "ok",
            service = ServiceName,
            version = ServiceVersion,
            synthesisReady = IsSynthesisReady(services, nativeAudioDependencies),
            timestamp = DateTimeOffset.UtcNow
        });
    }
}