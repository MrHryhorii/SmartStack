using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace WhisperTiny_STT.Services;

/// <summary>
/// Task 1: Smart Audio Preprocessing Pipeline (FFmpeg In-Memory Piping).
///
/// Accepts any audio stream (file or network) and pipes it directly into FFmpeg.
/// FFmpeg decodes and normalises it to 16 kHz mono float PCM on the fly, 
/// bypassing the hard drive completely. The raw bytes are then chunked into 
/// fixed 512-sample frames and written to a channel for the Silero VAD to consume.
///
/// Allocation strategy
/// ───────────────────
/// • One ArrayPool<byte> rental for the FFmpeg stdout read buffer — returned after the loop.
/// • Each outgoing chunk is a MemoryPool<float> rental whose ownership is transferred
///   to the channel consumer (VadProcessor), which is responsible for disposing it.
/// • No per-chunk heap allocations inside the hot loop. Zero-allocation byte-to-float casting.
///
/// Network-input advantage
/// ───────────────────────
/// Because we use stdin (pipe:0) and stdout (pipe:1), decoding begins immediately
/// while the client is still uploading the file, effectively reducing disk I/O latency to zero.
/// </summary>
public class AudioProcessor
{
    private const int TargetSampleRate = 16_000;

    /// <summary>
    /// 512 samples @ 16 kHz = 32 ms per frame — the standard Silero VAD window size.
    /// </summary>
    private const int VadChunkSize = 512;

    /// <summary>
    /// 32-bit float = 4 bytes. 512 samples * 4 = 2048 bytes per VAD frame.
    /// </summary>
    private const int BytesPerSample = 4;
    private const int VadChunkBytes = VadChunkSize * BytesPerSample;

    /// <summary>
    /// Streams <paramref name="inputStream"/> into FFmpeg, converts it to 16 kHz mono float PCM,
    /// and writes <see cref="VadChunkSize"/>-sample chunks to <paramref name="outputChannel"/>.
    /// The last chunk is zero-padded when the stream length is not an exact multiple.
    /// Completes the channel writer when done (or on error) so downstream consumers can finish.
    /// </summary>
    public async Task ProcessStreamToChannelAsync(
        Stream inputStream,
        ChannelWriter<IMemoryOwner<float>> outputChannel,
        CancellationToken ct = default)
    {
        // Get the cross-platform path to the locally downloaded FFmpeg binary
        string ffmpegPath = FfmpegManager.GetLocalFfmpegPath();

        // If FFmpeg is not found at the expected path, fallback to "ffmpeg" and hope it's in the system PATH.
        if (!File.Exists(ffmpegPath))
        {
            ffmpegPath = "ffmpeg";
        }

        // ── 1. Configure FFmpeg for Zero-Disk Piping ──────────────────────────
        // -i pipe:0  : Read input from standard input (streaming directly from Kestrel)
        // -ar 16000  : Resample to TargetSampleRate
        // -ac 1      : Downmix to Mono
        // -f f32le   : Output as 32-bit float little-endian (raw PCM bytes)
        // pipe:1     : Write output to standard output
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-i pipe:0 -ar {TargetSampleRate} -ac 1 -f f32le -loglevel quiet pipe:1",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start FFmpeg process.");

            // ── 2. Background Task: Push network stream into FFmpeg ───────────
            var inputTask = Task.Run(async () =>
            {
                try
                {
                    await inputStream.CopyToAsync(process.StandardInput.BaseStream, ct);
                }
                catch (Exception)
                {
                    // Ignore broken pipes if the client drops the connection mid-upload
                }
                finally
                {
                    // CRITICAL: Close the input stream so FFmpeg knows EOF is reached.
                    process.StandardInput.Close();
                }
            }, ct);

            // ── 3. Hot loop — Pull PCM bytes from FFmpeg and push to VAD ──────
            using var stdout = process.StandardOutput.BaseStream;

            // Rent once; each iteration reads 2048 bytes from stdout
            byte[] readBuffer = ArrayPool<byte>.Shared.Rent(VadChunkBytes);
            try
            {
                int bytesRead;
                while (!ct.IsCancellationRequested &&
                       (bytesRead = await ReadExactAsync(stdout, readBuffer, VadChunkBytes, ct)) > 0)
                {
                    // Rent a pooled owner; consumer (VadProcessor) disposes it.
                    IMemoryOwner<float> owner = MemoryPool<float>.Shared.Rent(VadChunkSize);
                    Span<float> dest = owner.Memory.Span[..VadChunkSize];

                    // Zero-allocation memory cast: interpret the float span as a byte span
                    // and copy the raw bytes directly into it.
                    Span<byte> destBytes = MemoryMarshal.Cast<float, byte>(dest);
                    readBuffer.AsSpan(0, bytesRead).CopyTo(destBytes);

                    // Zero-pad the tail when the stream ends mid-frame.
                    int samplesRead = bytesRead / BytesPerSample;
                    if (samplesRead < VadChunkSize)
                    {
                        dest[samplesRead..].Clear();
                    }

                    await outputChannel.WriteAsync(owner, ct);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
            }

            // Await both the input copy task and the FFmpeg process completion
            await Task.WhenAll(inputTask, process.WaitForExitAsync(ct));
        }
        finally
        {
            // Signal to VAD that no more chunks are coming.
            outputChannel.TryComplete();
        }
    }

    /// <summary>
    /// Helper method to ensure we read exactly the requested number of by tes, 
    /// unless the end of the stream is reached.
    /// </summary>
    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
            if (read == 0) break; // End of stream
            totalRead += read;
        }
        return totalRead;
    }
}