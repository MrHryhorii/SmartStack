using System.Buffers;

namespace ONNX_Runner.Services;

/// <summary>
/// Thin managed wrapper around the native LAME encoder.
/// MP3 psychoacoustics and bitstream generation remain entirely inside LAME.
/// </summary>
internal sealed class LameMp3Encoder : IDisposable
{
    // Native LAME MPEG_mode value for mono output.
    private const int MonoMode = 3;

    // LAME requires this extra safety margin for encoded output and final flush data.
    private const int FlushBufferBytes = 7200;

    private readonly Stream _output;

    // Native encoder state owned by this instance.
    private IntPtr _context;

    // Reused pooled buffer for compressed MP3 bytes.
    private byte[] _outputBuffer;

    private bool _flushed;
    private bool _disposed;

    public LameMp3Encoder(
        Stream output,
        int sampleRate,
        int bitRateKbps = 128)
    {
        ArgumentNullException.ThrowIfNull(
            output);

        if (
            sampleRate < 8000 ||
            sampleRate > 48000
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "LAME supports input sample rates from 8000 to 48000 Hz.");
        }

        if (
            bitRateKbps < 8 ||
            bitRateKbps > 320
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(bitRateKbps),
                bitRateKbps,
                "MP3 bitrate must be between 8 and 320 kbps.");
        }

        // Fail before allocating encoder state when MP3 capability is unavailable.
        if (
            !NativeLibraryResolver.TryLoadLame(
                out _)
        )
        {
            throw new PlatformNotSupportedException(
                "The native LAME MP3 library is not available on this system.");
        }

        _output =
            output;

        // Rent once and grow only when an unusually large input block requires it.
        _outputBuffer =
            ArrayPool<byte>.Shared.Rent(
                GetRequiredOutputBufferSize(
                    sampleRate));

        // Each request owns an independent LAME context; the native library handle is shared.
        _context =
            LameNative.Initialize();

        if (_context == IntPtr.Zero)
        {
            ArrayPool<byte>.Shared.Return(
                _outputBuffer);

            _outputBuffer = [];

            throw new InvalidOperationException(
                "LAME failed to allocate an encoder context.");
        }

        try
        {
            Configure(
                sampleRate,
                bitRateKbps);
        }
        catch
        {
            CloseContext();

            ArrayPool<byte>.Shared.Return(
                _outputBuffer);

            _outputBuffer = [];

            throw;
        }
    }

    public unsafe void WriteSamples(
        short[] samples,
        int sampleCount)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        ArgumentNullException.ThrowIfNull(
            samples);

        if (
            sampleCount < 0 ||
            sampleCount > samples.Length
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleCount));
        }

        if (sampleCount == 0)
        {
            return;
        }

        EnsureOutputCapacity(
            sampleCount);

        int encodedBytes;

        // Pin managed buffers only for the duration of the native call.
        // Mono mode uses the same PCM buffer for both channel pointers and performs no copy.
        fixed (
            short* samplesPointer =
                samples)
        fixed (
            byte* outputPointer =
                _outputBuffer)
        {
            encodedBytes =
                LameNative.EncodeBuffer(
                    _context,
                    samplesPointer,
                    samplesPointer,
                    sampleCount,
                    outputPointer,
                    _outputBuffer.Length);
        }

        if (encodedBytes < 0)
        {
            throw CreateEncodingException(
                "lame_encode_buffer",
                encodedBytes);
        }

        if (encodedBytes > 0)
        {
            _output.Write(
                _outputBuffer,
                0,
                encodedBytes);
        }
    }

    private void Configure(
        int sampleRate,
        int bitRateKbps)
    {
        // All parameters must be set before lame_init_params finalizes encoder configuration.
        EnsureConfigurationSuccess(
            LameNative.SetInputSampleRate(
                _context,
                sampleRate),
            "lame_set_in_samplerate");

        EnsureConfigurationSuccess(
            LameNative.SetChannelCount(
                _context,
                1),
            "lame_set_num_channels");

        EnsureConfigurationSuccess(
            LameNative.SetMode(
                _context,
                MonoMode),
            "lame_set_mode");

        EnsureConfigurationSuccess(
            LameNative.SetBitRate(
                _context,
                bitRateKbps),
            "lame_set_brate");

        // Streaming output cannot seek back to rewrite a Xing/LAME tag.
        // CBR playback does not require it, so keep the stream final from the first byte.
        EnsureConfigurationSuccess(
            LameNative.SetWriteVbrTag(
                _context,
                0),
            "lame_set_bWriteVbrTag");

        EnsureConfigurationSuccess(
            LameNative.InitializeParameters(
                _context),
            "lame_init_params");
    }

    private unsafe void FlushEncoder()
    {
        // LAME buffers delayed samples internally; flush is required to emit the final MP3 frames.
        if (
            _flushed ||
            _context == IntPtr.Zero
        )
        {
            return;
        }

        EnsureOutputCapacityForFlush();

        int encodedBytes;

        fixed (
            byte* outputPointer =
                _outputBuffer)
        {
            encodedBytes =
                LameNative.Flush(
                    _context,
                    outputPointer,
                    _outputBuffer.Length);
        }

        if (encodedBytes < 0)
        {
            throw CreateEncodingException(
                "lame_encode_flush",
                encodedBytes);
        }

        if (encodedBytes > 0)
        {
            _output.Write(
                _outputBuffer,
                0,
                encodedBytes);
        }

        _flushed =
            true;
    }

    private void EnsureOutputCapacity(
        int sampleCount)
    {
        // Preserve the pooled buffer while it is large enough for the current encode call.
        int requiredSize =
            GetRequiredOutputBufferSize(
                sampleCount);

        if (
            _outputBuffer.Length >=
            requiredSize
        )
        {
            return;
        }

        byte[] replacement =
            ArrayPool<byte>.Shared.Rent(
                requiredSize);

        ArrayPool<byte>.Shared.Return(
            _outputBuffer);

        _outputBuffer =
            replacement;
    }

    private void EnsureOutputCapacityForFlush()
    {
        if (
            _outputBuffer.Length >=
            FlushBufferBytes
        )
        {
            return;
        }

        byte[] replacement =
            ArrayPool<byte>.Shared.Rent(
                FlushBufferBytes);

        ArrayPool<byte>.Shared.Return(
            _outputBuffer);

        _outputBuffer =
            replacement;
    }

    private static int GetRequiredOutputBufferSize(
        int sampleCount)
    {
        // LAME's documented safe bound is 1.25 * num_samples + 7200 bytes.
        long required =
            ((long)sampleCount * 5 + 3) /
            4 +
            FlushBufferBytes;

        return checked(
            (int)Math.Max(
                FlushBufferBytes,
                required));
    }

    private static void EnsureConfigurationSuccess(
        int result,
        string operation)
    {
        if (result >= 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{operation} failed with LAME error code {result}.");
    }

    private static Exception CreateEncodingException(
        string operation,
        int result)
    {
        string detail =
            result switch
            {
                -1 => "output buffer was too small",
                -2 => "LAME could not allocate memory",
                -3 => "lame_init_params was not called",
                -4 => "psychoacoustic analysis failed",
                _ => "unknown LAME error"
            };

        return new InvalidOperationException(
            $"{operation} failed with error code {result}: {detail}.");
    }

    private void CloseContext()
    {
        // lame_close releases only per-encoder native state; the shared library stays loaded.
        if (_context == IntPtr.Zero)
        {
            return;
        }

        LameNative.Close(
            _context);

        _context =
            IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Exception? flushError =
            null;

        try
        {
            // Preserve delayed MP3 frames before destroying the native encoder.
            FlushEncoder();
        }
        catch (Exception ex)
        {
            flushError =
                ex;
        }
        finally
        {
            // Native state and pooled memory must be released even when the final flush fails.
            CloseContext();

            if (_outputBuffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(
                    _outputBuffer);

                _outputBuffer = [];
            }

            _disposed =
                true;
        }

        if (flushError != null)
        {
            throw flushError;
        }
    }
}

