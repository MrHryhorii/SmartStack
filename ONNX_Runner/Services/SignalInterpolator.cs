namespace ONNX_Runner.Services;

/// <summary>Linear interpolation of aligned PCM signals; contains no level or peak policy.</summary>
public static class SignalInterpolator
{
    public static void Mix(ReadOnlySpan<float> dry, Span<float> wet, float position)
    {
        if (dry.Length != wet.Length) throw new ArgumentException("Signal lengths differ.");
        if (!float.IsFinite(position) || position < 0 || position > 1)
            throw new ArgumentOutOfRangeException(nameof(position));
        if (position == 1) return;
        if (position == 0) { dry.CopyTo(wet); return; }

        double a = 1 - (double)position, b = position;
        for (int i = 0; i < wet.Length; i++) wet[i] = (float)(a * dry[i] + b * wet[i]);
    }
}
