using ONNX_Runner.Models;

namespace ONNX_Runner.Services.Synthesis;

/// <summary>
/// Resolves request/configuration state into a response-agnostic synthesis context.
/// This is the translation boundary where pseudo response formats such as B64Json
/// are converted into the real audio codec consumed by the generation pipeline.
/// </summary>
internal static class SynthesisContextBuilder
{
    /// <summary>Resolves request defaults, cloning availability, and processing/output rates into one synthesis context.</summary>
    public static SynthesisContext Build(
        SynthesisRequest request,
        IServiceProvider services,
        PiperConfig piperConfig)
    {
        var clonerConfig = services.GetRequiredService<ClonerSettings>();
        var dspConfig = services.GetRequiredService<DspSettings>();

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
                        && openVoice.VoiceLibrary.ContainsKey(request.Voice)
                        && openVoice.VoiceLibrary.ContainsKey("piper_base")
                        && Math.Abs(requestedIntensity) > 0.001f;

        // B64Json is a response representation, not an audio codec. Its payload currently
        // carries MP3 bytes encoded as Base64, so the synthesis core only ever sees MP3.
        AudioFormat audioFormat = ResolveAudioFormat(request.Format);

        int outSampleRate = canClone ? openVoice!.GetTargetSamplingRate() : piperConfig.Audio.SampleRate;
        // Keep the established DSP rate independent from an output-rate override.
        // A resolved zero retains the same buffers, effects, and encoder path.
        int processingSampleRate = audioFormat is AudioFormat.Opus or AudioFormat.Aac
            ? AudioSampleRate.Resolve(outSampleRate, audioFormat)
            : outSampleRate;
        int requestedSampleRate = request.SampleRate ?? dspConfig.DefaultSampleRate;
        bool enforceSampleRate = requestedSampleRate > 0;
        int finalSampleRate = enforceSampleRate
            ? AudioSampleRate.Resolve(requestedSampleRate, audioFormat)
            : processingSampleRate;

        int displaySampleRate = audioFormat == AudioFormat.Opus ? 48000 : finalSampleRate;

        return new SynthesisContext
        {
            Request = request,
            PiperConfig = piperConfig,
            ClonerConfig = clonerConfig,
            DspConfig = dspConfig,
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
            ProcessingSampleRate = processingSampleRate,
            FinalSampleRate = finalSampleRate,
            EnforceSampleRate = enforceSampleRate,
            DisplaySampleRate = displaySampleRate
        };
    }

    /// <summary>Maps the Base64 JSON wrapper to its underlying MP3 audio codec.</summary>
    private static AudioFormat ResolveAudioFormat(AudioFormat requestedFormat)
    {
        if (requestedFormat == AudioFormat.B64Json)
        {
            return AudioFormat.Mp3;
        }

        return requestedFormat;
    }

}
