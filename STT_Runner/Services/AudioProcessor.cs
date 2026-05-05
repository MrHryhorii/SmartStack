using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Buffers;
using System.Threading.Channels;

namespace STT_Runner.Services;

/// <summary>
/// Task 1: Smart Audio Preprocessing Pipeline.
///
/// Accepts any audio stream (file or network), normalises it to 16 kHz mono float PCM,
/// then writes fixed-size 512-sample chunks to a channel for the Silero VAD to consume.
///
/// Allocation strategy
/// ───────────────────
/// • One ArrayPool<float> rental for the NAudio read buffer — returned after the loop.
/// • Each outgoing chunk is a MemoryPool<float> rental whose ownership is transferred
///   to the channel consumer (VadProcessor), which is responsible for disposing it.
/// • No per-chunk heap allocations inside the hot loop.
///
/// File-input trade-off
/// ────────────────────
/// NAudio's AudioFileReader needs a seekable file path, so streaming input is buffered
/// to a temp file first. For real-time STT this adds latency that is acceptable; if true
/// streaming is needed, replace AudioFileReader with a streaming codec pipeline.
/// </summary>
public class AudioProcessor
{
    private const int TargetSampleRate = 16_000;

    /// <summary>
    /// 512 samples @ 16 kHz = 32 ms per frame — the standard Silero VAD window size.
    /// </summary>
    private const int VadChunkSize = 512;

    /// <summary>
    /// Reads <paramref name="inputStream"/>, converts it to 16 kHz mono float PCM,
    /// and writes <see cref="VadChunkSize"/>-sample chunks to <paramref name="outputChannel"/>.
    /// The last chunk is zero-padded when the stream length is not an exact multiple.
    /// Completes the channel writer when done (or on error) so downstream consumers can finish.
    /// </summary>
    public async Task ProcessStreamToChannelAsync(
        Stream inputStream,
        ChannelWriter<IMemoryOwner<float>> outputChannel,
        CancellationToken ct = default)
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            // ── 1. Buffer to disk so NAudio can seek ──────────────────────────────
            await using (var fs = new FileStream(
                             tempPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, bufferSize: 65_536, useAsync: true))
            {
                await inputStream.CopyToAsync(fs, ct);
            }

            // ── 2. Open, downmix, resample ────────────────────────────────────────
            using var reader = new AudioFileReader(tempPath);
            ISampleProvider provider = reader;

            provider = reader.WaveFormat.Channels switch
            {
                1 => provider,
                2 => new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f },
                _ => provider.ToMono(),   // > 2 channels
            };

            if (provider.WaveFormat.SampleRate != TargetSampleRate)
                provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);

            // ── 3. Hot loop — one rental for the entire stream ────────────────────
            // Rent once; each iteration copies from here into an individually rented
            // IMemoryOwner<float> whose lifetime is owned by the channel consumer.
            float[] readBuffer = ArrayPool<float>.Shared.Rent(VadChunkSize);
            try
            {
                int samplesRead;
                while (!ct.IsCancellationRequested &&
                       (samplesRead = provider.Read(readBuffer, 0, VadChunkSize)) > 0)
                {
                    // Rent a pooled owner; consumer (VadProcessor) disposes it.
                    IMemoryOwner<float> owner = MemoryPool<float>.Shared.Rent(VadChunkSize);
                    Span<float> dest = owner.Memory.Span[..VadChunkSize];

                    readBuffer.AsSpan(0, samplesRead).CopyTo(dest);

                    // Zero-pad the tail when the stream ends mid-frame.
                    if (samplesRead < VadChunkSize)
                        dest[samplesRead..].Clear();

                    await outputChannel.WriteAsync(owner, ct);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(readBuffer);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            // Signal to VAD that no more chunks are coming.
            outputChannel.TryComplete();
        }
    }
}