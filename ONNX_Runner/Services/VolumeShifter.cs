namespace ONNX_Runner.Services;

/// <summary>
/// Professional Soft-Knee Limiter.
/// Preserves perfect linearity up to 80%, then applies a smooth algebraic curve to peaks.
/// </summary>
public static class VolumeShifter
{
    /// <summary>Applies gain and soft-limits peaks; forceLimiter also protects boosted DSP at unity gain.</summary>
    public static void ApplyVolume(Span<float> buffer, float volume, bool forceLimiter = false)
    {
        if (!forceLimiter && MathF.Abs(volume - 1.0f) < 0.001f) return;
        // The threshold is where the soft-knee curve starts. Below this, the signal is unchanged.
        const float threshold = 0.8f;
        const float headroom = 1.0f - threshold;
        // For volume > 1.0, the effective threshold is reduced, making the curve more aggressive.
        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i] * volume;
            float absX = MathF.Abs(x);

            if (absX <= threshold)
            {
                buffer[i] = x;
            }
            else
            {
                // Soft-knee curve: y = threshold + headroom * (excess / (1 + excess))
                float excess = (absX - threshold) / headroom;
                float softPeak = threshold + headroom * (excess / (1.0f + excess));
                buffer[i] = MathF.Sign(x) * softPeak;
            }
        }
    }
}