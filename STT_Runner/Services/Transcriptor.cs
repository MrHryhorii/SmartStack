using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Wave;

namespace STT_Runner.Services;

/// <summary>
/// Task 3: Whisper Transcription Engine.
///
/// Reads VAD-segmented speech sentences from Channel 2, transcribes each one with
/// Whisper.net, and streams the resulting text strings to the caller via IAsyncEnumerable.
///
/// Design notes
/// ────────────
/// • WhisperFactory (the heavy GGML model matrix) is loaded once at construction
///   and reused for the lifetime of this instance.
/// • A single WhisperProcessor is created per audio stream (not per sentence) because
///   processor construction is expensive and the processor is not thread-safe.
/// • StringBuilder is allocated once per stream and cleared between sentences
///   to avoid per-sentence heap pressure.
/// • Incoming audio rentals (IMemoryOwner<float>) are disposed immediately after
///   each sentence is transcribed — ownership is consumed here.
///
/// Transcription vs. Translation
/// ──────────────────────────────
/// Whisper has two distinct inference tasks:
///   • Transcription — converts speech to text in its original language.
///   • Translation   — converts speech to English regardless of source language.
/// These are separate decoder tasks in the model, not post-processing steps.
/// Pass <c>translate: true</c> to activate the translation task.
/// </summary>
public sealed class Transcriptor : IDisposable
{
    private readonly WhisperFactory _whisperFactory;
    private readonly SemaphoreSlim _whisperSemaphore;

    /// <summary>
    /// Default language used when the caller does not provide an override.
    /// "auto" lets Whisper detect the language from the first ~30 s of audio.
    /// </summary>
    private readonly string _defaultLanguage;
    private readonly string _warmUpAudioPath;

    public Transcriptor(string modelPath, IConfiguration config, SemaphoreSlim whisperSemaphore)
    {
        _whisperSemaphore = whisperSemaphore;
        bool useGpu = config.GetValue<bool>("SttSettings:UseGpu", false);
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
        if (useGpu)
            RuntimeOptions.RuntimeLibraryOrder.Insert(0, RuntimeLibrary.Vulkan);
        var factoryOptions = new WhisperFactoryOptions();

        if (useGpu)
        {
            factoryOptions.UseGpu = true;
            factoryOptions.GpuDevice = config.GetValue<int>("SttSettings:GpuDeviceIndex", 0);

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[HARDWARE] Whisper GPU requested (device {factoryOptions.GpuDevice}).");
        }
        else
        {
            factoryOptions.UseGpu = false;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[HARDWARE] Whisper: CPU-only mode.");
        }
        Console.ResetColor();

        _defaultLanguage = config.GetValue<string>("SttSettings:DefaultLanguage") ?? "auto";
        string warmUpAudioFile = config.GetValue<string>("SttSettings:WarmUpAudioFile") ?? "Assets/warmup.wav";
        _warmUpAudioPath = Path.IsPathRooted(warmUpAudioFile)
            ? warmUpAudioFile
            : Path.Combine(AppContext.BaseDirectory, warmUpAudioFile);

        // Load the GGML weight matrix once — this is the expensive operation.
        _whisperFactory = WhisperFactory.FromPath(modelPath, factoryOptions);
        Console.WriteLine($"[HARDWARE] Whisper loaded runtime: {RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"}.");
        if (useGpu && RuntimeOptions.LoadedLibrary != RuntimeLibrary.Vulkan)
            Console.WriteLine("[WARNING] Vulkan unavailable; Whisper is running on CPU.");
    }

