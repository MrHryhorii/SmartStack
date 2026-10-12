using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace ONNX_Runner.Models;

/// <summary>A broad parametric band; Q controls width and gain describes the center in decibels.</summary>
public sealed record AudioEqualizerBand(float FrequencyHz, float QFactor, float GainDb);

/// <summary>Playback bandwidth and immutable tonal bands for one device class.</summary>
public sealed record AudioEqualizerProfile(string Id, float HighPassHz, float LowPassHz, float QFactor,
    IReadOnlyList<AudioEqualizerBand> Bands);

/// <summary>
/// Device-class approximations using Google's public profile IDs, with locally defined DSP settings.
/// Broad tonal bands distinguish device classes without adding noise, saturation, or spatial effects.
/// </summary>
public static class AudioEqualizerProfiles
{
    /// <summary>Maximum raw profile-list entries accepted per request, including unknown IDs and nulls.</summary>
    public const int MaxProfilesPerRequest = 16;

    // Butterworth sections have a monotonic frequency response with no resonant gain peak.
    private const float ButterworthQ = 0.70710678f;

    public static IReadOnlyList<AudioEqualizerProfile> All { get; } = Array.AsReadOnly<AudioEqualizerProfile>(
    [
        new("wearable-class-device", 350f, 5000f, ButterworthQ, Bands(new AudioEqualizerBand(2500f, .8f, 3f))),
        new("handset-class-device", 180f, 8000f, ButterworthQ, Bands(new AudioEqualizerBand(400f, .8f, -2f), new(1500f, .8f, 3f))),
        new("headphone-class-device", 40f, 18000f, ButterworthQ, Bands(new AudioEqualizerBand(120f, .6f, 1.5f), new(4500f, .6f, 1.5f))),
        new("small-bluetooth-speaker-class-device", 120f, 10000f, ButterworthQ, Bands(new AudioEqualizerBand(250f, .8f, 3f), new(800f, .9f, -2f))),
        new("medium-bluetooth-speaker-class-device", 70f, 14000f, ButterworthQ, Bands(new AudioEqualizerBand(180f, .7f, 2f), new(4000f, .7f, 1f))),
        new("large-home-entertainment-class-device", 30f, 20000f, ButterworthQ, Bands(new AudioEqualizerBand(100f, .6f, 2f), new(900f, .7f, -1.5f), new(6000f, .6f, 2f))),
        new("large-automotive-class-device", 90f, 12000f, ButterworthQ, Bands(new AudioEqualizerBand(180f, .7f, 3f), new(3000f, .8f, -2f))),
        new("telephony-class-application", 300f, 3400f, ButterworthQ, Bands(new AudioEqualizerBand(1000f, .8f, 3f)))
    ]);

    /// <summary>Protects preset bands from mutation through the public catalog.</summary>
    private static ReadOnlyCollection<AudioEqualizerBand> Bands(params AudioEqualizerBand[] bands) => Array.AsReadOnly(bands);

    public static IReadOnlyList<string> Ids { get; } = Array.AsReadOnly(All.Select(profile => profile.Id).ToArray());

    private static readonly FrozenDictionary<string, AudioEqualizerProfile> ById =
        All.ToFrozenDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a supported ID, allowing surrounding whitespace and ignoring unknown names.</summary>
    public static AudioEqualizerProfile? Find(string? id)
    {
        if (id == null) return null;
        return ById.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(id.AsSpan().Trim(), out var profile)
            ? profile
            : null;
    }
}
