using Microsoft.Extensions.Logging;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;

namespace ONNX_Runner.Services.Inference;

/// <summary>Resolves one valid speaker ID at startup, before inference sessions are created.</summary>
internal static partial class PiperSpeakerSelector
{
    /// <summary>Prefers the server selection, the model default, then the existing mapped fallback.</summary>
    public static long? Resolve(PiperConfig config, ModelSettings settings, ILogger logger)
    {
        if (config.NumSpeakers <= 1)
        {
            LogSingleSpeakerModel(logger);
            return null;
        }

        string? requestedSpeaker = settings.Speaker?.Trim();
        var speakerMap = config.SpeakerIdMap;
        if (!string.IsNullOrEmpty(requestedSpeaker))
        {
            if (speakerMap != null && speakerMap.TryGetValue(requestedSpeaker, out int configuredId))
            {
                if (IsValidId(configuredId, config.NumSpeakers))
                {
                    LogConfiguredSpeaker(logger, requestedSpeaker, configuredId);
                    return configuredId;
                }

                LogInvalidConfiguredSpeaker(logger, requestedSpeaker, configuredId, config.NumSpeakers);
            }
            else
            {
                LogSpeakerNotFound(logger, requestedSpeaker);
            }
        }

        if (config.DefaultSpeakerId is int defaultId)
        {
            if (IsValidId(defaultId, config.NumSpeakers))
            {
                LogDefaultSpeaker(logger, defaultId);
                return defaultId;
            }

            LogInvalidDefaultSpeaker(logger, defaultId, config.NumSpeakers);
        }

        KeyValuePair<string, int>? fallback = null;
        if (speakerMap != null)
        {
            foreach (var speaker in speakerMap)
            {
                if (!IsValidId(speaker.Value, config.NumSpeakers)) continue;
                if (fallback.HasValue && speaker.Value >= fallback.Value.Value) continue;
                fallback = speaker;
            }
        }

        if (fallback is { } selected)
        {
            LogFallbackSpeaker(logger, selected.Key, selected.Value);
            return selected.Value;
        }

        LogNoValidSpeaker(logger, config.NumSpeakers);
        return 0;
    }

    private static bool IsValidId(int speakerId, int speakerCount) =>
        (uint)speakerId < (uint)speakerCount;

    [LoggerMessage(Level = LogLevel.Information, Message = "[PIPER] Single-speaker model detected. Speaker selection is not required.")]
    private static partial void LogSingleSpeakerModel(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "[PIPER] Multi-speaker model detected. Using configured speaker '{Speaker}' (sid={SpeakerId}).")]
    private static partial void LogConfiguredSpeaker(ILogger logger, string speaker, int speakerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PIPER] Configured speaker '{Speaker}' was not found in speaker_id_map. Trying the model default and fallback speakers.")]
    private static partial void LogSpeakerNotFound(ILogger logger, string speaker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PIPER] Configured speaker '{Speaker}' has invalid sid={SpeakerId} for {SpeakerCount} speakers. Trying the model default and fallback speakers.")]
    private static partial void LogInvalidConfiguredSpeaker(ILogger logger, string speaker, int speakerId, int speakerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "[PIPER] Using model default speaker (sid={SpeakerId}).")]
    private static partial void LogDefaultSpeaker(ILogger logger, int speakerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PIPER] Ignoring default_speaker_id={SpeakerId}: expected an ID from zero to {SpeakerCount} exclusive.")]
    private static partial void LogInvalidDefaultSpeaker(ILogger logger, int speakerId, int speakerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "[PIPER] Using fallback speaker '{Speaker}' (sid={SpeakerId}).")]
    private static partial void LogFallbackSpeaker(ILogger logger, string speaker, int speakerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PIPER] Model reports {SpeakerCount} speakers but has no valid default or mapped speaker. Falling back to sid=0.")]
    private static partial void LogNoValidSpeaker(ILogger logger, int speakerCount);
}
