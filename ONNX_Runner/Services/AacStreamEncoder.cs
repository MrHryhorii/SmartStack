using System.Buffers;
using System.Numerics;

namespace ONNX_Runner.Services;

/// <summary>
/// Lightweight managed AAC-LC encoder specialized for Tsubaki's mono speech output.
///
/// Design goals:
/// - no native dependency or external process;
/// - true incremental ADTS streaming to any writable Stream;
/// - direct float PCM input, avoiding an intermediate PCM16 conversion;
/// - one conservative AAC-LC subset: mono, long windows, target-bitrate quantization;
/// - pooled per-request working buffers and process-wide immutable transform tables.
///
/// The first implementation intentionally omits short windows, TNS, PNS, VBR control,
/// stereo tools, and MP4/M4A muxing. It favors portability and low CPU cost for speech
/// over maximum compression efficiency for music.
/// </summary>
internal sealed class AacStreamEncoder : IDisposable
{
    private const int FrameSamples = 1024;
    private const int WindowSamples = 2048;
    private const int FftSize = 512;
    private const int AdtsHeaderBytes = 7;
    private const int MaxQuantizedMagnitude = 8191;
    private const int MaxRawDataBits = 6144;
    private const int OutputBufferBytes = 16 * 1024;
    private const double QuantizerOffset = 0.4054;
    private const double PcmScale = 32768.0;

