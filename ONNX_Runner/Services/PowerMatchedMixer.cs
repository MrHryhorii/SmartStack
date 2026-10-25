namespace ONNX_Runner.Services;

/// <summary>Interpolates aligned PCM and matches its mean-square power over the supplied buffer.</summary>
public static class PowerMatchedMixer
{
    /// <summary>Preserves endpoint PCM; contains no effect, output-gain, or peak-protection policy.</summary>
    public static PowerMatcher.Result Mix(ReadOnlySpan<float> dry, Span<float> wet, float position)
    {
        if (dry.Length != wet.Length) throw new ArgumentException("Signal lengths differ.");
        if (!float.IsFinite(position) || position < 0 || position > 1)
            throw new ArgumentOutOfRangeException(nameof(position));
        if (dry.Overlaps(wet, out int offset) && offset != 0)
            throw new ArgumentException("Signals partially overlap.", nameof(wet));
        if (position == 0) { dry.CopyTo(wet); return new(1, true); }
        if (position == 1) return new(1, true);

        double target = PowerMatcher.Target(dry, wet, position);
        SignalInterpolator.Mix(dry, wet, position);
        return PowerMatcher.Match(wet, target);
    }
}
