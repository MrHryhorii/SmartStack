using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using SherpaOnnx;

namespace WhisperTiny_STT.Services;

/// <summary>
/// Task 3: Sherpa-ONNX Whisper Transcription Engine.
///
/// Consumes VAD events from Channel 2. When a speech chunk arrives, it is immediately
/// decoded using Whisper Tiny. The text is accumulated until an End-Of-Turn (EOF) 
/// signal arrives, at which point the complete paragraph is yielded to the caller.
///
/// Architecture Note:
/// Unlike Whisper.net, Sherpa-ONNX binds the Language and Task (transcribe/translate)
/// to the OfflineRecognizer at initialization. If your endpoint requires dynamic switching 
/// between transcription and translation, initialize two instances of this service.
/// </summary>
public sealed class Transcriptor : IDisposable
{
    private readonly OfflineRecognizer _recognizer;
    private const int SampleRate = 16000;

    /// <summary>
    /// Initializes the Whisper engine, automatically selecting the hardware execution provider
    /// from configuration and setting up the Whisper model parameters.
    /// </summary>
    public Transcriptor(
        IConfiguration config,
        string encoderPath,
        string decoderPath,
        string tokensPath,
        bool translateToEnglish = false)
    {
        var recognizerConfig = new OfflineRecognizerConfig();

        // Hardware Provider Selection from Config
        // We use a single 'ExecutionProvider' setting for simplicity (e.g., "vulkan", "cuda", or "cpu").
        string provider = config["SttSettings:ExecutionProvider"] ?? "cpu";

        string osName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux/Other";
        Console.WriteLine($"[HARDWARE] OS: {osName}. Using configured provider: {provider.ToUpper()}");

        recognizerConfig.ModelConfig.Provider = provider;

        // Performance & Accuracy Tuning
        // If running on CPU, we increase threads to 4 for better performance. 
        // For GPU/Vulkan, 2 threads are sufficient for managing the execution queue.
        int threads = provider.Equals("cpu", StringComparison.OrdinalIgnoreCase) ? 4 : 2;
        recognizerConfig.ModelConfig.NumThreads = threads;
        recognizerConfig.ModelConfig.Debug = 0;

        // Whisper Model Setup
        recognizerConfig.ModelConfig.Whisper.Encoder = encoderPath;
        recognizerConfig.ModelConfig.Whisper.Decoder = decoderPath;
        recognizerConfig.ModelConfig.Tokens = tokensPath;

        // Language and Task Configuration
        string language = config["SttSettings:Language"] ?? "auto";
        recognizerConfig.ModelConfig.Whisper.Language =
            language.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : language;

        // Toggle between native transcription and translation to English
        recognizerConfig.ModelConfig.Whisper.Task = translateToEnglish ? "translate" : "transcribe";

        // Load the model weights into RAM/VRAM
        _recognizer = new OfflineRecognizer(recognizerConfig);

        string taskName = translateToEnglish ? "Translation (to English)" : $"Transcription ({language})";
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[SYSTEM] Whisper Tiny initialized on {provider.ToUpper()}. Task: {taskName}");
        Console.ResetColor();
    }

    /// <summary>
    /// Consumes the tuple events from the VadProcessor. 
    /// Decodes short chunks immediately and yields the full accumulated text on Long Pause (EOF).
    /// </summary>
    public async IAsyncEnumerable<string> ProcessWhisperChannelAsync(
        ChannelReader<(float[]? Audio, bool IsEndOfTurn)> vadChannel,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // StringBuilder to accumulate decoded text until an End-Of-Turn signal is received.
        var sb = new StringBuilder();
        // The VAD channel emits tuples of (Audio Segment, IsEndOfTurn). Audio Segment is null when IsEndOfTurn is true.
        await foreach (var (audioChunk, isEndOfTurn) in vadChannel.ReadAllAsync(ct))
        {
            // If the VAD chunk contains audio, decode it immediately and append to the current sentence buffer.
            if (audioChunk != null && audioChunk.Length > 0)
            {
                using OfflineStream stream = _recognizer.CreateStream();

                // 'AcceptWaveform' can handle variable-length input, but we feed it in the same chunk sizes as the VAD for consistency.
                stream.AcceptWaveform(SampleRate, audioChunk);
                _recognizer.Decode(stream);

                string chunkText = stream.Result.Text.Trim();

                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    if (sb.Length > 0) sb.Append(" ");
                    sb.Append(chunkText);
                }
            }
            // When the VAD signals an End-Of-Turn, 
            // yield the accumulated text as a complete sentence/paragraph and clear the buffer for the next turn.
            if (isEndOfTurn)
            {
                string finalSentence = sb.ToString().Trim();
                sb.Clear();

                if (!string.IsNullOrWhiteSpace(finalSentence))
                {
                    yield return finalSentence;
                }
            }
        }
        // After the channel is completed, if there's any leftover text that hasn't been yielded, yield it as well.
        string leftOver = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(leftOver))
        {
            yield return leftOver;
        }
    }

    /// <summary>
    /// Primes the ONNX Runtime graph execution by running a dummy inference pass.
    /// This forces the GPU driver (DirectML/CUDA) to compile shaders and allocate buffers,
    /// eliminating the latency spike on the first real user interaction.
    /// </summary>
    public void WarmUp()
    {
        Console.WriteLine("[SYSTEM] Warming up Whisper...");

        // 1.5 seconds of pure silence is sufficient to trigger the full encoder-decoder graph
        int dummySamples = (int)(SampleRate * 1.5);
        float[] silence = new float[dummySamples];

        using OfflineStream stream = _recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, silence);
        _recognizer.Decode(stream);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[SYSTEM] Whisper warm-up complete.");
        Console.ResetColor();
    }

    /// Properly dispose of the ONNX Runtime session to free GPU resources when the service is shut down.
    public void Dispose()
    {
        _recognizer.Dispose();
        GC.SuppressFinalize(this);
    }
}