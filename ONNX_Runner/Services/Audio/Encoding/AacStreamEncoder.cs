using System.Buffers;
using System.Numerics;

namespace ONNX_Runner.Services.Audio.Encoding;

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

    /// <summary>Creates a mono AAC-LC/ADTS encoder and rents its working buffers without taking ownership of the target stream.</summary>
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

        _sampleRateIndex = Array.IndexOf(AacEncodingTables.SampleRates, sampleRate);

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
        _scalefactorBands = AacEncodingTables.GetScalefactorBands(sampleRate);

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

    /// <summary>Transforms and quantizes one overlapping audio frame, then writes its ADTS packet.</summary>
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

    /// <summary>Calculates the frame payload bit budget from bitrate and sample rate, excluding the ADTS header.</summary>
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

    /// <summary>Searches for a global quantization gain that fits the frame bit budget without coefficient overflow.</summary>
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

    /// <summary>Precomputes spectral magnitudes raised to three quarters for AAC quantization.</summary>
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

    /// <summary>Quantizes the spectrum at the selected gain and reports values beyond the supported magnitude range.</summary>
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

    /// <summary>Assigns zero or spectral codebooks to scalefactor bands and returns the active band count.</summary>
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

    /// <summary>Counts the bits needed for the current single-channel raw data block.</summary>
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

    /// <summary>Counts Huffman, sign, and escape bits for one quantized spectral pair.</summary>
    private static int CountSpectralPairBits(
        int first,
        int second)
    {
        int firstMagnitude = Math.Abs(first);
        int secondMagnitude = Math.Abs(second);

        int index =
            Math.Min(firstMagnitude, 16) * 17 +
            Math.Min(secondMagnitude, 16);

        int bits = AacEncodingTables.SpectralBits11[index];

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

    /// <summary>Counts additional escape bits for a spectral magnitude not represented directly by the codebook.</summary>
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

    /// <summary>Writes the mono AAC raw data block with its gain, band sections, and spectral payload.</summary>
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

    /// <summary>Groups consecutive bands sharing a codebook into AAC section records.</summary>
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

    /// <summary>Writes quantized spectral pairs for nonzero scalefactor bands.</summary>
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

    /// <summary>Writes one Huffman-coded spectral pair with its signs and any escape extensions.</summary>
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
            AacEncodingTables.SpectralCodes11[index],
            AacEncodingTables.SpectralBits11[index]);

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

    /// <summary>Writes the AAC escape extension for a large spectral magnitude.</summary>
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

    /// <summary>Computes the long-window MDCT from overlapping PCM frames using the precomputed FFT tables.</summary>
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

    /// <summary>Reads the appropriate overlapping PCM sample and applies the sine analysis window.</summary>
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

    /// <summary>Runs the in-place forward FFT used by the MDCT calculation.</summary>
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

    /// <summary>Writes the seven-byte mono MPEG-4 AAC-LC ADTS header for the complete packet length.</summary>
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

    /// <summary>Encodes the final pending samples and overlap tail once, then returns working buffers.</summary>
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

    /// <summary>Returns every rented encoder buffer once.</summary>
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

    // Discard incomplete output while returning all rented buffers on cancellation.
    public void Abort()
    {
        if (_finalized)
        {
            return;
        }

        _finalized = true;
        ReturnBuffers();
    }

    /// <summary>Finalizes pending AAC output while leaving the caller-owned target stream open.</summary>
    public void Dispose()
    {
        EnsureFinalized();
        GC.SuppressFinalize(this);
    }

    /// <summary>Precomputes the sine window used by overlapping AAC frames.</summary>
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

    /// <summary>Precomputes real MDCT rotation factors.</summary>
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

    /// <summary>Precomputes imaginary MDCT rotation factors.</summary>
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

    /// <summary>Precomputes real forward-FFT rotation factors.</summary>
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

    /// <summary>Precomputes imaginary forward-FFT rotation factors.</summary>
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

    /// <summary>Precomputes bit-reversed indices for the fixed FFT size.</summary>
    private static ushort[] BuildFftBitReverse()
    {
        var table = new ushort[FftSize];
        int bits = BitOperations.Log2(FftSize);

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
    /// <remarks>Starts a bit writer at the payload offset in the supplied encoder buffer.</remarks>
    private struct AacBitWriter(
        byte[] buffer,
        int startIndex)

    {
        private readonly byte[] _buffer = buffer;
        private int _byteIndex = startIndex;
        private int _bitsUsed = 0;


        /// <summary>Writes the requested low-order bits most significant bit first, rejecting buffer overflow.</summary>
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

        /// <summary>Pads a partial output byte and returns the next available byte offset.</summary>
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
