using System.Collections.Frozen;

namespace ONNX_Runner.Models;

/// <summary>Unity-gain, second-order high-pass and low-pass settings for one device class.</summary>
public sealed record AudioEqualizerProfile(string Id, float HighPassHz, float LowPassHz, float QFactor);

/// <summary>
/// Device-class approximations using Google's public profile IDs, with locally defined DSP settings.
/// Cutoffs describe a dry playback bandwidth; no presence boost, noise, or saturation is added.
/// </summary>
public static class AudioEqualizerProfiles
{
    /// <summary>Maximum raw profile-list entries accepted per request, including unknown IDs and nulls.</summary>
    public const int MaxProfilesPerRequest = 16;

    // Butterworth sections have a monotonic frequency response with no resonant gain peak.
    private const float ButterworthQ = 0.70710678f;

    public static IReadOnlyList<AudioEqualizerProfile> All { get; } = Array.AsReadOnly<AudioEqualizerProfile>(
    [
        new("wearable-class-device", 350f, 5000f, ButterworthQ),
        new("handset-class-device", 180f, 8000f, ButterworthQ),
        new("headphone-class-device", 40f, 18000f, ButterworthQ),
        new("small-bluetooth-speaker-class-device", 120f, 10000f, ButterworthQ),
        new("medium-bluetooth-speaker-class-device", 70f, 14000f, ButterworthQ),
        new("large-home-entertainment-class-device", 30f, 20000f, ButterworthQ),
        new("large-automotive-class-device", 90f, 12000f, ButterworthQ),
        new("telephony-class-application", 300f, 3400f, ButterworthQ)
    ]);

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
