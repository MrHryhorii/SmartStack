namespace ONNX_Runner.Models;

/// <summary>Request limits and encoder input rates for optional output resampling.</summary>
internal static class AudioSampleRate
{
    internal const int Minimum = 8000;
    internal const int Maximum = 48000;

    private static readonly int[] OpusRates = [8000, 12000, 16000, 24000, 48000];
    private static readonly int[] Mp3Rates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000];
    private static readonly int[] AacRates =
        [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000];

    internal static int? Clamp(int? value)
        => value is null or 0 ? value : Math.Clamp(value.Value, Minimum, Maximum);

    internal static int Resolve(int sampleRate, AudioFormat format)
    {
        ReadOnlySpan<int> rates = format switch
        {
            AudioFormat.Opus => OpusRates,
            AudioFormat.Mp3 or AudioFormat.B64Json => Mp3Rates,
            AudioFormat.Aac => AacRates,
            _ => []
        };
        if (rates.IsEmpty)
        {
            return sampleRate;
        }

        int nearest = rates[0];
        int nearestDistance = Math.Abs(nearest - sampleRate);
        foreach (int candidate in rates[1..])
        {
            int distance = Math.Abs(candidate - sampleRate);
            // Ascending rates make ties deterministic: choose the lower rate.
            if (distance >= nearestDistance)
            {
                continue;
            }
            nearest = candidate;
            nearestDistance = distance;
        }
        return nearest;
    }
}