/// <summary>
/// Direct bindings to LAME exports resolved from the already-loaded native handle.
/// No runtime marshalling and no P/Invoke source generator are involved.
/// </summary>
internal static unsafe class LameNative
{
    // Function pointers are resolved once during type initialization and reused by every encoder.
    // The resolver keeps the owning native library handle alive for the process lifetime.
    private static readonly delegate* unmanaged[Cdecl]<IntPtr>
        InitializePointer =
            (delegate* unmanaged[Cdecl]<IntPtr>)
            NativeLibraryResolver.GetLameExport(
                "lame_init");

    private static readonly delegate* unmanaged[Cdecl]<IntPtr, int, int>
        SetInputSampleRatePointer =
            (delegate* unmanaged[Cdecl]<IntPtr, int, int>)
            NativeLibraryResolver.GetLameExport(
                "lame_set_in_samplerate");

    private static readonly delegate* unmanaged[Cdecl]<IntPtr, int, int>
        SetChannelCountPointer =
            (delegate* unmanaged[Cdecl]<IntPtr, int, int>)
            NativeLibraryResolver.GetLameExport(
                "lame_set_num_channels");

    private static readonly delegate* unmanaged[Cdecl]<IntPtr, int, int>
        SetModePointer =
            (delegate* unmanaged[Cdecl]<IntPtr, int, int>)
            NativeLibraryResolver.GetLameExport(
                "lame_set_mode");

