using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Tsubaki.ServiceCheck.Core.Audio;

/// <summary>Inspects bounded RIFF/WAVE payloads and PCM16 sample data.</summary>
public static class WavInspector
{
    private const double NearFullScaleLevel = 0.999;
    private const double NearFullScaleWarningFraction = 0.01;
    private const double QuietRmsDbfs = -50.0;

    /// <summary>Validates a WAV payload and calculates supported signal metrics.</summary>
    public static WavInspectionResult Inspect(
        ReadOnlySpan<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clock = Stopwatch.StartNew();
        var warnings = new List<AudioInspectionWarning>();

        var initial = new WavInspectionResult
        {
            ContainerState = WavContainerState.Invalid,
            SignalState = AudioSignalState.NotChecked,
            Code = "audio_invalid_container",
            Summary = "The response is not a supported WAV container.",
            PayloadBytes = payload.Length,
            PayloadSha256 = Convert.ToHexString(
                SHA256.HashData(payload)).ToLowerInvariant(),
            InspectionDurationMs = 0
        };

        cancellationToken.ThrowIfCancellationRequested();

        var result = ReadContainer(
            payload,
            initial,
            warnings,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        clock.Stop();

        return result with
        {
            InspectionDurationMs = clock.ElapsedMilliseconds,
            Warnings = warnings.ToArray()
        };
    }

    /// <summary>Walks complete RIFF chunks within the declared container boundary.</summary>
    private static WavInspectionResult ReadContainer(
        ReadOnlySpan<byte> payload,
        WavInspectionResult result,
        List<AudioInspectionWarning> warnings,
        CancellationToken cancellationToken)
    {
        if (payload.Length < 12)
            return Invalid(result, "The WAV header is missing or truncated.");

        if (!payload.Slice(8, 4).SequenceEqual("WAVE"u8))
            return Invalid(result, "The response does not contain a WAVE identifier.");

        if (payload[..4].SequenceEqual("RF64"u8)
            || payload[..4].SequenceEqual("RIFX"u8))
        {
            return Unsupported(
                result,
                "RF64 and big-endian WAV containers are not supported.");
        }

        if (!payload[..4].SequenceEqual("RIFF"u8))
            return Invalid(result, "The response does not contain a RIFF identifier.");

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.Slice(4, 4));

        var declaredEnd = checked((long)declaredSize + 8);

        if (declaredSize < 4 || declaredEnd > payload.Length)
            return Invalid(result, "The declared RIFF size exceeds the available payload.");

        var boundary = checked((int)declaredEnd);

        if (boundary < payload.Length)
        {
            warnings.Add(new AudioInspectionWarning(
                "audio_trailing_bytes",
                "Bytes after the declared RIFF boundary were ignored."));
        }

        var position = 12;
        var formatFound = false;
        var dataOffset = -1;
        var dataLength = 0;
        var duplicateChunks = false;

        while (position < boundary)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (boundary - position < 8)
                return Invalid(result, "A RIFF chunk header is truncated.");

            var identifier = payload.Slice(position, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.Slice(position + 4, 4));

            var start = checked(position + 8);
            var end = checked((long)start + length);
            var paddedEnd = checked(end + (length & 1u));

            if (paddedEnd > boundary)
                return Invalid(result, "A RIFF chunk or its padding exceeds the container.");

            if (identifier.SequenceEqual("fmt "u8))
            {
                if (length < 16)
                    return Invalid(result, "The format chunk is incomplete.");

                if (formatFound)
                {
                    duplicateChunks = true;
                }
                else
                {
                    result = ReadFormat(payload.Slice(start, 16), result);
                    formatFound = true;
                }
            }
            else if (identifier.SequenceEqual("data"u8))
            {
                if (dataOffset >= 0)
                {
                    duplicateChunks = true;
                }
                else
                {
                    dataOffset = start;
                    dataLength = checked((int)length);
                    result = result with { DataBytes = dataLength };
                }
            }

            position = checked((int)paddedEnd);
        }

        if (!formatFound)
            return Invalid(result, "The format chunk is missing.");

        if (dataOffset < 0)
            return Invalid(result, "The audio data chunk is missing.");

        if (duplicateChunks)
        {
            return Unsupported(
                result,
                "Multiple format or audio data chunks are not supported.");
        }

