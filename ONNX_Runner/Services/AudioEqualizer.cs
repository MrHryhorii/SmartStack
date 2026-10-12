using NAudio.Dsp;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Applies an ordered cascade of dry device profiles in place. Filter state belongs to one
/// request and continues across its audio chunks and sentence pauses. Never share an instance.
/// </summary>
public sealed class AudioEqualizer
{
    private readonly BiQuadFilter[] _filters;

    /// <summary>Owns the filter cascade prepared for this request's processing sample rate.</summary>
    private AudioEqualizer(BiQuadFilter[] filters) => _filters = filters;

    /// <summary>
    /// Creates filters once per request for the first MaxProfilesPerRequest list entries,
    /// preserving order and repetitions. Unknown IDs and nulls still occupy list positions.
    /// Returns null for missing, empty, or entirely unsupported lists without constructing an engine.
    /// Low-pass cutoffs at or above Nyquist are omitted because they exceed the available band.
    /// </summary>
    public static AudioEqualizer? Create(IReadOnlyList<string?>? profileIds, int sampleRate)
    {
        if (profileIds == null || profileIds.Count == 0) return null;

        int profileCount = Math.Min(profileIds.Count, AudioEqualizerProfiles.MaxProfilesPerRequest);
        int count = 0;
        float nyquist = sampleRate * 0.5f;
        for (int i = 0; i < profileCount; i++)
        {
            var profile = AudioEqualizerProfiles.Find(profileIds[i]);
            if (profile == null) continue;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
            count += profile.LowPassHz < nyquist ? 2 : 1;
        }
        if (count == 0) return null;

        var filters = new BiQuadFilter[count];
        int index = 0;
        for (int i = 0; i < profileCount; i++)
        {
            var profile = AudioEqualizerProfiles.Find(profileIds[i]);
            if (profile == null) continue;

            // Keep the high-pass section numerically safe even for an unusually low model rate.
            float highPass = Math.Min(profile.HighPassHz, nyquist * 0.95f);
            filters[index++] = BiQuadFilter.HighPassFilter(sampleRate, highPass, profile.QFactor);
            if (profile.LowPassHz < nyquist)
                filters[index++] = BiQuadFilter.LowPassFilter(sampleRate, profile.LowPassHz, profile.QFactor);
        }
        return new AudioEqualizer(filters);
    }

    /// <summary>Filters the valid PCM span without allocating, changing its length, or resetting state.</summary>
    public void Apply(Span<float> samples)
    {
        foreach (var filter in _filters)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = filter.Transform(samples[i]);
        }
    }
}
