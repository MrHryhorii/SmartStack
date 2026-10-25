namespace ONNX_Runner.Services;

/// <summary>Uniform PCM gain; does not compress, clip, normalize, or inspect effects.</summary>
public static class LinearGain
{
    public static void Apply(Span<float> samples, float gain)
    {
        if (!float.IsFinite(gain)) throw new ArgumentOutOfRangeException(nameof(gain));
        if (gain == 1) return;
        for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
    }
}
