using System.Buffers.Binary;
using System.Text;
using Tsubaki.ServiceCheck.Core.Audio;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Defines regression checks for WAV structure and PCM16 metrics.</summary>
internal static class WavInspectorChecks
{
    /// <summary>Registers audio checks for the shared regression runner.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks() =>
    [
        Check("PCM16 mono metrics verified", VerifyMetrics),
        Check("PCM16 stereo frames verified", VerifyStereo),
        Check("Silent PCM16 distinguished from invalid WAV", VerifySilence),

        Check("Leading silence accepted", () =>
            ExpectValid(Pcm([0, 0, 0, 0, 1000]))),

        Check("Minimum signed sample handled", VerifyMinimumSample),
        Check("Quiet nonzero signal warned", VerifyQuietSignal),
        Check("Odd ancillary chunk accepted", VerifyAncillaryChunk),
        Check("Data before format accepted", VerifyDataBeforeFormat),
        Check("Trailing payload bytes warned", VerifyTrailingBytes),
        Check("Empty payload hash verified", VerifyEmptyPayload),

        Check("Truncated WAV header rejected", () =>
            ExpectContainer(
                "RIFF"u8.ToArray(),
                WavContainerState.Invalid)),

        Check("Non-WAV payload rejected", () =>
            ExpectContainer(
                Encoding.UTF8.GetBytes("{\"error\":\"fixture\"}"),
                WavContainerState.Invalid)),

        Check("Oversized RIFF declaration rejected", () =>
            ExpectContainer(
                SetUInt32(Pcm([1000]), 4, uint.MaxValue),
                WavContainerState.Invalid)),

        Check("Undersized RIFF declaration rejected", () =>
            ExpectContainer(
                SetUInt32(Pcm([1000]), 4, 0),
                WavContainerState.Invalid)),

        Check("Oversized chunk declaration rejected", () =>
            ExpectContainer(
                SetUInt32(Pcm([1000]), 16, uint.MaxValue),
                WavContainerState.Invalid)),

        Check("Partial chunk header rejected", () =>
            ExpectContainer(
                AppendInside(Pcm([1000]), [1, 2, 3]),
                WavContainerState.Invalid)),

        Check("Missing format chunk rejected", () =>
            ExpectContainer(
                Container(("data", SampleBytes([1000]))),
                WavContainerState.Invalid)),

        Check("Missing data chunk rejected", () =>
            ExpectContainer(
                Container(("fmt ", Format())),
                WavContainerState.Invalid)),

        Check("Short format chunk rejected", () =>
            ExpectContainer(
                SetUInt32(Pcm([1000]), 16, 14),
                WavContainerState.Invalid)),

        Check("Empty audio data rejected", () =>
            ExpectContainer(Pcm([]), WavContainerState.Invalid)),

        Check("Zero channel count rejected", () =>
            ExpectContainer(
                SetUInt16(Pcm([1000]), 22, 0),
                WavContainerState.Invalid)),

        Check("Zero sample rate rejected", () =>
            ExpectContainer(
                SetUInt32(Pcm([1000]), 24, 0),
                WavContainerState.Invalid)),

        Check("Invalid block alignment rejected", () =>
            ExpectContainer(
                SetUInt16(Pcm([1000]), 32, 1),
                WavContainerState.Invalid)),

        Check("Invalid byte rate rejected", () =>
            ExpectContainer(
                SetUInt32(Pcm([1000]), 28, 1),
                WavContainerState.Invalid)),

        Check("Partial PCM16 sample rejected", () =>
            ExpectContainer(
                SetUInt32(Pcm([1000, -1000]), 40, 3),
                WavContainerState.Invalid)),

        Check("Partial stereo frame rejected", () =>
            ExpectContainer(
                Pcm([1000, -1000, 1000], channels: 2),
                WavContainerState.Invalid)),

        Check("Float WAV marked unsupported", () =>
            ExpectContainer(
                Container(
                    ("fmt ", Format(bits: 32, code: 3)),
                    ("data", new byte[] { 0, 0, 0, 0 })),
                WavContainerState.Unsupported)),

        Check("PCM8 WAV marked unsupported", () =>
            ExpectContainer(
                Container(
                    ("fmt ", Format(bits: 8)),
                    ("data", new byte[] { 128, 255 })),
                WavContainerState.Unsupported)),

        Check("RF64 marked unsupported", () =>
            ExpectContainer(
                SetSignature(Pcm([1000]), "RF64"),
                WavContainerState.Unsupported)),

        Check("Big-endian WAV marked unsupported", () =>
            ExpectContainer(
                SetSignature(Pcm([1000]), "RIFX"),
                WavContainerState.Unsupported)),

        Check("Multiple format chunks marked unsupported", () =>
            ExpectContainer(
                Container(
                    ("fmt ", Format()),
                    ("fmt ", Format()),
                    ("data", SampleBytes([1000]))),
                WavContainerState.Unsupported)),

        Check("Multiple data chunks marked unsupported", () =>
            ExpectContainer(
                Container(
                    ("fmt ", Format()),
                    ("data", SampleBytes([1000])),
                    ("data", SampleBytes([-1000]))),
                WavContainerState.Unsupported)),

        Check("Missing odd-chunk padding rejected", VerifyMissingPadding),
        Check("Audio inspection cancellation honored", VerifyCancellation)
    ];

