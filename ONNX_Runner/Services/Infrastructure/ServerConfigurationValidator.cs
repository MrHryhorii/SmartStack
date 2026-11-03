using ONNX_Runner.Models.Configuration;

namespace ONNX_Runner.Services.Infrastructure;

/// <summary>Rejects unsafe configured defaults before inference sessions and request handlers are created.</summary>
internal static class ServerConfigurationValidator
{
    public static void Validate(DspSettings dsp, StreamSettings stream, EffectsSettings effects,
        ClonerSettings cloner, ChunkerSettings chunker, RateLimitSettings rateLimit, PhonemizerSettings phonemizer)
    {
        Range("DspSettings:DefaultPitch", dsp.DefaultPitch, .5, 2);
        Range("DspSettings:DefaultVolume", dsp.DefaultVolume, 0, 4);
        Range("DspSettings:LowPassQFactor", dsp.LowPassQFactor, .1f, 1);
        Positive("DspSettings:LowPassCutoffFrequency", dsp.LowPassCutoffFrequency);
        Finite("DspSettings:VolumeBoosterDb", dsp.VolumeBoosterDb);
        if (!float.IsFinite(MathF.Pow(10, dsp.VolumeBoosterDb / 20) * 4))
            Invalid("DspSettings:VolumeBoosterDb", "must produce a finite gain for the supported volume range");

        Range("EffectsSettings:DefaultIntensity", effects.DefaultIntensity, 0, 1);
        Range("EffectsSettings:DefaultEnvironmentIntensity", effects.DefaultEnvironmentIntensity, 0, 1);
        Range("EffectsSettings:ReverbTailSilenceFloor", effects.ReverbTailSilenceFloor, 0, 1);
        Range("ClonerSettings:CloneIntensity", cloner.CloneIntensity, 0, 2);
        Range("ClonerSettings:ToneTemperature", cloner.ToneTemperature, .1f, 2);
        Finite("ClonerSettings:ReferenceAudioTargetLufs", cloner.ReferenceAudioTargetLufs);
        Range("ChunkerSettings:SentencePauseSeconds", chunker.SentencePauseSeconds, 0, int.MaxValue / 192000d);

        // Zero retains the existing minimum-sized streaming buffer; larger values must not overflow bytes.
        Range("StreamSettings:MinChunkSizeKb", stream.MinChunkSizeKb, 0, int.MaxValue / 1024);
        Positive("RateLimitSettings:PermitLimit", rateLimit.PermitLimit);
        Positive("RateLimitSettings:WindowSeconds", rateLimit.WindowSeconds);
        Range("RateLimitSettings:QueueLimit", rateLimit.QueueLimit, 0, int.MaxValue);

        // Values above one intentionally disable confidence gates; preserve that tuning contract.
        Finite("PhonemizerSettings:LocalWinnerProbabilityFloor", phonemizer.LocalWinnerProbabilityFloor);
        Finite("PhonemizerSettings:LocalWinnerMarginFloor", phonemizer.LocalWinnerMarginFloor);
        Finite("PhonemizerSettings:ReliabilityProbabilityThreshold", phonemizer.ReliabilityProbabilityThreshold);
        Finite("PhonemizerSettings:MaxBonusMultiplier", phonemizer.MaxBonusMultiplier);
        Finite("PhonemizerSettings:MixedLanguageOverrideThreshold", phonemizer.MixedLanguageOverrideThreshold);
    }

    private static void Range(string path, double value, double minimum, double maximum)
    {
        Finite(path, value);
        if (value < minimum || value > maximum) Invalid(path, $"must be between {minimum} and {maximum}");
    }

    private static void Positive(string path, double value)
    {
        Finite(path, value);
        if (value <= 0) Invalid(path, "must be greater than zero");
    }

    private static void Finite(string path, double value)
    {
        if (!double.IsFinite(value)) Invalid(path, "must be finite");
    }

    private static void Invalid(string path, string reason)
        => throw new InvalidOperationException($"Invalid server setting '{path}': {reason}.");
}
