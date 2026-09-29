using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Wave;

namespace STT_Runner.Services;

/// <summary>
/// Owns one WhisperFactory and its loaded model for the server lifetime.
/// Each request runs a bounded set of processors against its VAD segments and
/// yields their text in input order. Translation is a distinct Whisper task.
/// </summary>
public sealed class Transcriptor : IDisposable
{
    private readonly WhisperFactory _whisperFactory;
    private readonly SemaphoreSlim _whisperSemaphore;
    private readonly int _whisperWorkers;

    /// <summary>
    /// Default source language; "auto" delegates language detection to Whisper.
    /// </summary>
    private readonly string _defaultLanguage;
    private readonly string _warmUpAudioPath;

    public Transcriptor(string modelPath, IConfiguration config, SemaphoreSlim whisperSemaphore)
    {
        _whisperSemaphore = whisperSemaphore;
        _whisperWorkers = config.GetValue<int>("SttSettings:WhisperWorkers", 2);
        if (_whisperWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(config), "WhisperWorkers must be positive.");

        bool useGpu = config.GetValue<bool>("SttSettings:UseGpu", false);
        // Whisper.net selects the first native runtime it can load from this order.
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
        // Resolve relative assets beside the executable in build and publish output.
        string warmUpAudioFile = config.GetValue<string>("SttSettings:WarmUpAudioFile") ?? "Assets/warmup.wav";
        _warmUpAudioPath = Path.IsPathRooted(warmUpAudioFile)
            ? warmUpAudioFile
            : Path.Combine(AppContext.BaseDirectory, warmUpAudioFile);

        // Factory disposal occurs only when the application shuts down.
        _whisperFactory = WhisperFactory.FromPath(modelPath, factoryOptions);
        Console.WriteLine($"[HARDWARE] Whisper loaded runtime: {RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"}.");
        if (useGpu && RuntimeOptions.LoadedLibrary != RuntimeLibrary.Vulkan)
            Console.WriteLine("[WARNING] Vulkan unavailable; Whisper is running on CPU.");
    }

    /// <summary>
    /// Runs real speech through both decoder tasks before the server accepts requests.
    /// This primes the selected backend for paths exercised by the sample; it
    /// does not guarantee that every shader variant used later is cached.
    /// </summary>
    public async Task WarmUpAsync()
    {
        Console.WriteLine("[SYSTEM] Warming up Whisper...");
        if (!File.Exists(_warmUpAudioPath))
            throw new FileNotFoundException("Whisper warm-up audio is missing.", _warmUpAudioPath);

        await using var input = File.OpenRead(_warmUpAudioPath);
        var parser = new WaveParser(input);
        float[] samples = await parser.GetAvgSamplesAsync();
        // Whisper's float input expects 16 kHz; reject arbitrary replacement WAVs.
        if (parser.SampleRate != 16_000 || samples.Length < 16_000 || samples.Length > 16_000 * 30)
            throw new InvalidDataException("Whisper warm-up audio must be 16 kHz WAV between 1 and 30 seconds.");

        // Both tasks must execute before the HTTP server starts accepting traffic.
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
    /// Transcribes completed VAD segments on separate processors. A single
    /// feeder numbers segments, workers recognize them, and the reader emits
    /// text in input order. Every accepted rental is disposed by its worker.
    /// </summary>
    /// <param name="vadChannel">
    ///     Channel of owned, timestamped audio pieces produced by <see cref="VadProcessor"/>.
    /// </param>
    /// <param name="languageHint">
    ///     Source language code (e.g. "en", "uk") or "auto" for detection.
    /// </param>
    /// <param name="translate">
    ///     Enables Whisper's speech-to-English translation task when true.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<RecognizedChunk> ProcessWhisperChannelAsync(
        ChannelReader<AudioPiece> vadChannel,
        string? languageHint = null,
        bool translate = false,
        string? prompt = null,
        float temperature = 0,
        bool wordTimestamps = false,
        bool includeSegments = false,
        bool verboseSegmentMetadata = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string sourceLanguage = string.IsNullOrWhiteSpace(languageHint)
            ? _defaultLanguage
            : languageHint;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = linked.Token;
        int capacity = checked(_whisperWorkers * 2);
        using var window = new SemaphoreSlim(capacity, capacity);
        var work = Channel.CreateBounded<(long Index, AudioPiece Piece)>(
            new BoundedChannelOptions(capacity) { SingleReader = false, SingleWriter = true });
        var results = Channel.CreateBounded<(long Index, RecognizedChunk Chunk)>(
            new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = false });

        Task feeder = FeedAsync();
        Task[] workers = Enumerable.Range(0, _whisperWorkers)
            .Select(_ => RecognizeAsync())
            .ToArray();
        Task completion = CompleteAsync();
        var pending = new Dictionary<long, RecognizedChunk>();
        long next = 0;

        try
        {
            // Await channel completion to preserve the worker's real failure.
            await foreach (var (index, chunk) in results.Reader.ReadAllAsync())
            {
                pending.Add(index, chunk);
                while (pending.Remove(next, out RecognizedChunk? ordered))
                {
                    next++;
                    window.Release();
                    if (!string.IsNullOrWhiteSpace(ordered.Text))
                        yield return ordered;
                }
            }

            await completion;
            if (pending.Count != 0)
                throw new InvalidOperationException("Whisper finished with missing segment results.");
        }
        finally
        {
            // An abandoned stream must stop the feeder and all native workers.
            linked.Cancel();
            try { await completion; } catch (Exception) { }
            while (work.Reader.TryRead(out var item)) item.Piece.Owner.Dispose();
        }

