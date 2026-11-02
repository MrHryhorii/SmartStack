using ONNX_Runner.Services.Inference;
using ONNX_Runner.Services.Text.Phonemization;
using ONNX_Runner.Services.Audio;
using NAudio.Wave;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;

namespace ONNX_Runner.Services.Cloning;

/// <summary>
/// Automatically generates a baseline voice fingerprint for the currently loaded Piper TTS model.
/// OpenVoice uses this source-speaker tone embedding as the neutral point from which it shifts
/// the Piper voice toward a target cloned voice.
/// </summary>
public class BaseVoiceGenerator(
    UnifiedPhonemizer phonemizer,
    PiperRunner piperRunner,
    AudioProcessor audioProcessor,
    OpenVoiceRunner openVoice,
    PiperConfig piperConfig,
    ClonerSettings clonerConfig)
{
    private readonly UnifiedPhonemizer _phonemizer = phonemizer;
    private readonly PiperRunner _piperRunner = piperRunner;
    private readonly AudioProcessor _audioProcessor = audioProcessor;
    private readonly OpenVoiceRunner _openVoice = openVoice;
    private readonly PiperConfig _piperConfig = piperConfig;
    private readonly ClonerSettings _clonerConfig = clonerConfig;

    // OpenVoice does not publish a hard minimum duration; it only warns against references
    // that are too short. This is a Tsubaki diagnostic threshold, not an OpenVoice requirement.
    private const double RecommendedMinimumReferenceSeconds = 8.0;

    /// <summary>Synthesizes a varied reference passage and caches the base Piper fingerprint for voice cloning.</summary>
    public void GenerateAndCacheBaseFingerprint()
    {
        string configuredVoice = _piperConfig.Espeak.Voice ?? "en";
        string normalizedVoice = NormalizeVoiceCode(configuredVoice);
        string textToSpeak = ResolveReferenceText(normalizedVoice, out string resolvedCode);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[AUTO-BASE] Generating baseline footprint for model language: '{configuredVoice}'...");

        if (resolvedCode == "fallback")
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"[AUTO-BASE] No reference profile found for '{normalizedVoice}'. " +
                "Using the phonetically varied fallback passage.");
            Console.ForegroundColor = ConsoleColor.Cyan;
        }
        else if (!string.Equals(normalizedVoice, resolvedCode, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"[AUTO-BASE] Using reference profile '{resolvedCode}' for '{normalizedVoice}'.");
        }
        else
        {
            Console.WriteLine($"[AUTO-BASE] Using phonetically diverse reference for '{resolvedCode}'.");
        }

        if (string.Equals(resolvedCode, "chr", StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                "[AUTO-BASE] Cherokee uses eSpeak-ng's experimental DF frontend; " +
                "baseline pronunciation quality may vary.");
            Console.ForegroundColor = ConsoleColor.Cyan;
        }

        // 1. Phonemize the reference text and synthesize the raw audio using the loaded base Piper model.
        string phonemes = _phonemizer.GetPhonemes(textToSpeak);
        byte[] baseAudioBytes = _piperRunner.SynthesizeAudio(phonemes, false, true, 1.0f);

        // 2. Read the generated Piper WAV into memory.
        using var ms = new MemoryStream(baseAudioBytes);
        using var reader = new WaveFileReader(ms);

        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
            reader.WaveFormat.BitsPerSample != 16 ||
            reader.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException(
                $"Piper base reference must be mono 16-bit PCM, got {reader.WaveFormat}.");
        }

        double referenceSeconds = reader.TotalTime.TotalSeconds;
        Console.WriteLine($"[AUTO-BASE] Reference audio duration: {referenceSeconds:F2} s.");

        if (referenceSeconds < RecommendedMinimumReferenceSeconds)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"[AUTO-BASE] Reference is shorter than Tsubaki's " +
                $"{RecommendedMinimumReferenceSeconds:F0} s quality target. " +
                "Tone embedding may be less representative.");
            Console.ForegroundColor = ConsoleColor.Cyan;
        }

        // 3. Convert the complete mono PCM stream to float samples.
        var provider = reader.ToSampleProvider();
        int sampleCapacity = checked((int)(reader.Length / sizeof(short)));
        var samples = new float[sampleCapacity];
        int samplesRead = 0;

        while (samplesRead < samples.Length)
        {
            int read = provider.Read(samples.AsSpan(samplesRead));
            if (read <= 0) break;

            samplesRead += read;
        }

        if (samplesRead <= 0)
            throw new InvalidDataException("Piper base reference contains no PCM samples.");

        // 4. Resample from the actual WAV rate to the OpenVoice extractor rate.
        // AudioResampler is the single PCM resampling implementation used by Tsubaki.
        int openVoiceRate = _openVoice.GetTargetSamplingRate();
        var resampler = new AudioResampler(reader.WaveFormat.SampleRate, openVoiceRate);
        var resampled = resampler.Resample(samples, samplesRead);

        try
        {
            // Match reference WAV normalization so base and target embeddings are extracted
            // under equivalent loudness conditions.
            _audioProcessor.NormalizeLufs(
                resampled.Buffer.AsSpan(0, resampled.Length),
                openVoiceRate,
                _clonerConfig.ReferenceAudioTargetLufs);

            // 5. Generate the magnitude spectrogram.
            var spec = _audioProcessor.GetMagnitudeSpectrogram(
                resampled.Buffer.AsSpan(0, resampled.Length));

            // 6. Extract and cache the base Piper tone-color embedding.
            var baseFingerprint = _openVoice.ExtractToneColor(spec);
            _openVoice.VoiceLibrary["piper_base"] = baseFingerprint;
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(resampled.Buffer);
        }

        Console.WriteLine("[AUTO-BASE] Dynamic base footprint successfully calculated and stored in memory.");
        Console.ResetColor();
    }

    /// <summary>Chooses a reference passage by exact voice code, progressively shorter parent codes, then the fallback.</summary>
    private static string ResolveReferenceText(
        string normalizedVoice,
        out string resolvedCode)
    {
        if (BaseVoiceReferenceTexts.TryGetValue(normalizedVoice, out string? exact))
        {
            resolvedCode = normalizedVoice;
            return exact;
        }

        // Unknown regional/private-use variants progressively fall back through their BCP-47
        // parents (for example en-GB-x-custom -> en-GB -> en) instead of immediately discarding
        // every subtag as the previous Split('-')[0] implementation did.
        string candidate = normalizedVoice;

        while (true)
        {
            int separator = candidate.LastIndexOf('-');
            if (separator <= 0)
            {
                break;
            }

            candidate = candidate[..separator];

            if (BaseVoiceReferenceTexts.TryGetValue(candidate, out string? inherited))
            {
                resolvedCode = candidate;
                return inherited;
            }
        }

        resolvedCode = "fallback";
        return BaseVoiceReferenceTexts.FallbackText;
    }

    /// <summary>Normalizes voice codes to lowercase hyphenated form, defaulting empty input to English.</summary>
    private static string NormalizeVoiceCode(string voiceCode)
    {
        string normalized = voiceCode
            .Trim()
            .Replace('_', '-')
            .ToLowerInvariant();

        return string.IsNullOrWhiteSpace(normalized)
            ? "en"
            : normalized;
    }
}