    private static readonly int[] SampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000,
        24000, 22050, 16000, 12000, 11025, 8000
    ];

    private static readonly int[] Swb96000 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64,
        72, 80, 88, 96, 108, 120, 132, 144, 156, 172, 188, 212, 240, 276,
        320, 384, 448, 512, 576, 640, 704, 768, 832, 896, 960, 1024
    ];

    private static readonly int[] Swb64000 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64,
        72, 80, 88, 100, 112, 124, 140, 156, 172, 192, 216, 240, 268, 304,
        344, 384, 424, 464, 504, 544, 584, 624, 664, 704, 744, 784, 824, 864,
        904, 944, 984, 1024
    ];

    private static readonly int[] Swb48000 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80,
        88, 96, 108, 120, 132, 144, 160, 176, 196, 216, 240, 264, 292, 320,
        352, 384, 416, 448, 480, 512, 544, 576, 608, 640, 672, 704, 736, 768,
        800, 832, 864, 896, 928, 1024
    ];

    private static readonly int[] Swb32000 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80,
        88, 96, 108, 120, 132, 144, 160, 176, 196, 216, 240, 264, 292, 320,
        352, 384, 416, 448, 480, 512, 544, 576, 608, 640, 672, 704, 736, 768,
        800, 832, 864, 896, 928, 960, 992, 1024
    ];

    private static readonly int[] Swb24000 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 52, 60, 68, 76,
        84, 92, 100, 108, 116, 124, 136, 148, 160, 172, 188, 204, 220, 240,
        260, 284, 308, 336, 364, 396, 432, 468, 508, 552, 600, 652, 704, 768,
        832, 896, 960, 1024
    ];

    private static readonly int[] Swb16000 =
    [
        0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 100, 112, 124, 136,
        148, 160, 172, 184, 196, 212, 228, 244, 260, 280, 300, 320, 344, 368,
        396, 424, 456, 492, 532, 572, 616, 664, 716, 772, 832, 896, 960, 1024
    ];

    private static readonly int[] Swb8000 =
    [
        0, 12, 24, 36, 48, 60, 72, 84, 96, 108, 120, 132, 144, 156, 172, 188,
        204, 220, 236, 252, 268, 288, 308, 328, 348, 372, 396, 420, 448, 476,
        508, 544, 580, 620, 664, 712, 764, 820, 880, 944, 1024
    ];

    private static readonly byte[] SpectralBits11 =
    [
        4, 5, 6, 7, 8, 8, 9, 10, 10, 10, 11, 11, 12, 11, 12, 12,
        10, 5, 4, 5, 6, 7, 7, 8, 8, 9, 9, 9, 10, 10, 10, 10,
        11, 8, 6, 5, 5, 6, 7, 7, 8, 8, 8, 9, 9, 9, 10, 10,
        10, 10, 8, 7, 6, 6, 6, 7, 7, 8, 8, 8, 9, 9, 9, 10,
        10, 10, 10, 8, 8, 7, 7, 7, 7, 8, 8, 8, 8, 9, 9, 9,
        10, 10, 10, 10, 8, 8, 7, 7, 7, 7, 8, 8, 8, 9, 9, 9,
        9, 10, 10, 10, 10, 8, 9, 8, 8, 8, 8, 8, 8, 8, 9, 9,
        9, 10, 10, 10, 10, 10, 8, 9, 8, 8, 8, 8, 8, 8, 9, 9,
        9, 10, 10, 10, 10, 10, 10, 8, 10, 9, 8, 8, 9, 9, 9, 9,
        9, 10, 10, 10, 10, 10, 10, 11, 8, 10, 9, 9, 9, 9, 9, 9,
        9, 10, 10, 10, 10, 10, 10, 11, 11, 8, 11, 9, 9, 9, 9, 9,
        9, 10, 10, 10, 10, 10, 11, 10, 11, 11, 8, 11, 10, 9, 9, 10,
        9, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 8, 11, 10, 10, 10,
        10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 9, 11, 10, 9,
        9, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 9, 11, 10,
        10, 10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 9, 12,
        10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 12, 12, 9,
        9, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 9,
        5,
    ];

    private static readonly ushort[] SpectralCodes11 =
    [
        0x0, 0x6, 0x19, 0x3D, 0x9C, 0xC6, 0x1A7, 0x390, 0x3C2, 0x3DF, 0x7E6, 0x7F3, 0xFFB, 0x7EC, 0xFFA, 0xFFE,
        0x38E, 0x5, 0x1, 0x8, 0x14, 0x37, 0x42, 0x92, 0xAF, 0x191, 0x1A5, 0x1B5, 0x39E, 0x3C0, 0x3A2, 0x3CD,
        0x7D6, 0xAE, 0x17, 0x7, 0x9, 0x18, 0x39, 0x40, 0x8E, 0xA3, 0xB8, 0x199, 0x1AC, 0x1C1, 0x3B1, 0x396,
        0x3BE, 0x3CA, 0x9D, 0x3C, 0x15, 0x16, 0x1A, 0x3B, 0x44, 0x91, 0xA5, 0xBE, 0x196, 0x1AE, 0x1B9, 0x3A1,
        0x391, 0x3A5, 0x3D5, 0x94, 0x9A, 0x36, 0x38, 0x3A, 0x41, 0x8C, 0x9B, 0xB0, 0xC3, 0x19E, 0x1AB, 0x1BC,
        0x39F, 0x38F, 0x3A9, 0x3CF, 0x93, 0xBF, 0x3E, 0x3F, 0x43, 0x45, 0x9E, 0xA7, 0xB9, 0x194, 0x1A2, 0x1BA,
        0x1C3, 0x3A6, 0x3A7, 0x3BB, 0x3D4, 0x9F, 0x1A0, 0x8F, 0x8D, 0x90, 0x98, 0xA6, 0xB6, 0xC4, 0x19F, 0x1AF,
        0x1BF, 0x399, 0x3BF, 0x3B4, 0x3C9, 0x3E7, 0xA8, 0x1B6, 0xAB, 0xA4, 0xAA, 0xB2, 0xC2, 0xC5, 0x198, 0x1A4,
        0x1B8, 0x38C, 0x3A4, 0x3C4, 0x3C6, 0x3DD, 0x3E8, 0xAD, 0x3AF, 0x192, 0xBD, 0xBC, 0x18E, 0x197, 0x19A, 0x1A3,
        0x1B1, 0x38D, 0x398, 0x3B7, 0x3D3, 0x3D1, 0x3DB, 0x7DD, 0xB4, 0x3DE, 0x1A9, 0x19B, 0x19C, 0x1A1, 0x1AA, 0x1AD,
        0x1B3, 0x38B, 0x3B2, 0x3B8, 0x3CE, 0x3E1, 0x3E0, 0x7D2, 0x7E5, 0xB7, 0x7E3, 0x1BB, 0x1A8, 0x1A6, 0x1B0, 0x1B2,
        0x1B7, 0x39B, 0x39A, 0x3BA, 0x3B5, 0x3D6, 0x7D7, 0x3E4, 0x7D8, 0x7EA, 0xBA, 0x7E8, 0x3A0, 0x1BD, 0x1B4, 0x38A,
        0x1C4, 0x392, 0x3AA, 0x3B0, 0x3BC, 0x3D7, 0x7D4, 0x7DC, 0x7DB, 0x7D5, 0x7F0, 0xC1, 0x7FB, 0x3C8, 0x3A3, 0x395,
        0x39D, 0x3AC, 0x3AE, 0x3C5, 0x3D8, 0x3E2, 0x3E6, 0x7E4, 0x7E7, 0x7E0, 0x7E9, 0x7F7, 0x190, 0x7F2, 0x393, 0x1BE,
        0x1C0, 0x394, 0x397, 0x3AD, 0x3C3, 0x3C1, 0x3D2, 0x7DA, 0x7D9, 0x7DF, 0x7EB, 0x7F4, 0x7FA, 0x195, 0x7F8, 0x3BD,
        0x39C, 0x3AB, 0x3A8, 0x3B3, 0x3B9, 0x3D0, 0x3E3, 0x3E5, 0x7E2, 0x7DE, 0x7ED, 0x7F1, 0x7F9, 0x7FC, 0x193, 0xFFD,
        0x3DC, 0x3B6, 0x3C7, 0x3CC, 0x3CB, 0x3D9, 0x3DA, 0x7D3, 0x7E1, 0x7EE, 0x7EF, 0x7F5, 0x7F6, 0xFFC, 0xFFF, 0x19D,
        0x1C2, 0xB5, 0xA1, 0x96, 0x97, 0x95, 0x99, 0xA0, 0xA2, 0xAC, 0xA9, 0xB1, 0xB3, 0xBB, 0xC0, 0x18F,
        0x4,
    ];

    private static readonly double[] SineWindow = BuildSineWindow();
    private static readonly double[] MdctTwiddleReal = BuildMdctTwiddleReal();
    private static readonly double[] MdctTwiddleImag = BuildMdctTwiddleImag();
    private static readonly double[] FftTwiddleReal = BuildFftTwiddleReal();
    private static readonly double[] FftTwiddleImag = BuildFftTwiddleImag();
    private static readonly ushort[] FftBitReverse = BuildFftBitReverse();

    private readonly Stream _stream;
    private readonly int _sampleRate;
    private readonly int _sampleRateIndex;
    private readonly int _bitRate;
    private readonly int[] _scalefactorBands;

    private readonly float[] _previousSamples;
    private readonly float[] _currentSamples;
    private readonly double[] _folded;
    private readonly double[] _fftReal;
    private readonly double[] _fftImag;
    private readonly double[] _spectrum;
    private readonly double[] _pow34;
    private readonly short[] _quantized;
    private readonly byte[] _bandCodebooks;
    private readonly byte[] _outputBuffer;

    private int _currentSampleCount;
    private bool _hasInput;
    private bool _finalized;
    private bool _buffersReturned;

    public AacStreamEncoder(
        Stream stream,
        int sampleRate,
        int bitRateKbps = 96)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanWrite)
        {
            throw new ArgumentException(
                "AAC target stream must be writable.",
                nameof(stream));
        }

        _sampleRateIndex = Array.IndexOf(SampleRates, sampleRate);

        if (_sampleRateIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "AAC-LC requires a standard AAC sample rate: 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, or 96000 Hz.");
        }

        if (bitRateKbps < 16 || bitRateKbps > 320)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bitRateKbps),
                bitRateKbps,
                "AAC target bitrate must be between 16 and 320 kbps.");
        }

        _stream = stream;
        _sampleRate = sampleRate;
        _bitRate = checked(bitRateKbps * 1000);
        _scalefactorBands = GetScalefactorBands(sampleRate);

        _previousSamples = ArrayPool<float>.Shared.Rent(FrameSamples);
        _currentSamples = ArrayPool<float>.Shared.Rent(FrameSamples);
        _folded = ArrayPool<double>.Shared.Rent(FrameSamples);
        _fftReal = ArrayPool<double>.Shared.Rent(FftSize);
        _fftImag = ArrayPool<double>.Shared.Rent(FftSize);
        _spectrum = ArrayPool<double>.Shared.Rent(FrameSamples);
        _pow34 = ArrayPool<double>.Shared.Rent(FrameSamples);
        _quantized = ArrayPool<short>.Shared.Rent(FrameSamples);
        _bandCodebooks = ArrayPool<byte>.Shared.Rent(_scalefactorBands.Length - 1);
        _outputBuffer = ArrayPool<byte>.Shared.Rent(OutputBufferBytes);

        _previousSamples.AsSpan(0, FrameSamples).Clear();
        _currentSamples.AsSpan(0, FrameSamples).Clear();
    }

    /// <summary>
    /// Accepts normalized mono float PCM and emits one complete ADTS frame for every
    /// 1024 input samples. The initial zero overlap is part of normal AAC transform delay.
    /// </summary>
    public void WriteSamples(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_finalized, this);

        if (samples.IsEmpty)
        {
            return;
        }

        _hasInput = true;
        int sourceIndex = 0;

        while (sourceIndex < samples.Length)
        {
            int available = FrameSamples - _currentSampleCount;
            int toCopy = Math.Min(samples.Length - sourceIndex, available);

            samples.Slice(sourceIndex, toCopy)
                .CopyTo(_currentSamples.AsSpan(_currentSampleCount, toCopy));

            _currentSampleCount += toCopy;
            sourceIndex += toCopy;

            if (_currentSampleCount != FrameSamples)
            {
                continue;
            }

            EncodeFrame(
                _previousSamples.AsSpan(0, FrameSamples),
                _currentSamples.AsSpan(0, FrameSamples));

            _currentSamples.AsSpan(0, FrameSamples)
                .CopyTo(_previousSamples);

            _currentSampleCount = 0;
        }
    }

    private void EncodeFrame(
        ReadOnlySpan<float> previous,
        ReadOnlySpan<float> current)
    {
        ComputeMdct(previous, current);
        BuildPow34();

        int targetRawBits = GetTargetRawBits();
        int globalGain = SelectGlobalGain(targetRawBits);

        Quantize(globalGain, out bool overflow);

        if (overflow)
        {
            throw new InvalidOperationException(
                "AAC quantizer could not fit coefficients into the supported escape range.");
        }

        int maxSfb = BuildBandCodebooks();

        var writer = new AacBitWriter(
            _outputBuffer,
            AdtsHeaderBytes);

        WriteRawDataBlock(
            ref writer,
            globalGain,
            maxSfb);

        int frameLength = writer.AlignToByte();

        if (frameLength > 0x1FFF)
        {
            throw new InvalidOperationException(
                $"AAC ADTS frame exceeded the 13-bit frame length limit: {frameLength} bytes.");
        }

        WriteAdtsHeader(
            _outputBuffer.AsSpan(0, AdtsHeaderBytes),
            frameLength);

        _stream.Write(
            _outputBuffer,
            0,
            frameLength);
    }

    private int GetTargetRawBits()
    {
        int totalTargetBits = checked((int)Math.Round(
            _bitRate * FrameSamples / (double)_sampleRate));

        int rawTargetBits = Math.Max(
            256,
            totalTargetBits - AdtsHeaderBytes * 8);

        return Math.Min(
            MaxRawDataBits,
            rawTargetBits);
    }

    private int SelectGlobalGain(int targetRawBits)
    {
        int low = 0;
        int high = 255;
        int bestGain = 255;
        bool found = false;

        while (low <= high)
        {
            int candidate = low + ((high - low) >> 1);

            Quantize(
                candidate,
                out bool overflow);

            int maxSfb = BuildBandCodebooks();
            int bits = overflow
                ? int.MaxValue
                : CountRawDataBits(maxSfb);

            if (
                overflow ||
                bits > targetRawBits
            )
            {
                low = candidate + 1;
                continue;
            }

            found = true;
            bestGain = candidate;
            high = candidate - 1;
        }

        return found
            ? bestGain
            : 255;
    }

    private void BuildPow34()
    {
        for (int i = 0; i < FrameSamples; i++)
        {
            double magnitude = Math.Abs(_spectrum[i]);

            _pow34[i] = magnitude == 0.0
                ? 0.0
                : Math.Sqrt(magnitude * Math.Sqrt(magnitude));
        }
    }

    private void Quantize(
        int globalGain,
        out bool overflow)
    {
        double scale = Math.Pow(
            2.0,
            -3.0 * (globalGain - 100) / 16.0);

        overflow = false;

        for (int i = 0; i < FrameSamples; i++)
        {
            int magnitude = (int)Math.Floor(
                _pow34[i] * scale + QuantizerOffset);

            if (magnitude > MaxQuantizedMagnitude)
            {
                overflow = true;
                magnitude = MaxQuantizedMagnitude;
            }

            _quantized[i] = _spectrum[i] < 0.0
                ? (short)-magnitude
                : (short)magnitude;
        }
    }

    private int BuildBandCodebooks()
    {
        int maxSfb = 0;
        int bandCount = _scalefactorBands.Length - 1;

        for (int band = 0; band < bandCount; band++)
        {
            int start = _scalefactorBands[band];
            int end = _scalefactorBands[band + 1];
            bool nonZero = false;

            for (int i = start; i < end; i++)
            {
                if (_quantized[i] == 0)
                {
                    continue;
                }

                nonZero = true;
                break;
            }

            _bandCodebooks[band] = nonZero
                ? (byte)11
                : (byte)0;

            if (nonZero)
            {
                maxSfb = band + 1;
            }
        }

        return maxSfb;
    }

    private int CountRawDataBits(int maxSfb)
    {
        // SCE ID + element tag + global_gain + long-window ICS info.
        int bits =
            3 +
            4 +
            8 +
            1 +
            2 +
            1 +
            6 +
            1;

        int band = 0;

        while (band < maxSfb)
        {
            byte codebook = _bandCodebooks[band];
            int end = band + 1;

            while (
                end < maxSfb &&
                _bandCodebooks[end] == codebook
            )
            {
                end++;
            }

            int sectionLength = end - band;
            bits += 4;
            bits += 5 * (sectionLength / 31 + 1);
            band = end;
        }

        // Flat scalefactors: every nonzero band uses differential value zero,
        // whose AAC scalefactor Huffman code is a single zero bit.
        for (band = 0; band < maxSfb; band++)
        {
            if (_bandCodebooks[band] != 0)
            {
                bits++;
            }
        }

        // pulse_data_present, tns_data_present, gain_control_data_present.
        bits += 3;

        for (band = 0; band < maxSfb; band++)
        {
            if (_bandCodebooks[band] == 0)
            {
                continue;
            }

            int start = _scalefactorBands[band];
            int end = _scalefactorBands[band + 1];

            for (int i = start; i < end; i += 2)
            {
                bits += CountSpectralPairBits(
                    _quantized[i],
                    _quantized[i + 1]);
            }
        }

        bits += 3; // ID_END.

        return (bits + 7) & ~7;
    }

    private static int CountSpectralPairBits(
        int first,
        int second)
    {
        int firstMagnitude = Math.Abs(first);
        int secondMagnitude = Math.Abs(second);

        int index =
            Math.Min(firstMagnitude, 16) * 17 +
            Math.Min(secondMagnitude, 16);

        int bits = SpectralBits11[index];

        if (first != 0)
        {
            bits++;
        }

        if (second != 0)
        {
            bits++;
        }

        bits += CountEscapeBits(firstMagnitude);
        bits += CountEscapeBits(secondMagnitude);

        return bits;
    }

    private static int CountEscapeBits(int magnitude)
    {
        if (magnitude < 16)
        {
            return 0;
        }

        int exponent = BitOperations.Log2((uint)magnitude);

        // Unary prefix length (exponent - 4 ones + zero) followed by exponent low bits.
        return 2 * exponent - 3;
    }

    private void WriteRawDataBlock(
        ref AacBitWriter writer,
        int globalGain,
        int maxSfb)
    {
        writer.WriteBits(0, 3); // ID_SCE.
        writer.WriteBits(0, 4); // element_instance_tag.
        writer.WriteBits((uint)globalGain, 8);

        // Individual Channel Stream info: ONLY_LONG_SEQUENCE with sine window.
        writer.WriteBits(0, 1); // ics_reserved_bit.
        writer.WriteBits(0, 2); // window_sequence = ONLY_LONG_SEQUENCE.
        writer.WriteBits(0, 1); // window_shape = sine.
        writer.WriteBits((uint)maxSfb, 6);
        writer.WriteBits(0, 1); // predictor_data_present.

        WriteSections(
            ref writer,
            maxSfb);

        // Flat scalefactors: spectral bands share global_gain exactly.
        for (int band = 0; band < maxSfb; band++)
        {
            if (_bandCodebooks[band] != 0)
            {
                writer.WriteBits(0, 1); // scalefactor differential 0.
            }
        }

        writer.WriteBits(0, 1); // pulse_data_present.
        writer.WriteBits(0, 1); // tns_data_present.
        writer.WriteBits(0, 1); // gain_control_data_present.

        WriteSpectralData(
            ref writer,
            maxSfb);

        writer.WriteBits(7, 3); // ID_END.
    }

    private void WriteSections(
        ref AacBitWriter writer,
        int maxSfb)
    {
        int band = 0;

        while (band < maxSfb)
        {
            byte codebook = _bandCodebooks[band];
            int end = band + 1;

            while (
                end < maxSfb &&
                _bandCodebooks[end] == codebook
            )
            {
                end++;
            }

            int sectionLength = end - band;

            writer.WriteBits(
                codebook,
                4);

            while (sectionLength >= 31)
            {
                writer.WriteBits(31, 5);
                sectionLength -= 31;
            }

            writer.WriteBits(
                (uint)sectionLength,
                5);

            band = end;
        }
    }

    private void WriteSpectralData(
        ref AacBitWriter writer,
        int maxSfb)
    {
        for (int band = 0; band < maxSfb; band++)
        {
            if (_bandCodebooks[band] == 0)
            {
                continue;
            }

            int start = _scalefactorBands[band];
            int end = _scalefactorBands[band + 1];

            for (int i = start; i < end; i += 2)
            {
                WriteSpectralPair(
                    ref writer,
                    _quantized[i],
                    _quantized[i + 1]);
            }
        }
    }

    private static void WriteSpectralPair(
        ref AacBitWriter writer,
        int first,
        int second)
    {
        int firstMagnitude = Math.Abs(first);
        int secondMagnitude = Math.Abs(second);

        int index =
            Math.Min(firstMagnitude, 16) * 17 +
            Math.Min(secondMagnitude, 16);

        writer.WriteBits(
            SpectralCodes11[index],
            SpectralBits11[index]);

        if (first != 0)
        {
            writer.WriteBits(
                first < 0 ? 1u : 0u,
                1);
        }

        if (second != 0)
        {
            writer.WriteBits(
                second < 0 ? 1u : 0u,
                1);
        }

        WriteEscape(
            ref writer,
            firstMagnitude);

        WriteEscape(
            ref writer,
            secondMagnitude);
    }

    private static void WriteEscape(
        ref AacBitWriter writer,
        int magnitude)
    {
        if (magnitude < 16)
        {
            return;
        }

        int exponent = BitOperations.Log2((uint)magnitude);
        int unaryOnes = exponent - 4;

        if (unaryOnes > 0)
        {
            writer.WriteBits(
                (1u << unaryOnes) - 1u,
                unaryOnes);
        }

        writer.WriteBits(0, 1);

        uint baseMagnitude = 1u << exponent;
        writer.WriteBits(
            (uint)magnitude - baseMagnitude,
            exponent);
    }

    private void ComputeMdct(
        ReadOnlySpan<float> previous,
        ReadOnlySpan<float> current)
    {
        const int quarter = FrameSamples / 2;

        for (int n = 0; n < quarter; n++)
        {
            _folded[n] =
                -GetWindowedSample(previous, current, 3 * quarter - 1 - n) -
                GetWindowedSample(previous, current, 3 * quarter + n);

            _folded[quarter + n] =
                GetWindowedSample(previous, current, n) -
                GetWindowedSample(previous, current, FrameSamples - 1 - n);
        }

        for (int n = 0; n < FftSize; n++)
        {
            double real = _folded[2 * n];
            double imaginary = _folded[FrameSamples - 1 - 2 * n];
            double twiddleReal = MdctTwiddleReal[n];
            double twiddleImaginary = MdctTwiddleImag[n];

            _fftReal[n] =
                real * twiddleReal -
                imaginary * twiddleImaginary;

            _fftImag[n] =
                real * twiddleImaginary +
                imaginary * twiddleReal;
        }

        ForwardFft();

        for (int k = 0; k < FftSize; k++)
        {
            double twiddleReal = MdctTwiddleReal[k];
            double twiddleImaginary = MdctTwiddleImag[k];

            double real =
                _fftReal[k] * twiddleReal -
                _fftImag[k] * twiddleImaginary;

            double imaginary =
                _fftReal[k] * twiddleImaginary +
                _fftImag[k] * twiddleReal;

            _spectrum[2 * k] = 2.0 * real;
            _spectrum[FrameSamples - 1 - 2 * k] = -2.0 * imaginary;
        }
    }

    private static double GetWindowedSample(
        ReadOnlySpan<float> previous,
        ReadOnlySpan<float> current,
        int index)
    {
        float sample = index < FrameSamples
            ? previous[index]
            : current[index - FrameSamples];

        sample = Math.Clamp(
            sample,
            -1.0f,
            1.0f);

        return sample * PcmScale * SineWindow[index];
    }

    private void ForwardFft()
    {
        for (int i = 0; i < FftSize; i++)
        {
            int reversed = FftBitReverse[i];

            if (reversed <= i)
            {
                continue;
            }

            (_fftReal[i], _fftReal[reversed]) =
                (_fftReal[reversed], _fftReal[i]);

            (_fftImag[i], _fftImag[reversed]) =
                (_fftImag[reversed], _fftImag[i]);
        }

        for (int length = 2; length <= FftSize; length <<= 1)
        {
            int half = length >> 1;
            int twiddleStride = FftSize / length;

            for (int block = 0; block < FftSize; block += length)
            {
                for (int j = 0; j < half; j++)
                {
                    int twiddleIndex = j * twiddleStride;
                    double twiddleReal = FftTwiddleReal[twiddleIndex];
                    double twiddleImaginary = FftTwiddleImag[twiddleIndex];

                    int even = block + j;
                    int odd = even + half;

                    double oddReal =
                        _fftReal[odd] * twiddleReal -
                        _fftImag[odd] * twiddleImaginary;

                    double oddImaginary =
                        _fftReal[odd] * twiddleImaginary +
                        _fftImag[odd] * twiddleReal;

                    double evenReal = _fftReal[even];
                    double evenImaginary = _fftImag[even];

                    _fftReal[even] = evenReal + oddReal;
                    _fftImag[even] = evenImaginary + oddImaginary;
                    _fftReal[odd] = evenReal - oddReal;
                    _fftImag[odd] = evenImaginary - oddImaginary;
                }
            }
        }
    }

    private void WriteAdtsHeader(
        Span<byte> header,
        int frameLength)
    {
        const int profile = 1; // ADTS profile 1 => AAC-LC (Audio Object Type 2).
        const int channelConfiguration = 1; // Mono.

        header[0] = 0xFF;
        header[1] = 0xF1; // MPEG-4, layer 0, no CRC.
        header[2] = (byte)(
            (profile << 6) |
            (_sampleRateIndex << 2) |
            ((channelConfiguration >> 2) & 0x01));

        header[3] = (byte)(
            ((channelConfiguration & 0x03) << 6) |
            ((frameLength >> 11) & 0x03));

        header[4] = (byte)((frameLength >> 3) & 0xFF);

        // 0x7FF buffer fullness marks variable frame size / unconstrained bit reservoir.
        header[5] = (byte)(
            ((frameLength & 0x07) << 5) |
            0x1F);

        header[6] = 0xFC; // fullness low bits + one raw_data_block.
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
            if (!_hasInput)
            {
                return;
            }

            if (_currentSampleCount > 0)
            {
                _currentSamples.AsSpan(
                        _currentSampleCount,
                        FrameSamples - _currentSampleCount)
                    .Clear();

                EncodeFrame(
                    _previousSamples.AsSpan(0, FrameSamples),
                    _currentSamples.AsSpan(0, FrameSamples));

                _currentSamples.AsSpan(0, FrameSamples)
                    .CopyTo(_previousSamples);

                _currentSampleCount = 0;
            }

            _currentSamples.AsSpan(0, FrameSamples).Clear();

            // One zero-overlap tail frame flushes the final half-window of the last
            // real input block. ADTS carries no gapless trim metadata, so this normal
            // AAC priming/tail padding remains visible to a raw decoder.
            EncodeFrame(
                _previousSamples.AsSpan(0, FrameSamples),
                _currentSamples.AsSpan(0, FrameSamples));
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

        ArrayPool<float>.Shared.Return(_previousSamples);
        ArrayPool<float>.Shared.Return(_currentSamples);
        ArrayPool<double>.Shared.Return(_folded);
        ArrayPool<double>.Shared.Return(_fftReal);
        ArrayPool<double>.Shared.Return(_fftImag);
        ArrayPool<double>.Shared.Return(_spectrum);
        ArrayPool<double>.Shared.Return(_pow34);
        ArrayPool<short>.Shared.Return(_quantized);
        ArrayPool<byte>.Shared.Return(_bandCodebooks);
        ArrayPool<byte>.Shared.Return(_outputBuffer);
    }

    public void Dispose()
    {
        EnsureFinalized();
        GC.SuppressFinalize(this);
    }

    private static int[] GetScalefactorBands(int sampleRate)
    {
        return sampleRate switch
        {
            96000 or 88200 => Swb96000,
            64000 => Swb64000,
            48000 or 44100 => Swb48000,
            32000 => Swb32000,
            24000 or 22050 => Swb24000,
            16000 or 12000 or 11025 => Swb16000,
            8000 => Swb8000,
            _ => throw new ArgumentOutOfRangeException(nameof(sampleRate))
        };
    }

    private static double[] BuildSineWindow()
    {
        var table = new double[WindowSamples];

        for (int i = 0; i < table.Length; i++)
        {
            table[i] = Math.Sin(
                Math.PI / WindowSamples * (i + 0.5));
        }

        return table;
    }

    private static double[] BuildMdctTwiddleReal()
    {
        var table = new double[FftSize];

        for (int i = 0; i < table.Length; i++)
        {
            double angle = Math.PI * (i + 0.125) / FrameSamples;
            table[i] = Math.Cos(angle);
        }

        return table;
    }

    private static double[] BuildMdctTwiddleImag()
    {
        var table = new double[FftSize];

        for (int i = 0; i < table.Length; i++)
        {
            double angle = Math.PI * (i + 0.125) / FrameSamples;
            table[i] = -Math.Sin(angle);
        }

        return table;
    }

    private static double[] BuildFftTwiddleReal()
    {
        var table = new double[FftSize / 2];

        for (int i = 0; i < table.Length; i++)
        {
            double angle = 2.0 * Math.PI * i / FftSize;
            table[i] = Math.Cos(angle);
        }

        return table;
    }

    private static double[] BuildFftTwiddleImag()
    {
        var table = new double[FftSize / 2];

        for (int i = 0; i < table.Length; i++)
        {
            double angle = 2.0 * Math.PI * i / FftSize;
            table[i] = -Math.Sin(angle);
        }

        return table;
    }

    private static ushort[] BuildFftBitReverse()
    {
        var table = new ushort[FftSize];
        int bits = BitOperations.Log2((uint)FftSize);

        for (int value = 0; value < FftSize; value++)
        {
            uint source = (uint)value;
            uint reversed = 0;

            for (int bit = 0; bit < bits; bit++)
            {
                reversed = (reversed << 1) | (source & 1u);
                source >>= 1;
            }

            table[value] = checked((ushort)reversed);
        }

        return table;
    }

    /// <summary>
    /// Minimal MSB-first bit writer for one AAC raw_data_block inside a pooled ADTS buffer.
    /// </summary>
    private struct AacBitWriter
    {
        private readonly byte[] _buffer;
        private int _byteIndex;
        private int _bitsUsed;

        public AacBitWriter(
            byte[] buffer,
            int startIndex)
        {
            _buffer = buffer;
            _byteIndex = startIndex;
            _bitsUsed = 0;
        }

        public void WriteBits(
            uint value,
            int bitCount)
        {
            while (bitCount > 0)
            {
                if (_bitsUsed == 0)
                {
                    if (_byteIndex >= _buffer.Length)
                    {
                        throw new InvalidOperationException(
                            "AAC frame exceeded the pooled output buffer.");
                    }

                    _buffer[_byteIndex] = 0;
                }

                int freeBits = 8 - _bitsUsed;
                int bitsToWrite = Math.Min(bitCount, freeBits);
                int sourceShift = bitCount - bitsToWrite;
                uint mask = (1u << bitsToWrite) - 1u;
                byte chunk = (byte)((value >> sourceShift) & mask);

                _buffer[_byteIndex] |= (byte)(
                    chunk << (freeBits - bitsToWrite));

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