        async Task FeedAsync()
        {
            long index = 0;
            try
            {
                await foreach (var piece in vadChannel.ReadAllAsync(token))
                {
                    bool admitted = false;
                    try
                    {
                        // Limit unfinished and reordered segments, including text.
                        await window.WaitAsync(token);
                        admitted = true;
                        await work.Writer.WriteAsync((index++, piece), token);
                    }
                    catch
                    {
                        piece.Owner.Dispose();
                        if (admitted) window.Release();
                        throw;
                    }
                }
                work.Writer.TryComplete();
            }
            catch
            {
                linked.Cancel();
                work.Writer.TryComplete();
                throw;
            }
        }

        async Task RecognizeAsync()
        {
            try
            {
                var builder = _whisperFactory.CreateBuilder()
                    .WithLanguage(sourceLanguage)
                    .WithTemperature(temperature);
                if (translate) builder = builder.WithTranslate();
                if (!string.IsNullOrWhiteSpace(prompt)) builder = builder.WithPrompt(prompt);
                if (wordTimestamps) builder = builder.WithTokenTimestamps();

                // Processor state belongs to one worker; model weights stay shared.
                using var processor = builder.Build();
                var text = new StringBuilder();
                await foreach (var (index, piece) in work.Reader.ReadAllAsync(token))
                {
                    List<TranscriptSegment>? segments = includeSegments ? new() : null;
                    List<TranscriptWord>? words = wordTimestamps ? new() : null;
                    string? detectedLanguage = null;
                    using (piece.Owner)
                    {
                        text.Clear();
                        await _whisperSemaphore.WaitAsync(token);
                        try
                        {
                            await foreach (var segment in processor.ProcessAsync(piece.Owner.Memory[..piece.Length], token))
                            {
                                string value = segment.Text.Trim();
                                if (value.Length == 0) continue;
                                if (text.Length > 0) text.Append(' ');
                                text.Append(value);
                                detectedLanguage ??= segment.Language;
                                if (segments is null && words is null) continue;

                                double offset = piece.StartSample / 16_000d;
                                WhisperToken[] tokens = segment.Tokens ?? [];
                                if (segments is not null)
                                {
                                    double start = offset + segment.Start.TotalSeconds;
                                    double end = Math.Max(start, offset + segment.End.TotalSeconds);
                                    int[] ids = verboseSegmentMetadata ? new int[tokens.Length] : [];
                                    double logprob = 0;
                                    if (verboseSegmentMetadata)
                                    {
                                        for (int i = 0; i < tokens.Length; i++)
                                        {
                                            ids[i] = tokens[i].Id;
                                            logprob += tokens[i].ProbabilityLog;
                                        }
                                    }
                                    segments.Add(new TranscriptSegment(0, start, end, value, ids,
                                        temperature, tokens.Length == 0 ? 0 : logprob / tokens.Length,
                                        verboseSegmentMetadata ? CompressionRatio(value) : 0,
                                        segment.NoSpeechProbability));
                                }
                                if (words is not null)
                                    AppendWords(words, tokens, offset);
                            }
                        }
                        finally
                        {
                            _whisperSemaphore.Release();
                        }
                    }

                    await results.Writer.WriteAsync((index,
                        new RecognizedChunk(text.ToString(), detectedLanguage,
                            piece.StartSample / 16_000d,
                            (piece.StartSample + piece.Length) / 16_000d,
                            segments ?? [], words ?? [])), token);
                }
            }
            catch
            {
                linked.Cancel();
                throw;
            }
        }

        async Task CompleteAsync()
        {
            try
            {
                await Task.WhenAll(workers.Prepend(feeder));
                results.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                linked.Cancel();
                results.Writer.TryComplete(ex);
                throw;
            }
        }
    }

    private static double CompressionRatio(string value)
    {
        byte[] data = Encoding.UTF8.GetBytes(value);
        using var buffer = new MemoryStream();
        using (var compressor = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(data);
        return buffer.Length == 0 ? 0 : (double)data.Length / buffer.Length;
    }

    private static void AppendWords(List<TranscriptWord> words, WhisperToken[] tokens, double offset)
    {
        string value = "";
        long start = 0;
        long end = 0;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token.Text) || token.Text.StartsWith("<|", StringComparison.Ordinal)
                || token.Text.StartsWith("[_", StringComparison.Ordinal) || token.End <= token.Start)
                continue;
            if (value.Length > 0 && char.IsWhiteSpace(token.Text[0]))
            {
                words.Add(new TranscriptWord(offset + start / 100d, offset + end / 100d, value));
                value = "";
            }
            if (value.Length == 0) start = token.Start;
            value += token.Text.TrimStart();
            end = token.End;
        }
        if (value.Length > 0)
            words.Add(new TranscriptWord(offset + start / 100d, offset + end / 100d, value));
    }
    public void Dispose()
    {
        _whisperFactory?.Dispose();
        GC.SuppressFinalize(this);
    }
}
