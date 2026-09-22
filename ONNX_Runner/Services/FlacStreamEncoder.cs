using System.Buffers;
using System.Buffers.Binary;

namespace ONNX_Runner.Services;

/// <summary>
/// Lightweight managed FLAC encoder for Tsubaki's mono 16-bit PCM output.
///
/// Design goals:
/// - no native dependency or external process;
/// - low CPU cost suitable for desktop users and parallel synthesis requests;
/// - true incremental streaming to any writable Stream;
/// - low-latency FLAC subset-friendly 576-sample blocks;
/// - simple fixed predictors and partition-order-0 Rice coding only;
/// - pooled working buffers with no per-frame heap allocation.
///
/// This intentionally favors encoding speed over maximum compression.
/// </summary>
internal sealed class FlacStreamEncoder : IDisposable
{
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BlockSize = 576;
    private const int MaxRiceParameter = 14;
    private const int FrameOutputBufferSize = 16 * 1024;
    private const ulong MaxTotalSamples = 0xFFFFFFFFFUL; // 36 bits

    private readonly Stream _stream;
    private readonly int _sampleRate;
    private readonly short[] _frameBuffer;
    private readonly int[] _residualBuffer;
    private readonly byte[] _outputBuffer;
    private readonly long _streamInfoCombinedFieldPosition;

    private int _frameBufferCount;
    private uint _frameNumber;
    private long _totalSamples;
    private bool _finalized;
    private bool _buffersReturned;

    public FlacStreamEncoder(Stream stream, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanWrite)
        {
            throw new ArgumentException("FLAC target stream must be writable.", nameof(stream));
        }

