using Microsoft.ML.OnnxRuntime;
using System.Buffers;
using System.Threading.Channels;

namespace STT_Runner.Services;

public sealed record VadSegmentationOptions(int SilenceDurationMs, int PrefixPaddingMs, float Threshold);

/// <summary>
/// Shares immutable Silero model weights. Every request owns its waveform
/// context, recurrent state, segmentation clock, and pooled PCM storage.
/// </summary>
public sealed class VadProcessor : IDisposable
{
    private readonly VadProfile _profile;
    private readonly InferenceSession _session;

    public VadProcessor(string vadModelPath, IConfiguration config)
    {
        _profile = VadProfile.Read(config);
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new InferenceSession(vadModelPath, options);
    }

    public async Task<long> ProcessVadChannelAsync(
        ChannelReader<PcmFrame> inputChannel, ChannelWriter<AudioPiece> outputChannel,
        VadSegmentationOptions? options = null, CancellationToken ct = default, bool useVad = true)
    {
        SileroVadStream? inference = useVad ? new(_session) : null;
        // Completed files without a chunking strategy preserve the whole waveform.
        var profile = useVad ? _profile : new VadProfile
        {
            Threshold = 0, MinSpeechMs = 0, PrefixPaddingMs = 0, TailPaddingMs = 0,
            MaxBufferedSegmentSeconds = _profile.MaxBufferedSegmentSeconds
        };
        using var segmenter = new VadSegmenter(profile, useVad ? options : null);
        Exception? failure = null;
        try
        {
            await foreach (PcmFrame frame in inputChannel.ReadAllAsync(ct))
            {
                try
                {
                    float probability = inference?.Predict(frame.Samples.AsSpan(0, frame.Length)) ?? 1f;
                    AudioPiece? piece = segmenter.Accept(frame.Samples.AsSpan(0, frame.Length), probability);
                    await TransferAsync(piece, outputChannel, ct);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(frame.Samples);
                }
            }
            await TransferAsync(segmenter.Finish(), outputChannel, ct);
            return segmenter.SamplesRead;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            outputChannel.TryComplete(failure);
        }
    }

    private static async ValueTask TransferAsync(
        AudioPiece? piece, ChannelWriter<AudioPiece> channel, CancellationToken ct)
    {
        if (piece is null) return;
        try { await channel.WriteAsync(piece, ct); }
        catch { piece.Owner.Dispose(); throw; }
    }

    public void WarmUp()
    {
        Console.WriteLine("[SYSTEM] Warming up VAD...");
        new SileroVadStream(_session).Predict(new float[SileroVadStream.WindowSize]);
        Console.WriteLine("[SYSTEM] VAD warm-up complete.");
    }

    public void Dispose()
    {
        _session.Dispose();
        GC.SuppressFinalize(this);
    }
}