    private static readonly delegate* unmanaged[Cdecl]<IntPtr, int, int>
        SetBitRatePointer =
            (delegate* unmanaged[Cdecl]<IntPtr, int, int>)
            NativeLibraryResolver.GetLameExport(
                "lame_set_brate");

    private static readonly delegate* unmanaged[Cdecl]<IntPtr, int, int>
        SetWriteVbrTagPointer =
            (delegate* unmanaged[Cdecl]<IntPtr, int, int>)
            NativeLibraryResolver.GetLameExport(
                "lame_set_bWriteVbrTag");

    private static readonly delegate* unmanaged[Cdecl]<IntPtr, int>
        InitializeParametersPointer =
            (delegate* unmanaged[Cdecl]<IntPtr, int>)
            NativeLibraryResolver.GetLameExport(
                "lame_init_params");

    // Hot-path encoder call: blittable pointers cross the native boundary without marshalling.
    private static readonly delegate* unmanaged[Cdecl]<
        IntPtr,
        short*,
        short*,
        int,
        byte*,
        int,
        int>
        EncodeBufferPointer =
            (delegate* unmanaged[Cdecl]<
                IntPtr,
                short*,
                short*,
                int,
                byte*,
                int,
                int>)
            NativeLibraryResolver.GetLameExport(
                "lame_encode_buffer");

    private static readonly delegate* unmanaged[Cdecl]<
        IntPtr,
        byte*,
        int,
        int>
        FlushPointer =
            (delegate* unmanaged[Cdecl]<
                IntPtr,
                byte*,
                int,
                int>)
            NativeLibraryResolver.GetLameExport(
                "lame_encode_flush");

    private static readonly delegate* unmanaged[Cdecl]<IntPtr, int>
        ClosePointer =
            (delegate* unmanaged[Cdecl]<IntPtr, int>)
            NativeLibraryResolver.GetLameExport(
                "lame_close");

    public static IntPtr Initialize()
    {
        return InitializePointer();
    }

    public static int SetInputSampleRate(
        IntPtr context,
        int sampleRate)
    {
        return SetInputSampleRatePointer(
            context,
            sampleRate);
    }

    public static int SetChannelCount(
        IntPtr context,
        int channels)
    {
        return SetChannelCountPointer(
            context,
            channels);
    }

    public static int SetMode(
        IntPtr context,
        int mode)
    {
        return SetModePointer(
            context,
            mode);
    }

    public static int SetBitRate(
        IntPtr context,
        int bitRateKbps)
    {
        return SetBitRatePointer(
            context,
            bitRateKbps);
    }

    public static int SetWriteVbrTag(
        IntPtr context,
        int writeTag)
    {
        return SetWriteVbrTagPointer(
            context,
            writeTag);
    }

    public static int InitializeParameters(
        IntPtr context)
    {
        return InitializeParametersPointer(
            context);
    }

    public static int EncodeBuffer(
        IntPtr context,
        short* left,
        short* right,
        int sampleCount,
        byte* output,
        int outputSize)
    {
        return EncodeBufferPointer(
            context,
            left,
            right,
            sampleCount,
            output,
            outputSize);
    }

    public static int Flush(
        IntPtr context,
        byte* output,
        int outputSize)
    {
        return FlushPointer(
            context,
            output,
            outputSize);
    }

    public static int Close(
        IntPtr context)
    {
        return ClosePointer(
            context);
    }
}