        return ValidatePcm16(
            payload.Slice(dataOffset, dataLength),
            result,
            warnings,
            cancellationToken);
    }

    /// <summary>Reads the common waveform format fields.</summary>
    private static WavInspectionResult ReadFormat(
        ReadOnlySpan<byte> format,
        WavInspectionResult result) => result with
        {
            FormatCode = BinaryPrimitives.ReadUInt16LittleEndian(format[..2]),
            Channels = BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(2, 2)),
            SampleRateHz = BinaryPrimitives.ReadUInt32LittleEndian(format.Slice(4, 4)),
            ByteRate = BinaryPrimitives.ReadUInt32LittleEndian(format.Slice(8, 4)),
            BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(12, 2)),
            BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(14, 2))
        };

    /// <summary>Validates supported PCM parameters before reading samples.</summary>
    private static WavInspectionResult ValidatePcm16(
        ReadOnlySpan<byte> data,
        WavInspectionResult result,
        List<AudioInspectionWarning> warnings,
        CancellationToken cancellationToken)
    {
        var channels = result.Channels.GetValueOrDefault();
        var sampleRate = result.SampleRateHz.GetValueOrDefault();
        var blockAlign = result.BlockAlign.GetValueOrDefault();
        var byteRate = result.ByteRate.GetValueOrDefault();

        if (channels == 0 || sampleRate == 0)
            return Invalid(result, "Channel count and sample rate must be positive.");

        if (result.FormatCode != 1 || result.BitsPerSample != 16)
        {
            return Unsupported(
                result,
                "Only PCM format code 1 with 16-bit samples is supported.");
        }

        var expectedBlockAlign = checked((long)channels * 2);

        if (blockAlign != expectedBlockAlign)
            return Invalid(result, "PCM16 block alignment is inconsistent.");

        var expectedByteRate = checked((ulong)sampleRate * blockAlign);

        if (byteRate != expectedByteRate)
            return Invalid(result, "PCM16 byte rate is inconsistent.");

        if (data.IsEmpty)
            return Invalid(result, "The audio data chunk is empty.");

        if (data.Length % blockAlign != 0)
            return Invalid(result, "Audio data is not aligned to a complete sample frame.");

        var frames = (long)data.Length / blockAlign;

        result = result with
        {
            ContainerState = WavContainerState.Valid,
            FrameCount = frames,
            DurationSeconds = frames / (double)sampleRate
        };

        return AnalyzeSignal(data, result, warnings, cancellationToken);
    }

    /// <summary>Calculates normalized PCM16 levels across all channels.</summary>
    private static WavInspectionResult AnalyzeSignal(
        ReadOnlySpan<byte> data,
        WavInspectionResult result,
        List<AudioInspectionWarning> warnings,
        CancellationToken cancellationToken)
    {
        var sampleCount = data.Length / 2;
        var peak = 0.0;
        var sumSquares = 0.0;
        long nearFullScaleSamples = 0;

        for (var offset = 0; offset < data.Length; offset += 2)
        {
            if (offset % 32_768 == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var sample = BinaryPrimitives.ReadInt16LittleEndian(
                data.Slice(offset, 2));

            var normalized = sample / 32768.0;
            var absolute = Math.Abs(normalized);

            peak = Math.Max(peak, absolute);
            sumSquares += normalized * normalized;

            if (absolute >= NearFullScaleLevel)
                nearFullScaleSamples++;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        var rmsDbfs = ToDbfs(rms);
        var nearFullScaleFraction =
            nearFullScaleSamples / (double)sampleCount;

        var silent = peak == 0;

        if (!silent && rmsDbfs is double level && level < QuietRmsDbfs)
        {
            warnings.Add(new AudioInspectionWarning(
                "audio_very_quiet",
                "The PCM signal has an RMS level below -50 dBFS."));
        }

        if (nearFullScaleFraction >= NearFullScaleWarningFraction)
        {
            warnings.Add(new AudioInspectionWarning(
                "audio_near_full_scale",
                "At least 1% of samples are near full scale."));
        }

        return result with
        {
            SignalState = silent
                ? AudioSignalState.Silent
                : AudioSignalState.Nonzero,
            Code = silent ? "audio_silent" : "audio_verified",
            Summary = silent
                ? "The PCM16 response contains only zero-valued samples."
                : "The response contains valid, nonzero PCM16 audio.",
            PeakAbsolute = peak,
            Rms = rms,
            PeakDbfs = ToDbfs(peak),
            RmsDbfs = rmsDbfs,
            NearFullScaleFraction = nearFullScaleFraction
        };
    }

    /// <summary>Returns null for an undefined logarithmic silence level.</summary>
    private static double? ToDbfs(double level) =>
        level > 0 ? 20 * Math.Log10(level) : null;

    /// <summary>Records malformed or incomplete container evidence.</summary>
    private static WavInspectionResult Invalid(
        WavInspectionResult result,
        string summary) => result with
        {
            ContainerState = WavContainerState.Invalid,
            SignalState = AudioSignalState.NotChecked,
            Code = "audio_invalid_container",
            Summary = summary
        };

    /// <summary>Records a format outside the supported inspection coverage.</summary>
    private static WavInspectionResult Unsupported(
        WavInspectionResult result,
        string summary) => result with
        {
            ContainerState = WavContainerState.Unsupported,
            SignalState = AudioSignalState.NotChecked,
            Code = "audio_unsupported_format",
            Summary = summary
        };
}