using NAudio.Wave;
using ONNX_Runner.Models;
using System.Buffers;
using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;

namespace ONNX_Runner.Services;

/// <summary>
/// Manages the encoding, formatting, and routing of generated audio streams.
/// Supports dynamic format switching (WAV, MP3, OPUS, AAC, FLAC, PCM) and handles both
/// in-memory buffering and real-time chunked network streaming.
///
/// Ogg/Opus is muxed internally instead of using Concentus.OggFile.
/// Concentus is used only for raw Opus packet encoding.
/// </summary>
public class AudioStreamManager : IDisposable
{
    private const int OpusPacketMaxBytes = 1275;
    private const int OpusGranuleRate = 48000;
    private const int OpusFrameDurationMs = 20;
    private const int OpusFrameGranules = OpusGranuleRate * OpusFrameDurationMs / 1000; // 960

    private readonly Stream _baseStream;
    private readonly Stream? _audioWriter;
    private readonly LameMp3Encoder? _lameEncoder;
    private readonly AudioFormat _format;

    private readonly IOpusEncoder? _opusEncoder;
    private readonly OggOpusMuxer? _oggMuxer;
    private readonly FlacStreamEncoder? _flacEncoder;
    private readonly AacStreamEncoder? _aacEncoder;
    private readonly short[]? _opusFrameBuffer;
    private readonly byte[]? _opusPacketBuffer;
    private readonly int _opusFrameSize;
    private readonly int _opusSampleRate;
    private readonly int _opusPreSkip48k;

    private int _opusBufferCount;
    private long _opusEncodedGranule48k;
    private long _samplesWritten;
    private bool _finalized;
    private bool _opusBuffersReturned;

    /// <summary>
    /// Total logical PCM samples accepted by the output path.
    /// Includes sentence pauses and generated reverb tails, but excludes codec padding.
    /// </summary>
    public long SamplesWritten => _samplesWritten;

    /// <summary>
    /// Initializes the audio output path for the selected format and configures
    /// the required encoder or raw PCM writer.
    /// </summary>
    public AudioStreamManager(AudioFormat format, int sampleRate, Stream targetStream)
    {
        _format = format;
        _baseStream = targetStream;

        if (_format == AudioFormat.Mp3 || _format == AudioFormat.B64Json)
        {
            _lameEncoder = new LameMp3Encoder(
                _baseStream,
                sampleRate,
                128);

            return;
        }

        if (_format == AudioFormat.Wav)
        {
            var waveFormat = new WaveFormat(sampleRate, 16, 1);
            _audioWriter = new WaveFileWriter(_baseStream, waveFormat);
            return;
        }

        if (_format == AudioFormat.Opus)
        {
            ValidateOpusSampleRate(sampleRate);

            _opusSampleRate = sampleRate;
            _opusFrameSize = sampleRate / (1000 / OpusFrameDurationMs);

            _opusEncoder = OpusCodecFactory.CreateEncoder(
                sampleRate,
                1,
                OpusApplication.OPUS_APPLICATION_VOIP);

            // RFC 7845 stores pre-skip in 48 kHz granule units, while Concentus
            // reports encoder lookahead in samples at the encoder input rate.
            _opusPreSkip48k = checked((int)ScaleToOpusGranules(
                _opusEncoder.Lookahead,
                sampleRate));

            if ((uint)_opusPreSkip48k > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Opus encoder pre-skip is too large: {_opusPreSkip48k} samples at 48 kHz.");
            }

            _opusFrameBuffer = ArrayPool<short>.Shared.Rent(_opusFrameSize);
            _opusPacketBuffer = ArrayPool<byte>.Shared.Rent(OpusPacketMaxBytes);

            string vendor = $"Tsubaki TTS Engine / {_opusEncoder.GetVersionString()}";

            _oggMuxer = new OggOpusMuxer(
                _baseStream,
                inputSampleRate: sampleRate,
                channels: 1,
                preSkip: (ushort)_opusPreSkip48k,
                vendor: vendor);

            return;
        }

        if (_format == AudioFormat.Aac)
        {
            _aacEncoder = new AacStreamEncoder(
                _baseStream,
                sampleRate,
                96);

            return;
        }

        if (_format == AudioFormat.Flac)
        {
            _flacEncoder = new FlacStreamEncoder(_baseStream, sampleRate);
            return;
        }

        // AudioFormat.Pcm: raw little-endian signed 16-bit mono PCM.
        _audioWriter = _baseStream;
    }

