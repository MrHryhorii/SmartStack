using System.Buffers;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace STT_Runner.Services;

public sealed record PipelineRequest(
    string? Language, string? Prompt, float Temperature, bool Translate,
    VadSegmentationOptions? VadOptions, bool WordTimestamps,
    bool IncludeSegments, bool VerboseSegmentMetadata);

/// <summary>
/// Runs the same bounded decoding, VAD, and Whisper stages for HTTP and live
/// sessions. Only the final transcript is retained unless metadata is requested.
/// </summary>
public static class TranscriptPipeline
{
    private const int AudioChannelCapacity = 32;
    private const int SentenceChannelCapacity = 3;

    public static async Task<TranscriptResult> RunAsync(
        Stream input, AudioProcessor audio, VadProcessor vad, Transcriptor whisper,
        PipelineRequest request,
        Func<string, RecognizedChunk, CancellationToken, Task>? onDelta,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = linked.Token;
        var pcm = Channel.CreateBounded<float[]>(new BoundedChannelOptions(AudioChannelCapacity)
        {
            SingleWriter = true, SingleReader = true
        });
        var sentences = Channel.CreateBounded<AudioPiece>(new BoundedChannelOptions(SentenceChannelCapacity)
        {
            SingleWriter = true, SingleReader = true
        });

        Task producer = audio.ProcessStreamToChannelAsync(input, pcm.Writer, token);
        Task<long> segmenter = vad.ProcessVadChannelAsync(pcm.Reader, sentences.Writer, request.VadOptions, token);
        var text = new StringBuilder();
        List<TranscriptSegment>? segments = request.IncludeSegments ? new() : null;
        List<TranscriptWord>? words = request.WordTimestamps ? new() : null;
        string? language = null;
        try
        {
            await foreach (RecognizedChunk chunk in whisper.ProcessWhisperChannelAsync(sentences.Reader,
                request.Language, request.Translate, request.Prompt, request.Temperature,
                request.WordTimestamps, request.IncludeSegments, request.VerboseSegmentMetadata, token))
            {
                bool separator = text.Length > 0;
                if (separator) text.Append(' ');
                text.Append(chunk.Text);
                language ??= chunk.Language;
                if (segments is not null)
                    foreach (TranscriptSegment segment in chunk.Segments)
                        segments.Add(segment with { Id = segments.Count });
                words?.AddRange(chunk.Words);
                if (onDelta is not null)
                    await onDelta(separator ? " " + chunk.Text : chunk.Text, chunk, token);
            }

            await producer;
            long samples = await segmenter;
            return new TranscriptResult(text.ToString(), request.Translate ? "english" : LanguageName(language),
                samples / 16_000d, segments ?? [], words ?? []);
        }
        catch
        {
            linked.Cancel();
            try { await Task.WhenAll(producer, segmenter); } catch (Exception) { }
            throw;
        }
        finally
        {
            while (pcm.Reader.TryRead(out float[]? frame)) ArrayPool<float>.Shared.Return(frame);
            while (sentences.Reader.TryRead(out var piece)) piece.Owner.Dispose();
        }
    }

    private static string LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "unknown";
        try { return CultureInfo.GetCultureInfo(code).EnglishName.ToLowerInvariant(); }
        catch (CultureNotFoundException) { return code.ToLowerInvariant(); }
    }
}