        if (sampleRate <= 0 || sampleRate > 1_048_575)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "FLAC sample rate must be between 1 and 1048575 Hz.");
        }

        _stream = stream;
        _sampleRate = sampleRate;
        _frameBuffer = ArrayPool<short>.Shared.Rent(BlockSize);
        _residualBuffer = ArrayPool<int>.Shared.Rent(BlockSize);
        _outputBuffer = ArrayPool<byte>.Shared.Rent(FrameOutputBufferSize);

        _streamInfoCombinedFieldPosition = stream.CanSeek
            ? stream.Position + 18
            : -1;

        WriteStreamHeader();
    }

    /// <summary>
    /// Accepts signed mono PCM16 samples and emits complete FLAC frames as soon
    /// as a 576-sample block is available.
    /// </summary>
    public void WriteSamples(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(_finalized, this);

        int sourceIndex = 0;

        while (sourceIndex < samples.Length)
        {
            int available = BlockSize - _frameBufferCount;
            int toCopy = Math.Min(samples.Length - sourceIndex, available);

            samples.Slice(sourceIndex, toCopy)
                .CopyTo(_frameBuffer.AsSpan(_frameBufferCount, toCopy));

            _frameBufferCount += toCopy;
            sourceIndex += toCopy;
            _totalSamples += toCopy;

            if (_frameBufferCount != BlockSize)
            {
                continue;
            }

            EncodeFrame(_frameBuffer.AsSpan(0, BlockSize));
            _frameBufferCount = 0;
        }
    }

    private void WriteStreamHeader()
    {
        Span<byte> header = stackalloc byte[42];
        header.Clear();

        // Native FLAC marker.
        header[0] = (byte)'f';
        header[1] = (byte)'L';
        header[2] = (byte)'a';
        header[3] = (byte)'C';

        // STREAMINFO is both the first and final metadata block.
        header[4] = 0x80;
        header[5] = 0x00;
        header[6] = 0x00;
        header[7] = 34;

        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(8, 2), checked(BlockSize));
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(10, 2), checked(BlockSize));

        // Min/max frame byte sizes are intentionally unknown (0).
        // They are optional hints and avoiding a seek/rewrite keeps streaming simple.

        ulong streamInfo = BuildStreamInfoCombinedField(totalSamples: 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.Slice(18, 8), streamInfo);

        // MD5 is intentionally left as all zeroes. FLAC explicitly permits this
        // when the checksum is unknown, avoiding a second full PCM pass.
        _stream.Write(header);
    }

    private ulong BuildStreamInfoCombinedField(ulong totalSamples)
    {
        return ((ulong)_sampleRate << 44) |
               ((ulong)(Channels - 1) << 41) |
               ((ulong)(BitsPerSample - 1) << 36) |
               (totalSamples & MaxTotalSamples);
    }

    private void EncodeFrame(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        if (_frameNumber > 0x7FFFFFFF)
        {
            throw new InvalidOperationException("FLAC frame number exceeded the 31-bit fixed-block limit.");
        }

        int position = 0;

        _outputBuffer[position++] = 0xFF;
        _outputBuffer[position++] = 0xF8; // 15-bit sync + fixed-block strategy.

        byte blockSizeCode = GetBlockSizeCode(samples.Length, out int blockSizeExtraBytes);
        byte sampleRateCode = GetSampleRateCode(_sampleRate, out int sampleRateExtraBytes);

        _outputBuffer[position++] = (byte)((blockSizeCode << 4) | sampleRateCode);
        _outputBuffer[position++] = 0x08; // Mono, 16-bit, reserved bit = 0.

        position = WriteUtf8UInt31(_outputBuffer, position, _frameNumber);

        if (blockSizeExtraBytes == 1)
        {
            _outputBuffer[position++] = checked((byte)(samples.Length - 1));
        }
        else if (blockSizeExtraBytes == 2)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                _outputBuffer.AsSpan(position, 2),
                checked((ushort)(samples.Length - 1)));
            position += 2;
        }

        position = WriteSampleRateExtra(
            _outputBuffer,
            position,
            sampleRateCode,
            sampleRateExtraBytes,
            _sampleRate);

        byte headerCrc = ComputeCrc8(_outputBuffer.AsSpan(0, position));
        _outputBuffer[position++] = headerCrc;

        var writer = new FrameBitWriter(_outputBuffer, position);

        if (IsConstant(samples))
        {
            WriteConstantSubframe(ref writer, samples[0]);
        }
        else
        {
            WriteCompressedOrVerbatimSubframe(ref writer, samples);
        }

        int frameLengthWithoutCrc = writer.AlignToByte();
        ushort crc = ComputeCrc16(_outputBuffer.AsSpan(0, frameLengthWithoutCrc));

        BinaryPrimitives.WriteUInt16BigEndian(
            _outputBuffer.AsSpan(frameLengthWithoutCrc, 2),
            crc);

        _stream.Write(_outputBuffer, 0, frameLengthWithoutCrc + 2);
        _frameNumber++;
    }

    private void WriteCompressedOrVerbatimSubframe(
        ref FrameBitWriter writer,
        ReadOnlySpan<short> samples)
    {
        int predictorOrder = SelectPredictorOrder(samples);
        int residualCount = BuildResiduals(
            samples,
            predictorOrder,
            _residualBuffer.AsSpan());

        int riceParameter = SelectRiceParameter(
            _residualBuffer.AsSpan(0, residualCount),
            out ulong riceBits);

        ulong fixedBits =
            8UL +
            (ulong)(predictorOrder * BitsPerSample) +
            2UL + // Residual coding method.
            4UL + // Partition order.
            4UL + // Rice parameter.
            riceBits;

        ulong verbatimBits = 8UL + (ulong)samples.Length * BitsPerSample;

        if (fixedBits >= verbatimBits)
        {
            WriteVerbatimSubframe(ref writer, samples);
            return;
        }

        // Reserved 0 bit + six-bit FIXED type (001xxx) + no wasted bits.
        writer.WriteBits(0, 1);
        writer.WriteBits((uint)(8 + predictorOrder), 6);
        writer.WriteBits(0, 1);

        for (int i = 0; i < predictorOrder; i++)
        {
            writer.WriteSigned(samples[i], BitsPerSample);
        }

        // Partitioned Rice, partition order 0: one Rice parameter for the whole frame.
        writer.WriteBits(0, 2);
        writer.WriteBits(0, 4);
        writer.WriteBits((uint)riceParameter, 4);

        WriteRiceResiduals(
            ref writer,
            _residualBuffer.AsSpan(0, residualCount),
            riceParameter);
    }

    private static void WriteConstantSubframe(ref FrameBitWriter writer, short sample)
    {
        writer.WriteBits(0, 1);
        writer.WriteBits(0, 6); // CONSTANT subframe.
        writer.WriteBits(0, 1);
        writer.WriteSigned(sample, BitsPerSample);
    }

    private static void WriteVerbatimSubframe(
        ref FrameBitWriter writer,
        ReadOnlySpan<short> samples)
    {
        writer.WriteBits(0, 1);
        writer.WriteBits(1, 6); // VERBATIM subframe.
        writer.WriteBits(0, 1);

        foreach (short sample in samples)
        {
            writer.WriteSigned(sample, BitsPerSample);
        }
    }

    /// <summary>
    /// Picks only among fixed predictors 0..2. This is intentionally much cheaper
    /// than LPC analysis while still working very well for speech waveforms.
    /// </summary>
    private static int SelectPredictorOrder(ReadOnlySpan<short> samples)
    {
        if (samples.Length <= 1)
        {
            return 0;
        }

        long sum0 = 0;
        long sum1 = 0;
        long sum2 = 0;

        for (int i = 0; i < samples.Length; i++)
        {
            sum0 += Math.Abs((int)samples[i]);

            if (i >= 1)
            {
                sum1 += Math.Abs(samples[i] - samples[i - 1]);
            }

            if (i >= 2)
            {
                int prediction = 2 * samples[i - 1] - samples[i - 2];
                sum2 += Math.Abs(samples[i] - prediction);
            }
        }

        int bestOrder = 0;
        long bestSum = sum0;
        int bestCount = samples.Length;

        if (IsLowerAverage(sum1, samples.Length - 1, bestSum, bestCount))
        {
            bestOrder = 1;
            bestSum = sum1;
            bestCount = samples.Length - 1;
        }

        if (samples.Length >= 3 &&
            IsLowerAverage(sum2, samples.Length - 2, bestSum, bestCount))
        {
            bestOrder = 2;
        }

        return bestOrder;
    }

    private static bool IsLowerAverage(
        long candidateSum,
        int candidateCount,
        long currentSum,
        int currentCount)
    {
        return candidateSum * currentCount < currentSum * candidateCount;
    }

    private static int BuildResiduals(
        ReadOnlySpan<short> samples,
        int order,
        Span<int> residuals)
    {
        int destination = 0;

        for (int i = order; i < samples.Length; i++)
        {
            int prediction = order switch
            {
                0 => 0,
                1 => samples[i - 1],
                2 => 2 * samples[i - 1] - samples[i - 2],
                _ => throw new ArgumentOutOfRangeException(nameof(order))
            };

            residuals[destination++] = samples[i] - prediction;
        }

        return destination;
    }

    private static int SelectRiceParameter(
        ReadOnlySpan<int> residuals,
        out ulong encodedBits)
    {
        if (residuals.IsEmpty)
        {
            encodedBits = 0;
            return 0;
        }

        int bestParameter = 0;
        ulong bestBits = ulong.MaxValue;

        // Exhaustively checking 0..14 is cheap for 576-sample blocks and avoids
        // spending CPU on a more complicated model while still selecting the exact
        // best single-partition Rice parameter.
        for (int parameter = 0; parameter <= MaxRiceParameter; parameter++)
        {
            ulong bits = 0;

            foreach (int residual in residuals)
            {
                uint folded = FoldSigned(residual);
                bits += (folded >> parameter) + 1UL + (uint)parameter;

                if (bits >= bestBits)
                {
                    break;
                }
            }

            if (bits >= bestBits)
            {
                continue;
            }

            bestBits = bits;
            bestParameter = parameter;
        }

        encodedBits = bestBits;
        return bestParameter;
    }

    private static void WriteRiceResiduals(
        ref FrameBitWriter writer,
        ReadOnlySpan<int> residuals,
        int riceParameter)
    {
        uint remainderMask = riceParameter == 0
            ? 0
            : (1u << riceParameter) - 1u;

        foreach (int residual in residuals)
        {
            uint folded = FoldSigned(residual);
            uint quotient = folded >> riceParameter;

            writer.WriteUnary(quotient);

            if (riceParameter != 0)
            {
                writer.WriteBits(folded & remainderMask, riceParameter);
            }
        }
    }

    private static uint FoldSigned(int value)
    {
        return value >= 0
            ? (uint)value << 1
            : ((uint)(-value) << 1) - 1u;
    }

    private static bool IsConstant(ReadOnlySpan<short> samples)
    {
        short first = samples[0];

        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i] != first)
            {
                return false;
            }
        }

        return true;
    }

    private static byte GetBlockSizeCode(int blockSize, out int extraBytes)
    {
        extraBytes = 0;

        switch (blockSize)
        {
            case 192: return 1;
            case 576: return 2;
            case 1152: return 3;
            case 2304: return 4;
            case 4608: return 5;
            case 256: return 8;
            case 512: return 9;
            case 1024: return 10;
            case 2048: return 11;
            case 4096: return 12;
            case 8192: return 13;
            case 16384: return 14;
            case 32768: return 15;
        }

        if (blockSize <= 256)
        {
            extraBytes = 1;
            return 6;
        }

        extraBytes = 2;
        return 7;
    }

    private static byte GetSampleRateCode(int sampleRate, out int extraBytes)
    {
        extraBytes = 0;

        switch (sampleRate)
        {
            case 88200: return 1;
            case 176400: return 2;
            case 192000: return 3;
            case 8000: return 4;
            case 16000: return 5;
            case 22050: return 6;
            case 24000: return 7;
            case 32000: return 8;
            case 44100: return 9;
            case 48000: return 10;
            case 96000: return 11;
        }

        if (sampleRate % 1000 == 0 && sampleRate / 1000 <= byte.MaxValue)
        {
            extraBytes = 1;
            return 12;
        }

        if (sampleRate <= ushort.MaxValue)
        {
            extraBytes = 2;
            return 13;
        }

        if (sampleRate % 10 == 0 && sampleRate / 10 <= ushort.MaxValue)
        {
            extraBytes = 2;
            return 14;
        }

        // Rare sample rates that cannot be represented by an explicit frame-header
        // code are read from STREAMINFO instead.
        return 0;
    }

    private static int WriteSampleRateExtra(
        byte[] buffer,
        int position,
        byte sampleRateCode,
        int extraBytes,
        int sampleRate)
    {
        if (extraBytes == 0)
        {
            return position;
        }

        if (sampleRateCode == 12)
        {
            buffer[position++] = checked((byte)(sampleRate / 1000));
            return position;
        }

        ushort encodedRate = sampleRateCode == 14
            ? checked((ushort)(sampleRate / 10))
            : checked((ushort)sampleRate);

        BinaryPrimitives.WriteUInt16BigEndian(
            buffer.AsSpan(position, 2),
            encodedRate);

        return position + 2;
    }

    private static int WriteUtf8UInt31(byte[] buffer, int position, uint value)
    {
        if (value < 0x80)
        {
            buffer[position++] = (byte)value;
            return position;
        }

        int byteCount;
        byte first;

        if (value < 0x800)
        {
            byteCount = 2;
            first = (byte)(0xC0 | (value >> 6));
        }
        else if (value < 0x10000)
        {
            byteCount = 3;
            first = (byte)(0xE0 | (value >> 12));
        }
        else if (value < 0x200000)
        {
            byteCount = 4;
            first = (byte)(0xF0 | (value >> 18));
        }
        else if (value < 0x4000000)
        {
            byteCount = 5;
            first = (byte)(0xF8 | (value >> 24));
        }
        else
        {
            byteCount = 6;
            first = (byte)(0xFC | (value >> 30));
        }

        buffer[position++] = first;

        for (int shift = (byteCount - 2) * 6; shift >= 0; shift -= 6)
        {
            buffer[position++] = (byte)(0x80 | ((value >> shift) & 0x3F));
        }

        return position;
    }

    private static byte ComputeCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;

        foreach (byte value in data)
        {
            crc ^= value;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (byte)((crc & 0x80) != 0
                    ? (crc << 1) ^ 0x07
                    : crc << 1);
            }
        }

        return crc;
    }

    private static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;

        foreach (byte value in data)
        {
            crc ^= (ushort)(value << 8);

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0
                    ? (crc << 1) ^ 0x8005
                    : crc << 1);
            }
        }

        return crc;
    }

    private void PatchTotalSamples()
    {
        if (_streamInfoCombinedFieldPosition < 0 ||
            !_stream.CanSeek ||
            _totalSamples <= 0 ||
            (ulong)_totalSamples > MaxTotalSamples)
        {
            return;
        }

        long endPosition = _stream.Position;

        try
        {
            _stream.Position = _streamInfoCombinedFieldPosition;

            Span<byte> field = stackalloc byte[8];
            ulong streamInfo = BuildStreamInfoCombinedField((ulong)_totalSamples);
            BinaryPrimitives.WriteUInt64BigEndian(field, streamInfo);
            _stream.Write(field);
        }
        finally
        {
            _stream.Position = endPosition;
        }
    }

    private void EnsureFinalized()
    {
        if (_finalized)
        {
            return;
        }

        _finalized = true;

        try
        {
            if (_frameBufferCount > 0)
            {
                EncodeFrame(_frameBuffer.AsSpan(0, _frameBufferCount));
                _frameBufferCount = 0;
            }

            PatchTotalSamples();
        }
        finally
        {
            ReturnBuffers();
        }
    }

    private void ReturnBuffers()
    {
        if (_buffersReturned)
        {
            return;
        }

        _buffersReturned = true;
        ArrayPool<short>.Shared.Return(_frameBuffer);
        ArrayPool<int>.Shared.Return(_residualBuffer);
        ArrayPool<byte>.Shared.Return(_outputBuffer);
    }

    public void Dispose()
    {
        EnsureFinalized();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Minimal MSB-first bit writer that writes directly into one pooled frame buffer.
    /// </summary>
    private struct FrameBitWriter
    {
        private readonly byte[] _buffer;
        private int _byteIndex;
        private int _bitsUsed;

        public FrameBitWriter(byte[] buffer, int startIndex)
        {
            _buffer = buffer;
            _byteIndex = startIndex;
            _bitsUsed = 0;
        }

        public void WriteSigned(int value, int bitCount)
        {
            ulong mask = (1UL << bitCount) - 1UL;
            WriteBits((ulong)(long)value & mask, bitCount);
        }

        public void WriteUnary(uint zeroCount)
        {
            while (zeroCount >= 32)
            {
                WriteBits(0, 32);
                zeroCount -= 32;
            }

            if (zeroCount != 0)
            {
                WriteBits(0, (int)zeroCount);
            }

            WriteBits(1, 1);
        }

        public void WriteBits(uint value, int bitCount)
        {
            WriteBits((ulong)value, bitCount);
        }

        public void WriteBits(ulong value, int bitCount)
        {
            while (bitCount > 0)
            {
                if (_bitsUsed == 0)
                {
                    if (_byteIndex >= _buffer.Length)
                    {
                        throw new InvalidOperationException("FLAC frame exceeded the pooled output buffer.");
                    }

                    _buffer[_byteIndex] = 0;
                }

                int freeBits = 8 - _bitsUsed;
                int bitsToWrite = Math.Min(bitCount, freeBits);
                int sourceShift = bitCount - bitsToWrite;
                ulong mask = (1UL << bitsToWrite) - 1UL;
                byte chunk = (byte)((value >> sourceShift) & mask);

                _buffer[_byteIndex] |= (byte)(chunk << (freeBits - bitsToWrite));

                _bitsUsed += bitsToWrite;
                bitCount -= bitsToWrite;

                if (_bitsUsed != 8)
                {
                    continue;
                }

                _byteIndex++;
                _bitsUsed = 0;
            }
        }

        public int AlignToByte()
        {
            if (_bitsUsed != 0)
            {
                _byteIndex++;
                _bitsUsed = 0;
            }

            return _byteIndex;
        }
    }
}