    /// <summary>
    /// Processes a chunk of raw 32-bit float audio, applies optional DSP filtering,
    /// converts it to 16-bit PCM, and writes it to the selected encoder.
    /// </summary>
    public void WriteChunk(Span<float> samples, NAudio.Dsp.BiQuadFilter? filter = null)
    {
        ObjectDisposedException.ThrowIf(_finalized, this);

        if (samples.IsEmpty)
        {
            return;
        }

        if (filter != null)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = filter.Transform(samples[i]);
            }
        }

        // Managed AAC accepts normalized float PCM directly. Keep it before the shared
        // PCM16 conversion so AAC adds neither a temporary short[] nor an extra pass.
        if (_format == AudioFormat.Aac)
        {
            _aacEncoder!.WriteSamples(samples);
            _samplesWritten += samples.Length;
            return;
        }

        short[] shortSamples = ArrayPool<short>.Shared.Rent(samples.Length);

        try
        {
            ConvertFloatToPcm16(samples, shortSamples);

            if (_format == AudioFormat.Mp3 || _format == AudioFormat.B64Json)
            {
                _lameEncoder!.WriteSamples(
                    shortSamples,
                    samples.Length);
            }
            else if (_format == AudioFormat.Opus)
            {
                WriteOpusSamples(shortSamples.AsSpan(0, samples.Length));
            }
            else if (_format == AudioFormat.Flac)
            {
                _flacEncoder!.WriteSamples(shortSamples.AsSpan(0, samples.Length));
            }
            else if (_audioWriter != null)
            {
                WritePcm16Bytes(shortSamples, samples.Length);
            }
        }
        finally
        {
            ArrayPool<short>.Shared.Return(shortSamples);
        }

        _samplesWritten += samples.Length;
    }

    /// <summary>
    /// Converts normalized 32-bit float samples to clamped 16-bit PCM,
    /// using SIMD acceleration where available.
    /// </summary>
    private static void ConvertFloatToPcm16(
        ReadOnlySpan<float> samples,
        Span<short> destination)
    {
        int vectorSize = System.Numerics.Vector<float>.Count;
        int i = 0;

        var minVec = new System.Numerics.Vector<float>(-1f);
        var maxVec = new System.Numerics.Vector<float>(1f);
        var multVec = new System.Numerics.Vector<float>(32767f);

        for (; i <= samples.Length - vectorSize; i += vectorSize)
        {
            var source = new System.Numerics.Vector<float>(samples.Slice(i));
            var clamped = System.Numerics.Vector.Max(
                minVec,
                System.Numerics.Vector.Min(maxVec, source));

            var scaled = clamped * multVec;

            for (int k = 0; k < vectorSize; k++)
            {
                destination[i + k] = (short)scaled[k];
            }
        }

        for (; i < samples.Length; i++)
        {
            float sample = Math.Clamp(samples[i], -1f, 1f) * 32767f;
            destination[i] = (short)sample;
        }
    }

    /// <summary>
    /// Writes 16-bit PCM samples to the configured output writer through
    /// a pooled byte buffer.
    /// </summary>
    private void WritePcm16Bytes(short[] shortSamples, int sampleCount)
    {
        int requiredBytes = checked(sampleCount * sizeof(short));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(requiredBytes);

        try
        {
            Buffer.BlockCopy(
                shortSamples,
                0,
                buffer,
                0,
                requiredBytes);

            _audioWriter!.Write(buffer, 0, requiredBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Accumulates 16-bit PCM samples into fixed 20 ms Opus frames and
    /// encodes each complete frame as soon as it becomes available.
    /// </summary>
    private void WriteOpusSamples(ReadOnlySpan<short> samples)
    {
        if (_opusEncoder == null ||
            _oggMuxer == null ||
            _opusFrameBuffer == null ||
            _opusPacketBuffer == null)
        {
            throw new InvalidOperationException("Opus encoder is not initialized.");
        }

        int sourceIndex = 0;

        while (sourceIndex < samples.Length)
        {
            int spaceInFrame = _opusFrameSize - _opusBufferCount;
            int toCopy = Math.Min(samples.Length - sourceIndex, spaceInFrame);

            samples.Slice(sourceIndex, toCopy)
                .CopyTo(_opusFrameBuffer.AsSpan(_opusBufferCount, toCopy));

            _opusBufferCount += toCopy;
            sourceIndex += toCopy;

            if (_opusBufferCount != _opusFrameSize)
            {
                continue;
            }

            EncodeCurrentOpusFrame();
        }
    }

    /// <summary>
    /// Encodes exactly one 20 ms PCM frame into one raw Opus packet and hands
    /// that packet to the internal RFC 7845 Ogg muxer.
    /// </summary>
    private void EncodeCurrentOpusFrame()
    {
        if (_opusEncoder == null ||
            _oggMuxer == null ||
            _opusFrameBuffer == null ||
            _opusPacketBuffer == null)
        {
            throw new InvalidOperationException("Opus encoder is not initialized.");
        }

        int packetSize = _opusEncoder.Encode(
            _opusFrameBuffer.AsSpan(0, _opusFrameSize),
            _opusFrameSize,
            _opusPacketBuffer.AsSpan(0, OpusPacketMaxBytes),
            OpusPacketMaxBytes);

        if (packetSize <= 0 || packetSize > OpusPacketMaxBytes)
        {
            throw new InvalidOperationException(
                $"Concentus returned an invalid Opus packet size: {packetSize}.");
        }

        _opusEncodedGranule48k += OpusFrameGranules;

        _oggMuxer.WriteAudioPacket(
            _opusPacketBuffer.AsSpan(0, packetSize),
            _opusEncodedGranule48k);

        _opusBufferCount = 0;
    }

    /// <summary>
    /// Finalizes Ogg/Opus sample-accurately.
    ///
    /// A compliant Opus file cannot merely set pre-skip and stop after the final
    /// user PCM frame. The decoder must receive enough trailing codec padding to
    /// cover the encoder lookahead. The EOS page granule position then trims that
    /// padding back to:
    ///
    ///     preSkip + real input duration
    ///
    /// This preserves the original audible duration while allowing the decoder to
    /// discard encoder delay at the beginning.
    /// </summary>
    private void FinalizeOpus()
    {
        if (_opusEncoder == null ||
            _oggMuxer == null ||
            _opusFrameBuffer == null)
        {
            return;
        }

        long logicalDuration48k = ScaleToOpusGranules(
            _samplesWritten,
            _opusSampleRate);

        long finalGranule = checked(
            logicalDuration48k + _opusPreSkip48k);

        long remainingGranules = finalGranule - _opusEncodedGranule48k;

        // Normally this is one or two frames:
        //   - one frame to finish an incomplete 20 ms PCM block,
        //   - possibly one additional frame to cover encoder pre-skip/lookahead.
        //
        // Keep this generic instead of assuming a particular encoder delay.
        if (remainingGranules > 0)
        {
            long framesToEncode =
                (remainingGranules + OpusFrameGranules - 1) /
                OpusFrameGranules;

            for (long frame = 0; frame < framesToEncode; frame++)
            {
                // Preserve any real PCM already accumulated in the first final frame.
                // Everything after it is codec padding and must be silence.
                Array.Clear(
                    _opusFrameBuffer,
                    _opusBufferCount,
                    _opusFrameSize - _opusBufferCount);

                _opusBufferCount = _opusFrameSize;
                EncodeCurrentOpusFrame();
            }
        }

        // The muxer intentionally keeps the newest audio page pending so EOS can
        // be placed directly on the final page instead of emitting an invalid
        // zero-byte packet/page after the audio.
        _oggMuxer.Complete(finalGranule);
    }

    /// <summary>
    /// Converts a sample count at the input rate to the fixed 48 kHz
    /// granule timebase used by Ogg Opus.
    /// </summary>
    private static long ScaleToOpusGranules(long samples, int sampleRate)
    {
        // Every legal Opus API sample rate divides 48 kHz exactly.
        return checked(samples * OpusGranuleRate / sampleRate);
    }

    /// <summary>
    /// Validates that the input sample rate is supported by the Opus encoder.
    /// </summary>
    private static void ValidateOpusSampleRate(int sampleRate)
    {
        if (sampleRate is 8000 or 12000 or 16000 or 24000 or 48000)
        {
            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(sampleRate),
            sampleRate,
            "Opus supports 8000, 12000, 16000, 24000, or 48000 Hz input.");
    }

    /// <summary>
    /// Finalizes the active output format exactly once and releases
    /// format-specific encoder resources.
    /// </summary>
    private void EnsureFinalized()
    {
        if (_finalized)
        {
            return;
        }

        _finalized = true;

        try
        {
            if (_format == AudioFormat.Opus)
            {
                FinalizeOpus();
                return;
            }

            if (_format == AudioFormat.Aac)
            {
                _aacEncoder?.Dispose();
                return;
            }

            if (_format == AudioFormat.Flac)
            {
                _flacEncoder?.Dispose();
                return;
            }

            if (_format == AudioFormat.Mp3 || _format == AudioFormat.B64Json)
            {
                _lameEncoder?.Dispose();
                return;
            }

            if (_format != AudioFormat.Pcm)
            {
                _audioWriter?.Dispose();
            }
        }
        finally
        {
            if (_format == AudioFormat.Opus)
            {
                _opusEncoder?.Dispose();
                _oggMuxer?.Dispose();
                ReturnOpusBuffers();
            }
        }
    }

    /// <summary>
    /// Returns the pooled Opus frame and packet buffers exactly once.
    /// </summary>
    private void ReturnOpusBuffers()
    {
        if (_opusBuffersReturned)
        {
            return;
        }

        _opusBuffersReturned = true;

        if (_opusFrameBuffer != null)
        {
            ArrayPool<short>.Shared.Return(_opusFrameBuffer);
        }

        if (_opusPacketBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_opusPacketBuffer);
        }
    }

    /// <summary>
    /// Returns the HTTP MIME type associated with an output audio format.
    /// </summary>
    public static string GetMimeType(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.Mp3 => "audio/mpeg",
            AudioFormat.Opus => "audio/ogg; codecs=opus",
            AudioFormat.Aac => "audio/aac",
            AudioFormat.Flac => "audio/flac",
            AudioFormat.Pcm => "audio/pcm",
            AudioFormat.B64Json => "application/json",
            _ => "audio/wav"
        };
    }

    /// <summary>
    /// Returns the default download file name associated with an output audio format.
    /// </summary>
    public static string GetFileName(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.Mp3 => "speech.mp3",
            AudioFormat.Opus => "speech.opus",
            AudioFormat.Aac => "speech.aac",
            AudioFormat.Flac => "speech.flac",
            AudioFormat.Pcm => "speech.pcm",
            AudioFormat.B64Json => "speech.json",
            _ => "speech.wav"
        };
    }

    /// <summary>
    /// Releases encoder resources without writing to a failed or disconnected response.
    /// </summary>
    public void Abort()
    {
        if (_finalized)
        {
            return;
        }

        _finalized = true;

        try
        {
            _lameEncoder?.Abort();
            _aacEncoder?.Abort();
            _flacEncoder?.Abort();
        }
        finally
        {
            if (_format == AudioFormat.Opus)
            {
                _opusEncoder?.Dispose();
                _oggMuxer?.Dispose();
                ReturnOpusBuffers();
            }
        }
    }

    /// <summary>
    /// Finalizes the output stream and releases encoder resources.
    /// </summary>
    public void Dispose()
    {
        EnsureFinalized();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Minimal RFC 3533 / RFC 7845 Ogg Opus muxer.
    ///
    /// Design constraints for Tsubaki:
    /// - mono Opus only;
    /// - one logical Ogg stream;
    /// - Opus packets never span pages (max Opus packet is only 1275 bytes);
    /// - keeps a small group of packets per page for low streaming latency;
    /// - keeps the newest page pending so EOS is attached to real audio data;
    /// - never owns/disposes the HTTP/Memory target stream.
    /// </summary>
    private sealed class OggOpusMuxer : IDisposable
    {
        private const int OggHeaderSize = 27;
        private const int MaxLacingSegments = 255;
        private const int MaxOggPagePayload = 65025;

        // Five 20 ms packets = 100 ms of audio per page.
        // The page is emitted when the next packet arrives, so worst-case muxer
        // latency is about 120 ms while still keeping Ogg overhead modest.
        private const int MaxPacketsPerPage = 5;

        private const byte ContinuedPacketFlag = 0x01;
        private const byte BeginningOfStreamFlag = 0x02;
        private const byte EndOfStreamFlag = 0x04;

        private static readonly uint[] CrcTable = BuildCrcTable();

        private readonly Stream _output;
        private readonly uint _streamSerial;

        private readonly byte[] _pageHeader;
        private readonly byte[] _segmentTable;
        private readonly byte[] _pagePayload;

        private int _segmentCount;
        private int _payloadLength;
        private int _packetCount;
        private long _pageGranulePosition;
        private long _lastFlushedAudioGranule;
        private uint _pageSequence;
        private bool _completed;
        private bool _disposed;

        /// <summary>
        /// Initializes a single logical Ogg Opus stream and writes the
        /// mandatory OpusHead and OpusTags header packets.
        /// </summary>
        public OggOpusMuxer(
            Stream output,
            int inputSampleRate,
            byte channels,
            ushort preSkip,
            string vendor)
        {
            if (channels == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channels));
            }

            _output = output;
            _streamSerial = unchecked((uint)Random.Shared.NextInt64(1, uint.MaxValue));

            _pageHeader = new byte[OggHeaderSize];
            _segmentTable = ArrayPool<byte>.Shared.Rent(MaxLacingSegments);
            _pagePayload = ArrayPool<byte>.Shared.Rent(MaxOggPagePayload);

            WriteOpusHead(
                inputSampleRate,
                channels,
                preSkip);

            WriteOpusTags(vendor);
        }

        /// <summary>
        /// Appends one raw Opus packet to the pending Ogg audio page,
        /// flushing the previous page first when capacity limits require it.
        /// </summary>
        public void WriteAudioPacket(
            ReadOnlySpan<byte> packet,
            long granulePosition)
        {
            if (_completed)
            {
                throw new InvalidOperationException(
                    "Cannot write Opus packets after the Ogg stream has been completed.");
            }

            if (packet.IsEmpty)
            {
                throw new ArgumentException(
                    "An Ogg Opus audio packet cannot be empty.",
                    nameof(packet));
            }

            if (packet.Length > OpusPacketMaxBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(packet),
                    packet.Length,
                    "An Opus packet cannot exceed 1275 bytes.");
            }

            int requiredSegments = GetLacingSegmentCount(packet.Length);

            // Deliberately flush BEFORE adding the new packet. That leaves the newest
            // page pending, allowing Complete() to set EOS directly on a real audio page.
            if (_packetCount >= MaxPacketsPerPage ||
                _segmentCount + requiredSegments > MaxLacingSegments ||
                _payloadLength + packet.Length > MaxOggPagePayload)
            {
                FlushAudioPage(
                    flags: 0,
                    granulePosition: _pageGranulePosition);
            }

            AppendPacket(packet);

            _packetCount++;
            _pageGranulePosition = granulePosition;
        }

        /// <summary>
        /// Completes the Ogg stream by marking the pending final audio page
        /// with EOS and the exact final granule position.
        /// </summary>
        public void Complete(long finalGranulePosition)
        {
            if (_completed)
            {
                return;
            }

            if (_packetCount == 0)
            {
                throw new InvalidOperationException(
                    "Cannot complete Ogg Opus without a final audio packet.");
            }

            if (finalGranulePosition < _lastFlushedAudioGranule)
            {
                throw new InvalidOperationException(
                    $"Final Opus granule {finalGranulePosition} is before the previous page granule {_lastFlushedAudioGranule}.");
            }

            if (finalGranulePosition > _pageGranulePosition)
            {
                throw new InvalidOperationException(
                    $"Final Opus granule {finalGranulePosition} exceeds encoded audio granule {_pageGranulePosition}.");
            }

            FlushAudioPage(
                flags: EndOfStreamFlag,
                granulePosition: finalGranulePosition);

            _output.Flush();
            _completed = true;
        }

        /// <summary>
        /// Writes the mandatory OpusHead identification packet on the
        /// beginning-of-stream Ogg page.
        /// </summary>
        private void WriteOpusHead(
            int inputSampleRate,
            byte channels,
            ushort preSkip)
        {
            Span<byte> packet = stackalloc byte[19];
            "OpusHead"u8.CopyTo(packet);

            packet[8] = 1;          // OpusHead version
            packet[9] = channels;

            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.Slice(10, 2),
                preSkip);

            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.Slice(12, 4),
                checked((uint)inputSampleRate));

            BinaryPrimitives.WriteInt16LittleEndian(
                packet.Slice(16, 2),
                0);                 // output gain Q8

            packet[18] = 0;         // channel mapping family 0 (mono/stereo)

            WriteStandalonePacketPage(
                packet,
                flags: BeginningOfStreamFlag,
                granulePosition: 0,
                flushOutput: false);
        }

        /// <summary>
        /// Writes the mandatory OpusTags packet containing the encoder vendor
        /// string and no user comments.
        /// </summary>
        private void WriteOpusTags(string vendor)
        {
            byte[] vendorBytes = System.Text.Encoding.UTF8.GetBytes(
                string.IsNullOrWhiteSpace(vendor)
                    ? "Tsubaki TTS Engine"
                    : vendor);

            int packetLength = checked(16 + vendorBytes.Length);
            byte[] packet = ArrayPool<byte>.Shared.Rent(packetLength);

            try
            {
                Span<byte> payload = packet.AsSpan(0, packetLength);
                payload.Clear();

                "OpusTags"u8.CopyTo(payload);

                BinaryPrimitives.WriteUInt32LittleEndian(
                    payload.Slice(8, 4),
                    checked((uint)vendorBytes.Length));

                vendorBytes.AsSpan().CopyTo(
                    payload.Slice(12, vendorBytes.Length));

                // No user comments.
                BinaryPrimitives.WriteUInt32LittleEndian(
                    payload.Slice(12 + vendorBytes.Length, 4),
                    0);

                WriteStandalonePacketPage(
                    payload,
                    flags: 0,
                    granulePosition: 0,
                    flushOutput: false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }

        /// <summary>
        /// Writes one complete packet as a standalone Ogg page with the
        /// supplied flags and granule position.
        /// </summary>
        private void WriteStandalonePacketPage(
            ReadOnlySpan<byte> packet,
            byte flags,
            long granulePosition,
            bool flushOutput)
        {
            int segmentCount = BuildLacingTable(
                packet.Length,
                _segmentTable);

            WritePage(
                _segmentTable.AsSpan(0, segmentCount),
                packet,
                flags,
                granulePosition,
                flushOutput);
        }

        /// <summary>
        /// Appends a packet and its lacing values to the current pending
        /// audio page without emitting the page.
        /// </summary>
        private void AppendPacket(ReadOnlySpan<byte> packet)
        {
            int segmentsWritten = BuildLacingTable(
                packet.Length,
                _segmentTable.AsSpan(_segmentCount));

            packet.CopyTo(
                _pagePayload.AsSpan(_payloadLength, packet.Length));

            _segmentCount += segmentsWritten;
            _payloadLength += packet.Length;
        }

        /// <summary>
        /// Writes the pending audio page with the supplied flags and granule
        /// position, flushes it to the output, and resets page state.
        /// </summary>
        private void FlushAudioPage(
            byte flags,
            long granulePosition)
        {
            if (_packetCount == 0)
            {
                return;
            }

            WritePage(
                _segmentTable.AsSpan(0, _segmentCount),
                _pagePayload.AsSpan(0, _payloadLength),
                flags,
                granulePosition,
                flushOutput: true);

            _lastFlushedAudioGranule = granulePosition;
            ResetAudioPage();
        }

        /// <summary>
        /// Clears the counters and granule state for the next audio page.
        /// </summary>
        private void ResetAudioPage()
        {
            _segmentCount = 0;
            _payloadLength = 0;
            _packetCount = 0;
            _pageGranulePosition = 0;
        }

        /// <summary>
        /// Builds and writes a complete Ogg page, including its lacing table,
        /// payload, sequence number, granule position, and CRC checksum.
        /// </summary>
        private void WritePage(
            ReadOnlySpan<byte> segmentTable,
            ReadOnlySpan<byte> payload,
            byte flags,
            long granulePosition,
            bool flushOutput)
        {
            if ((flags & ContinuedPacketFlag) != 0)
            {
                throw new NotSupportedException(
                    "Tsubaki's Ogg Opus muxer does not split Opus packets across pages.");
            }

            if (segmentTable.Length > MaxLacingSegments)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentTable));
            }

            Span<byte> header = _pageHeader.AsSpan(0, OggHeaderSize);
            header.Clear();

            "OggS"u8.CopyTo(header);

            header[4] = 0;      // Ogg stream structure version
            header[5] = flags;

            BinaryPrimitives.WriteInt64LittleEndian(
                header.Slice(6, 8),
                granulePosition);

            BinaryPrimitives.WriteUInt32LittleEndian(
                header.Slice(14, 4),
                _streamSerial);

            BinaryPrimitives.WriteUInt32LittleEndian(
                header.Slice(18, 4),
                _pageSequence++);

            // Bytes 22..25 remain zero while calculating CRC.
            header[26] = checked((byte)segmentTable.Length);

            uint crc = 0;
            crc = UpdateCrc(crc, header);
            crc = UpdateCrc(crc, segmentTable);
            crc = UpdateCrc(crc, payload);

            BinaryPrimitives.WriteUInt32LittleEndian(
                header.Slice(22, 4),
                crc);

            _output.Write(_pageHeader, 0, OggHeaderSize);

            if (!segmentTable.IsEmpty)
            {
                _output.Write(
                    _segmentTable,
                    0,
                    segmentTable.Length);
            }

            if (!payload.IsEmpty)
            {
                _output.Write(payload);
            }

            if (flushOutput)
            {
                // In streaming mode this forces the BridgingStream to release the current
                // Ogg page instead of waiting for its generic byte-size threshold.
                // In buffered mode MemoryStream.Flush() is effectively free.
                _output.Flush();
            }
        }

        /// <summary>
        /// Calculates how many Ogg lacing values are required to represent
        /// a packet boundary of the specified length.
        /// </summary>
        private static int GetLacingSegmentCount(int packetLength)
        {
            // A packet whose size is an exact multiple of 255 requires a trailing
            // zero lacing value to mark the packet boundary.
            return (packetLength / 255) + 1;
        }

        /// <summary>
        /// Encodes a packet length into Ogg lacing values in the supplied
        /// destination buffer and returns the number of values written.
        /// </summary>
        private static int BuildLacingTable(
            int packetLength,
            Span<byte> destination)
        {
            int required = GetLacingSegmentCount(packetLength);

            if (destination.Length < required)
            {
                throw new ArgumentException(
                    "Insufficient Ogg lacing-table space.",
                    nameof(destination));
            }

            int remaining = packetLength;
            int index = 0;

            while (remaining >= 255)
            {
                destination[index++] = 255;
                remaining -= 255;
            }

            destination[index++] = checked((byte)remaining);
            return index;
        }

        /// <summary>
        /// Updates an Ogg CRC-32 checksum with the supplied data span.
        /// </summary>
        private static uint UpdateCrc(
            uint crc,
            ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
            {
                int index = (int)(((crc >> 24) ^ value) & 0xFF);
                crc = (crc << 8) ^ CrcTable[index];
            }

            return crc;
        }

        /// <summary>
        /// Builds the lookup table used by the Ogg CRC-32 implementation.
        /// </summary>
        private static uint[] BuildCrcTable()
        {
            const uint polynomial = 0x04C11DB7;
            var table = new uint[256];

            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i << 24;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 0x80000000) != 0
                        ? (value << 1) ^ polynomial
                        : value << 1;
                }

                table[i] = value;
            }

            return table;
        }

        /// <summary>
        /// Returns the muxer's pooled buffers without disposing the underlying
        /// transport stream owned by the response pipeline.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            ArrayPool<byte>.Shared.Return(_segmentTable);
            ArrayPool<byte>.Shared.Return(_pagePayload);

            // Intentionally do NOT dispose _output.
            // ResponsePipeline owns the HTTP/Memory transport.
        }
    }
}
