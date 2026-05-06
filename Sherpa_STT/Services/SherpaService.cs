using SherpaOnnx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Sherpa_STT.Services;

/// <summary>
/// Task 3: Sherpa-ONNX Offline Speech Recognition.
///
/// Consumes complete speech sentences from Channel 2 (produced by VadProcessor),
/// runs each through the Sherpa-ONNX <see cref="OfflineRecognizer"/>, and yields
/// the recognised text strings as an <see cref="IAsyncEnumerable{string}"/>.
///
/// Why OfflineRecognizer (not OnlineRecognizer)?
/// ─────────────────────────────────────────────
/// VadProcessor already performed the hard work of sentence segmentation: every
/// chunk that arrives here is a complete, bounded utterance. OfflineRecognizer
/// processes the whole utterance in one pass, which gives higher accuracy and
/// avoids the latency cost of maintaining an online decoder state across frames.
///
/// Allocation note
/// ───────────────
/// Sherpa-ONNX's native AcceptWaveform binding requires a managed float[] whose
/// .Length equals the exact sample count; it is not possible to pass a pooled
/// over-sized array without corrupting the decode. Therefore a per-sentence
/// allocation of new float[length] is unavoidable here.
///
/// This is acceptable: sentence allocations are infrequent (~once per 1–5 s),
/// short-lived (immediately eligible for GC after Decode returns), and negligible
/// compared to the cost of the ONNX inference itself. The hot-path frame loop in
/// AudioProcessor and VadProcessor remains allocation-free.
///
/// Ownership rules
/// ───────────────
/// • This class disposes every IMemoryOwner<float> received from the input channel.
///   Ownership is fully transferred from VadProcessor to this method.
/// </summary>
public sealed class SherpaService : IDisposable
{
    private const int SampleRate = 16_000;
    private readonly OfflineRecognizer _recognizer;

    /// <param name="modelPath">Absolute path to the ONNX model file (e.g. model.int8.onnx).</param>
    /// <param name="tokensPath">Absolute path to the tokens vocabulary file (tokens.txt).</param>
    /// <param name="numThreads">Number of intra-op threads for ONNX Runtime. Default 2 is safe.</param>
    public SherpaService(string modelPath, string tokensPath, int numThreads = 2)
    {
        // Initialize base configuration
        var config = new OfflineRecognizerConfig();

        // Audio features (80 log-mel filterbank bins — standard for NeMo/Conformer CTC)
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;

        // Hardware / Runtime configuration
        config.ModelConfig.NumThreads = numThreads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        // Model selection
        // The omnilingual-1600-lang model uses a NeMo CTC encoder.
        config.ModelConfig.Omnilingual.Model = modelPath;
        config.ModelConfig.Tokens = tokensPath;

        _recognizer = new OfflineRecognizer(config);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[HARDWARE] Sherpa-ONNX initialized on CPU with {numThreads} threads.");
        Console.ResetColor();
    }

    /// <summary>
    /// Reads segmented sentences from <paramref name="inputChannel"/>,
    /// transcribes each one, and yields the non-empty result strings.
    ///
    /// Backpressure: the method awaits each <c>channel.ReadAllAsync</c> iteration,
    /// so Sherpa will never receive chunks faster than it can decode them —
    /// the channel naturally throttles VAD if the recogniser is slower.
    /// </summary>
    public async IAsyncEnumerable<string> TranscribeChannelAsync(
        ChannelReader<(IMemoryOwner<float> Owner, int Length)> inputChannel,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (owner, length) in inputChannel.ReadAllAsync(ct))
        {
            string text;

            using (owner)
            {
                // Allocate an exact-length array: required by the Sherpa native ABI.
                float[] samples = new float[length];
                owner.Memory.Span[..length].CopyTo(samples);

                // CreateStream() is cheap (reuses the model weights).
                using OfflineStream stream = _recognizer.CreateStream();
                stream.AcceptWaveform(SampleRate, samples);

                // Decode() runs the CTC forward pass (the expensive step).
                _recognizer.Decode(stream);

                // Extract the result directly from stream properties
                text = stream.Result.Text.Trim();
            }

            // Discard blank outputs (common for pure-silence tail padding).
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
        }
    }

    /// <summary>
    /// Runs a single silent-frame inference pass to force ONNX Runtime JIT compilation
    /// before the first real utterance, eliminating the cold-start latency spike.
    /// </summary>
    public void WarmUp()
    {
        Console.WriteLine("[SYSTEM] Warming up Sherpa recognizer...");

        // One second of silence is sufficient to trigger graph compilation
        float[] silence = new float[SampleRate];

        using OfflineStream stream = _recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, silence);
        _recognizer.Decode(stream);

        Console.WriteLine("[SYSTEM] Sherpa warm-up complete.");
    }

    public void Dispose()
    {
        _recognizer.Dispose();
        GC.SuppressFinalize(this);
    }
}