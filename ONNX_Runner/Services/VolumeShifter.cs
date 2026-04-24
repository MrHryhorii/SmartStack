namespace ONNX_Runner.Services;

/// <summary>
/// A highly optimized static DSP helper for in-place volume modification.
/// Includes a soft-clipper (Tanh limiter) to prevent digital distortion when boosting volume.
/// </summary>
public static class VolumeShifter
{
    /// <summary>
    /// Multiplies the audio samples by the volume factor.
    /// Uses Math.Tanh to smoothly limit audio peaks and prevent hard clipping.
    /// </summary>
    /// <param name="buffer">The audio buffer to modify in-place.</param>
    /// <param name="volume">1.0 = original, < 1.0 = quieter, > 1.0 = louder (up to 10.0).</param>
    public static void ApplyVolume(Span<float> buffer, float volume)
    {
        // Bypass if volume is unchanged to save CPU cycles
        if (Math.Abs(volume - 1.0f) < 0.001f)
            return;

        for (int i = 0; i < buffer.Length; i++)
        {
            // Apply gain and smoothly round off any peaks that exceed [-1.0, 1.0]
            buffer[i] = (float)Math.Tanh(buffer[i] * volume);
        }
    }
}