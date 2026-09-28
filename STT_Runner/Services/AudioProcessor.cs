using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace STT_Runner.Services;

public sealed class AudioProcessor
{
    private const int FrameBytes = 512 * sizeof(float);

    public async Task ProcessStreamToChannelAsync(
        Stream inputStream,
        ChannelWriter<IMemoryOwner<float>> outputChannel,
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
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-ar", "16000", "-ac", "1", "-f", "f32le", "pipe:1" })
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

                IMemoryOwner<float> owner = MemoryPool<float>.Shared.Rent(512);
                try
                {
                    Span<float> frame = owner.Memory.Span[..512];
                    frame.Clear();
                    buffer.AsSpan(0, count).CopyTo(MemoryMarshal.AsBytes(frame));
                    await outputChannel.WriteAsync(owner, token);
                    owner = null!;
                }
                finally
                {
                    owner?.Dispose();
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

    private static async Task CopyInputAsync(Stream input, Stream ffmpegInput, CancellationToken ct)
    {
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
