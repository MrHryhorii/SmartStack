namespace ONNX_Runner.Services;

/// <summary>Finite-buffer mean-square matching, independent of interpolation and peak protection.</summary>
public static class PowerMatcher
{
    public readonly record struct Result(double Gain, bool Matched);

    public static double Measure(ReadOnlySpan<float> samples)
    {
        double energy = 0;
        foreach (float sample in samples) energy += sample * (double)sample;
        return samples.IsEmpty ? 0 : energy / samples.Length;
    }

    public static double Target(ReadOnlySpan<float> dry, ReadOnlySpan<float> wet, float position)
    {
        if (dry.Length != wet.Length) throw new ArgumentException("Signal lengths differ.");
        if (!float.IsFinite(position) || position < 0 || position > 1)
            throw new ArgumentOutOfRangeException(nameof(position));

        double dryEnergy = 0, wetEnergy = 0;
        for (int i = 0; i < dry.Length; i++)
        {
            dryEnergy += dry[i] * (double)dry[i];
            wetEnergy += wet[i] * (double)wet[i];
        }
        return dry.IsEmpty ? 0 : ((1 - (double)position) * dryEnergy + position * wetEnergy) / dry.Length;
    }

    /// <summary>Interpolates measured powers without requiring a dry/wet signal mix.</summary>
    public static double Target(double dryPower, double wetPower, float position)
    {
        if (!double.IsFinite(dryPower) || dryPower < 0)
            throw new ArgumentOutOfRangeException(nameof(dryPower));
        if (!double.IsFinite(wetPower) || wetPower < 0)
            throw new ArgumentOutOfRangeException(nameof(wetPower));
        if (!float.IsFinite(position) || position < 0 || position > 1)
            throw new ArgumentOutOfRangeException(nameof(position));

        return (1 - (double)position) * dryPower + position * wetPower;
    }

    /// <summary>Applies one scalar; reports an unattainable nonzero target when the input has zero power.</summary>
    public static Result Match(Span<float> samples, double targetPower)
    {
        if (!double.IsFinite(targetPower) || targetPower < 0)
            throw new ArgumentOutOfRangeException(nameof(targetPower));
        double power = Measure(samples);
        if (!double.IsFinite(power)) throw new ArgumentException("Signal power is not finite.", nameof(samples));
        if (power == targetPower) return new Result(1, true);
        if (power == 0) return new Result(1, targetPower == 0);

        double gain = Math.Sqrt(targetPower / power);
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(samples[i] * gain);
        return new Result(gain, true);
    }
}