    /// <summary>Checks known levels and duration against fixed expected values.</summary>
    private static void VerifyMetrics()
    {
        var result = ExpectValid(
            Pcm([0, 16_384, -16_384, 0], sampleRate: 8000));

        Require(result.FormatCode == 1
            && result.Channels == 1
            && result.SampleRateHz == 8000
            && result.BitsPerSample == 16
            && result.BlockAlign == 2
            && result.ByteRate == 16_000,
            "PCM format fields are incorrect.");

        Require(result.DataBytes == 8 && result.FrameCount == 4,
            "Audio lengths are incorrect.");

        RequireClose(result.DurationSeconds, 0.0005, "Duration");
        RequireClose(result.PeakAbsolute, 0.5, "Peak");
        RequireClose(result.Rms, 0.3535533905932738, "RMS");
        RequireClose(result.PeakDbfs, -6.020599913279624, "Peak dBFS");
        RequireClose(result.RmsDbfs, -9.030899869919436, "RMS dBFS");
        RequireClose(result.NearFullScaleFraction, 0, "Near-full-scale fraction");
    }

    /// <summary>Verifies that frame counts account for all channels.</summary>
    private static void VerifyStereo()
    {
        var result = ExpectValid(
            Pcm([0, 1000, -1000, 0], channels: 2));

        Require(result.Channels == 2
            && result.BlockAlign == 4
            && result.FrameCount == 2
            && result.ByteRate == 88_200,
            "Stereo frame parameters are incorrect.");
    }

    /// <summary>Distinguishes structural validity from an all-zero signal.</summary>
    private static void VerifySilence()
    {
        var result = WavInspector.Inspect(Pcm([0, 0, 0, 0]));

        Require(result.ContainerState == WavContainerState.Valid
            && result.SignalState == AudioSignalState.Silent
            && result.Code == "audio_silent",
            "Silence was not classified correctly.");

        Require(result.PeakAbsolute == 0
            && result.Rms == 0
            && result.PeakDbfs is null
            && result.RmsDbfs is null,
            "Silence produced invalid level measurements.");
    }

    /// <summary>Verifies the full negative PCM16 range without overflow.</summary>
    private static void VerifyMinimumSample()
    {
        var result = ExpectValid(Pcm([short.MinValue]));

        RequireClose(result.PeakAbsolute, 1, "Peak");
        RequireClose(result.Rms, 1, "RMS");
        RequireClose(result.PeakDbfs, 0, "Peak dBFS");
        RequireClose(result.NearFullScaleFraction, 1, "Near-full-scale fraction");

        Require(result.Warnings.Any(warning =>
            warning.Code == "audio_near_full_scale"),
            "The near-full-scale observation is missing.");
    }

    /// <summary>Requires quiet audio to remain a valid nonzero signal.</summary>
    private static void VerifyQuietSignal()
    {
        var result = ExpectValid(Pcm([1, -1]));

        Require(result.Warnings.Any(warning =>
            warning.Code == "audio_very_quiet"),
            "The quiet-signal observation is missing.");
    }

    /// <summary>Verifies unknown chunks and odd-sized chunk padding.</summary>
    private static void VerifyAncillaryChunk()
    {
        var payload = Container(
            ("JUNK", new byte[] { 1, 2, 3 }),
            ("fmt ", Format()),
            ("data", SampleBytes([1000, -1000])));

        var result = ExpectValid(payload);

        Require(result.DataBytes == 4 && result.FrameCount == 2,
            "Ancillary data affected audio measurements.");
    }

    /// <summary>Verifies chunk discovery independently of chunk order.</summary>
    private static void VerifyDataBeforeFormat()
    {
        var result = ExpectValid(Container(
            ("data", SampleBytes([1000, -1000])),
            ("fmt ", Format())));

        Require(result.SampleRateHz == 22_050 && result.FrameCount == 2,
            "The format was not found after the data chunk.");
    }

    /// <summary>Verifies trailing bytes are identified and included in the hash.</summary>
    private static void VerifyTrailingBytes()
    {
        var original = Pcm([1000]);
        var extended = original.Concat(new byte[] { 9, 8, 7 }).ToArray();

        var originalResult = ExpectValid(original);
        var result = ExpectValid(extended);

        Require(result.Warnings.Any(warning =>
            warning.Code == "audio_trailing_bytes"),
            "Trailing bytes were not reported.");

        Require(result.DataBytes == originalResult.DataBytes
            && result.PayloadBytes == extended.Length
            && result.PayloadSha256 != originalResult.PayloadSha256,
            "Artifact identity or audio length is incorrect.");
    }

