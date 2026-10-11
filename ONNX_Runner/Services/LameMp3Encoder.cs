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

    /// <summary>Creates and configures a mono native LAME encoder with pooled output, optionally enforcing the output rate.</summary>
    public LameMp3Encoder(
        Stream output,
        int sampleRate,
        int bitRateKbps = 128,
        bool enforceSampleRate = false)
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
                bitRateKbps,
                enforceSampleRate);
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

    /// <summary>Encodes valid mono PCM16 samples and writes produced MP3 bytes to the caller-owned stream.</summary>
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

    /// <summary>Sets LAME input, output, channel, and bitrate parameters before finalizing encoder configuration.</summary>
    private void Configure(
        int sampleRate,
        int bitRateKbps,
        bool enforceSampleRate)
    {
        // All parameters must be set before lame_init_params finalizes encoder configuration.
        EnsureConfigurationSuccess(
            LameNative.SetInputSampleRate(
                _context,
                sampleRate),
            "lame_set_in_samplerate");

        if (enforceSampleRate)
        {
            // The request resolver already selected a legal MP3 rate. Do not let
            // LAME silently choose another one after response headers have been sent.
            EnsureConfigurationSuccess(
                LameNative.SetOutputSampleRate(_context, sampleRate),
                "lame_set_out_samplerate");
        }

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

    /// <summary>Drains LAME's delayed MP3 frames once and writes any final bytes.</summary>
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

    /// <summary>Ensures the pooled output buffer can hold LAME's bound for the next input block.</summary>
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

    /// <summary>Ensures the output buffer can hold the final LAME flush.</summary>
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

    /// <summary>Calculates LAME's required output capacity with checked integer bounds.</summary>
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

    /// <summary>Converts a negative native configuration result into an operation-specific failure.</summary>
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

    /// <summary>Maps a LAME encoding error code to a readable exception.</summary>
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

    /// <summary>Releases the per-encoder native context without unloading the shared LAME library.</summary>
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

    // A canceled response must release the native encoder without writing its final frames.
    public void Abort()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            CloseContext();
        }
        finally
        {
            if (_outputBuffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_outputBuffer);
                _outputBuffer = [];
            }

            _disposed = true;
        }
    }

    /// <summary>Flushes pending audio and releases native and pooled resources even when finalization fails.</summary>
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
            Abort();
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
        SetOutputSampleRatePointer =
            (delegate* unmanaged[Cdecl]<IntPtr, int, int>)
            NativeLibraryResolver.GetLameExport(
                "lame_set_out_samplerate");

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

    /// <summary>Allocates a native LAME encoder context; its caller must close the returned handle.</summary>
    public static IntPtr Initialize()
    {
        return InitializePointer();
    }

    /// <summary>Sets the native encoder's input PCM sample rate in Hz.</summary>
    public static int SetInputSampleRate(
        IntPtr context,
        int sampleRate)
    {
        return SetInputSampleRatePointer(
            context,
            sampleRate);
    }

    /// <summary>Sets the native encoder's requested output sample rate in Hz.</summary>
    public static int SetOutputSampleRate(IntPtr context, int sampleRate)
    {
        return SetOutputSampleRatePointer(context, sampleRate);
    }

    /// <summary>Sets the number of input channels before parameter initialization.</summary>
    public static int SetChannelCount(
        IntPtr context,
        int channels)
    {
        return SetChannelCountPointer(
            context,
            channels);
    }

    /// <summary>Sets the native MPEG channel mode before parameter initialization.</summary>
    public static int SetMode(
        IntPtr context,
        int mode)
    {
        return SetModePointer(
            context,
            mode);
    }

    /// <summary>Sets the native encoder's target bitrate in kilobits per second.</summary>
    public static int SetBitRate(
        IntPtr context,
        int bitRateKbps)
    {
        return SetBitRatePointer(
            context,
            bitRateKbps);
    }

    /// <summary>Enables or disables the seek-dependent VBR metadata tag.</summary>
    public static int SetWriteVbrTag(
        IntPtr context,
        int writeTag)
    {
        return SetWriteVbrTagPointer(
            context,
            writeTag);
    }

    /// <summary>Finalizes native encoding parameters after all configuration setters.</summary>
    public static int InitializeParameters(
        IntPtr context)
    {
        return InitializeParametersPointer(
            context);
    }

    /// <summary>Encodes planar PCM16 input into the supplied output region and returns bytes written or an error code.</summary>
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

    /// <summary>Flushes delayed native frames into the supplied buffer and returns bytes written or an error code.</summary>
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

    /// <summary>Releases one native LAME encoder context.</summary>
    public static int Close(
        IntPtr context)
    {
        return ClosePointer(
            context);
    }
}
