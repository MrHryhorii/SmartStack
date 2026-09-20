using ONNX_Runner.Models;

namespace ONNX_Runner.Services.Synthesis;

/// <summary>
/// Resolves request/configuration state into a response-agnostic synthesis context.
/// This is the translation boundary where pseudo response formats such as B64Json
/// are converted into the real audio codec consumed by the generation pipeline.
/// </summary>
internal static class SynthesisContextBuilder
{
    private static readonly int[] ValidOpusRates = [8000, 12000, 16000, 24000, 48000];
    private static readonly int[] ValidAacRates =
        [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000];

    public static SynthesisContext Build(
        SynthesisRequest request,
        IServiceProvider services,
        PiperConfig piperConfig)
    {
        var clonerConfig = services.GetRequiredService<ClonerSettings>();

        bool useOpenVoice = !string.IsNullOrEmpty(request.Voice) &&
            !string.Equals(request.Voice, "piper_base", StringComparison.OrdinalIgnoreCase);

        var openVoice = services.GetService<OpenVoiceRunner>();
        var audioProc = services.GetService<AudioProcessor>();

        // We get the intensity in advance
        float requestedIntensity = request.CloneIntensity ?? clonerConfig.CloneIntensity;

        // Global toggle for Voice Cloning. Ensures all prerequisites (config enabled,
        // target voice requested, and models loaded) are met before activating the heavy cloner.
        bool canClone = clonerConfig.EnableCloning
                        && useOpenVoice
                        && openVoice != null
                        && audioProc != null
                        && Math.Abs(requestedIntensity) > 0.001f;

        // B64Json is a response representation, not an audio codec. Its payload currently
        // carries MP3 bytes encoded as Base64, so the synthesis core only ever sees MP3.
        AudioFormat audioFormat = ResolveAudioFormat(request.Format);

        int outSampleRate = canClone ? openVoice!.GetTargetSamplingRate() : piperConfig.Audio.SampleRate;
        int finalSampleRate = outSampleRate;

        if (audioFormat == AudioFormat.Opus)
        {
            // Ogg Opus strictly requires specific sample rates (e.g., 24kHz, 48kHz).
            finalSampleRate = GetNearestOpusSampleRate(outSampleRate);
        }
        else if (audioFormat == AudioFormat.Aac)
        {
            // AAC-LC uses a fixed set of sampling-frequency indices in ADTS/AudioSpecificConfig.
            finalSampleRate = GetNearestSampleRate(outSampleRate, ValidAacRates);
        }

        int displaySampleRate = audioFormat == AudioFormat.Opus ? 48000 : finalSampleRate;

        return new SynthesisContext
        {
            Request = request,
            PiperConfig = piperConfig,
            ClonerConfig = clonerConfig,
            DspConfig = services.GetRequiredService<DspSettings>(),
            EffectsConfig = services.GetRequiredService<EffectsSettings>(),
            ChunkerConfig = services.GetRequiredService<ChunkerSettings>(),
            TextChunker = services.GetRequiredService<TextChunker>(),
            Phonemizer = services.GetRequiredService<UnifiedPhonemizer>(),
            PiperRunner = services.GetRequiredService<PiperRunner>(),
            CanClone = canClone,
            OpenVoice = openVoice,
            AudioProc = audioProc,
            AudioFormat = audioFormat,
            OutSampleRate = outSampleRate,
            FinalSampleRate = finalSampleRate,
            DisplaySampleRate = displaySampleRate
        };
    }

    private static AudioFormat ResolveAudioFormat(AudioFormat requestedFormat)
    {
        if (requestedFormat == AudioFormat.B64Json)
        {
            return AudioFormat.Mp3;
        }

        return requestedFormat;
    }

    private static int GetNearestOpusSampleRate(int sampleRate)
    {
        return GetNearestSampleRate(sampleRate, ValidOpusRates);
    }

    private static int GetNearestSampleRate(
        int sampleRate,
        ReadOnlySpan<int> validRates)
    {
        int nearest = validRates[0];
        int nearestDistance = Math.Abs(nearest - sampleRate);

        for (int i = 1; i < validRates.Length; i++)
        {
            int candidate = validRates[i];
            int distance = Math.Abs(candidate - sampleRate);

            if (distance >= nearestDistance)
            {
                continue;
            }

            nearest = candidate;
            nearestDistance = distance;
        }

        return nearest;
    }
}