    /// <summary>Verifies hashing of a known empty payload.</summary>
    private static void VerifyEmptyPayload()
    {
        var result = WavInspector.Inspect([]);

        Require(result.ContainerState == WavContainerState.Invalid,
            "An empty payload was accepted.");

        Require(result.PayloadBytes == 0
            && result.PayloadSha256
                == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "The payload hash is incorrect.");
    }

    /// <summary>Rejects absent padding even after valid audio chunks.</summary>
    private static void VerifyMissingPadding()
    {
        var payload = Container(
            ("fmt ", Format()),
            ("data", SampleBytes([1000])),
            ("JUNK", new byte[] { 1 }));

        var truncated = payload[..^1];

        SetUInt32(truncated, 4, checked((uint)(truncated.Length - 8)));
        ExpectContainer(truncated, WavContainerState.Invalid);
    }

    /// <summary>Verifies that a cancelled inspection does not continue.</summary>
    private static void VerifyCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            WavInspector.Inspect(Pcm([1000]), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Cancelled audio inspection completed.");
    }

    /// <summary>Requires a valid, nonzero PCM16 inspection result.</summary>
    private static WavInspectionResult ExpectValid(byte[] payload)
    {
        var result = WavInspector.Inspect(payload);

        Require(result.ContainerState == WavContainerState.Valid
            && result.SignalState == AudioSignalState.Nonzero,
            $"Expected valid nonzero audio: {result.Summary}");

        return result;
    }

    /// <summary>Requires an invalid or unsupported container without signal analysis.</summary>
    private static void ExpectContainer(
        byte[] payload,
        WavContainerState expected)
    {
        var result = WavInspector.Inspect(payload);

        Require(result.ContainerState == expected
            && result.SignalState == AudioSignalState.NotChecked,
            $"Expected {expected}: {result.Summary}");
    }

    /// <summary>Compares a finite measurement with a fixed expected value.</summary>
    private static void RequireClose(
        double? actual,
        double expected,
        string name)
    {
        if (actual is not double value
            || !double.IsFinite(value)
            || Math.Abs(value - expected) > 1e-10)
        {
            throw new InvalidOperationException(
                $"{name} differs from the expected measurement.");
        }
    }

    /// <summary>Fails a regression check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    /// <summary>Adapts a synchronous assertion to the shared runner.</summary>
    private static (string Name, Func<Task> Run) Check(
        string name,
        Action action)
    {
        return (name, () =>
        {
            action();
            return Task.CompletedTask;
        }
        );
    }

    /// <summary>Creates a conventional PCM16 fixture.</summary>
    private static byte[] Pcm(
        short[] samples,
        ushort channels = 1,
        uint sampleRate = 22_050) =>
        Container(
            ("fmt ", Format(channels, sampleRate)),
            ("data", SampleBytes(samples)));

    /// <summary>Creates common waveform format fields in little-endian order.</summary>
    private static byte[] Format(
        ushort channels = 1,
        uint sampleRate = 22_050,
        ushort bits = 16,
        ushort code = 1)
    {
        var format = new byte[16];
        var alignment = checked((ushort)(channels * (bits / 8)));

        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(0, 2), code);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4, 4), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            format.AsSpan(8, 4),
            checked(sampleRate * alignment));
        BinaryPrimitives.WriteUInt16LittleEndian(
            format.AsSpan(12, 2), alignment);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14, 2), bits);

        return format;
    }

    /// <summary>Encodes signed PCM16 fixture samples.</summary>
    private static byte[] SampleBytes(short[] samples)
    {
        var bytes = new byte[checked(samples.Length * 2)];

        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(index * 2, 2),
                samples[index]);
        }

        return bytes;
    }

    /// <summary>Builds a RIFF/WAVE fixture with controlled chunks and padding.</summary>
    private static byte[] Container(
        params (string Id, byte[] Data)[] chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0u);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        foreach (var (id, data) in chunks)
        {
            writer.Write(Encoding.ASCII.GetBytes(id));
            writer.Write(checked((uint)data.Length));
            writer.Write(data);

            if (data.Length % 2 != 0)
                writer.Write((byte)0);
        }

        writer.Flush();

        var payload = stream.ToArray();

        return SetUInt32(
            payload,
            4,
            checked((uint)(payload.Length - 8)));
    }

    /// <summary>Changes a known 16-bit field in a controlled fixture.</summary>
    private static byte[] SetUInt16(byte[] payload, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(offset, 2), value);

        return payload;
    }

    /// <summary>Changes a known 32-bit field in a controlled fixture.</summary>
    private static byte[] SetUInt32(byte[] payload, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(offset, 4), value);

        return payload;
    }

    /// <summary>Changes the container signature in a controlled fixture.</summary>
    private static byte[] SetSignature(byte[] payload, string signature)
    {
        Encoding.ASCII.GetBytes(signature).CopyTo(payload, 0);

        return payload;
    }

    /// <summary>Adds bytes inside the declared RIFF boundary.</summary>
    private static byte[] AppendInside(byte[] payload, byte[] extra)
    {
        var combined = payload.Concat(extra).ToArray();

        return SetUInt32(
            combined,
            4,
            checked((uint)(combined.Length - 8)));
    }
}