    /// <summary>
    /// Runs real speech through both decoder tasks before the server accepts requests.
    /// This primes the loaded backend and the shader paths exercised by the sample.
    /// </summary>
    public async Task WarmUpAsync()
    {
        Console.WriteLine("[SYSTEM] Warming up Whisper...");
        if (!File.Exists(_warmUpAudioPath))
            throw new FileNotFoundException("Whisper warm-up audio is missing.", _warmUpAudioPath);

        await using var input = File.OpenRead(_warmUpAudioPath);
        var parser = new WaveParser(input);
        float[] samples = await parser.GetAvgSamplesAsync();
        if (parser.SampleRate != 16_000 || samples.Length < 16_000 || samples.Length > 16_000 * 30)
            throw new InvalidDataException("Whisper warm-up audio must be 16 kHz WAV between 1 and 30 seconds.");

        using var transcriptionProcessor = _whisperFactory.CreateBuilder()
            .WithLanguage(_defaultLanguage)
            .WithTemperature(0.0f)
            .Build();
        await foreach (var _ in transcriptionProcessor.ProcessAsync(samples.AsMemory())) { }

        using var translationProcessor = _whisperFactory.CreateBuilder()
            .WithLanguage(_defaultLanguage)
            .WithTranslate()
            .WithTemperature(0.0f)
            .Build();
        await foreach (var _ in translationProcessor.ProcessAsync(samples.AsMemory())) { }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[SYSTEM] Whisper warm-up complete (transcription + translation paths).");
        Console.ResetColor();
    }

    /// <summary>
    /// Consumes VAD-segmented sentences from <paramref name="vadChannel"/> and yields
    /// each transcribed (or translated) sentence as a string the moment it is ready,
    /// enabling real-time streaming to the caller.
    ///
    /// The method disposes every <see cref="IMemoryOwner{T}"/> it dequeues — ownership
    /// is fully transferred from VadProcessor to this method.
    /// </summary>
    /// <param name="vadChannel">
    ///     Channel of <c>(IMemoryOwner&lt;float&gt; Owner, int Length)</c> tuples produced
    ///     by <see cref="VadProcessor"/>.
    /// </param>
    /// <param name="languageHint">
    ///     BCP-47 source language code (e.g. "en", "uk") or "auto" for automatic detection.
    ///     For translation, this is a source language hint — the output is always English.
    /// </param>
    /// <param name="translate">
    ///     <c>true</c>  — activates Whisper's translation task: speech → English text.<br/>
    ///     <c>false</c> — activates Whisper's transcription task: speech → same-language text.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<string> ProcessWhisperChannelAsync(
        ChannelReader<(IMemoryOwner<float> Owner, int Length)> vadChannel,
        string? languageHint = null,
        bool translate = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string sourceLanguage = string.IsNullOrWhiteSpace(languageHint)
            ? _defaultLanguage
            : languageHint;

        var builder = _whisperFactory.CreateBuilder()
            .WithLanguage(sourceLanguage)
            .WithTemperature(0.0f); // Deterministic output; avoids hallucination loops.

        // WithTranslate() switches the decoder task from transcription to translation.
        // This is a fundamentally different inference path — not a post-processing step.
        if (translate)
            builder = builder.WithTranslate();

        // One processor for the entire audio stream — construction is expensive.
        using var processor = builder.Build();

        // Reused across sentences to avoid per-sentence StringBuilder allocation.
        var sb = new StringBuilder();

        await foreach (var (owner, length) in vadChannel.ReadAllAsync(ct))
        {
            // We own this rental; dispose it as soon as transcription is done.
            using (owner)
            {
                sb.Clear();

                // Slice to the exact valid sample count reported by VadProcessor.
                ReadOnlyMemory<float> audioSlice = owner.Memory[..length];

                await _whisperSemaphore.WaitAsync(ct);
                try
                {
                    await foreach (var segment in processor.ProcessAsync(audioSlice, ct))
                        sb.Append(segment.Text);
                }
                finally
                {
                    _whisperSemaphore.Release();
                }
            }
            // owner is returned to MemoryPool here — safe to yield after the using block.

            string sentence = sb.ToString().Trim();

            // Whisper occasionally emits empty strings or filler tokens on near-silence;
            // discard them to keep the output stream clean.
            if (!string.IsNullOrWhiteSpace(sentence))
                yield return sentence;
        }
    }

    public void Dispose()
    {
        _whisperFactory?.Dispose();
        GC.SuppressFinalize(this);
    }
}
