using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace STT_Runner.Services;

/// <summary>
/// Converts an incoming audio stream to 16 kHz mono float PCM using one FFmpeg
/// process per request. The bounded output channel controls upstream reading.
/// </summary>
public sealed class AudioProcessor
{
    // One VAD window is 512 samples, or 32 ms at 16 kHz.
    private const int FrameBytes = 512 * sizeof(float);

    /// <summary>
    /// Writes padded 512-sample frames and transfers each pooled array to the channel.
    /// Completes the channel with the original failure when decoding fails.
    /// </summary>
    public async Task ProcessStreamToChannelAsync(
        Stream inputStream,
        ChannelWriter<float[]> outputChannel,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(FfmpegManager.ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in FfmpegArguments)
            startInfo.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start FFmpeg.");
        }
        catch (Exception ex)
        {
            outputChannel.TryComplete(ex);
            throw;
        }
        using (process)
        {
        // Feed stdin and drain stderr concurrently to avoid blocking FFmpeg's pipes.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = linked.Token;
        Task inputTask = CopyInputAsync(inputStream, process.StandardInput.BaseStream, token);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(token);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(FrameBytes);
        Exception? failure = null;

        try
        {
            while (true)
            {
                int count = await ReadFrameAsync(process.StandardOutput.BaseStream, buffer, token);
                if (count == 0) break;
                if (count % sizeof(float) != 0)
                    throw new InvalidDataException("FFmpeg returned incomplete float PCM data.");

                float[] frame = ArrayPool<float>.Shared.Rent(512);
                try
                {
                    // Zero padding is needed only for the final partial VAD window.
                    if (count < FrameBytes)
                        frame.AsSpan(count / sizeof(float), 512 - count / sizeof(float)).Clear();
                    buffer.AsSpan(0, count).CopyTo(MemoryMarshal.AsBytes(frame.AsSpan(0, 512)));
                    await outputChannel.WriteAsync(frame, token);
                    frame = null!;
                }
                finally
                {
                    if (frame is not null) ArrayPool<float>.Shared.Return(frame);
                }
            }

            await inputTask;
            await process.WaitForExitAsync(token);
            string errors = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidDataException($"FFmpeg exited with code {process.ExitCode}: {errors.Trim()}");
        }
        catch (Exception ex)
        {
            failure = ex;
            // Cancel a stalled upload and stop the child process on any stage failure.
            linked.Cancel();
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            try { await inputTask; } catch (Exception) { }
            try { await errorTask; } catch (Exception) { }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            outputChannel.TryComplete(failure);
        }
        }
    }

    private static readonly string[] FfmpegArguments =
        ["-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-ar", "16000", "-ac", "1", "-f", "f32le", "pipe:1"];

    private static async Task CopyInputAsync(Stream input, Stream ffmpegInput, CancellationToken ct)
    {
        // Closing stdin tells FFmpeg that the upload has ended.
        try
        {
            await input.CopyToAsync(ffmpegInput, ct);
            await ffmpegInput.FlushAsync(ct);
        }
        finally
        {
            await ffmpegInput.DisposeAsync();
        }
    }

    private static async Task<int> ReadFrameAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        // A pipe read may return fewer bytes than one VAD window without reaching EOF.
        int count = 0;
        while (count < FrameBytes)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count, FrameBytes - count), ct);
            if (read == 0) break;
            count += read;
        }
        return count;
    }
}
