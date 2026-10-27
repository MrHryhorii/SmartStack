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
        return TargetFromEnergy(dryEnergy, wetEnergy, position, dry.Length);
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

    /// <summary>Derives a mean-square target from existing energy sums without scanning PCM.</summary>
    public static double TargetFromEnergy(double dryEnergy, double wetEnergy, float position, int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        double targetEnergy = Target(dryEnergy, wetEnergy, position);
        if (sampleCount == 0)
        {
            if (dryEnergy != 0 || wetEnergy != 0) throw new ArgumentException("Empty signals cannot have energy.");
            return 0;
        }
        return targetEnergy / sampleCount;
    }

    /// <summary>Applies one scalar; reports an unattainable nonzero target when the input has zero power.</summary>
    public static Result Match(Span<float> samples, double targetPower)
    {
        if (!double.IsFinite(targetPower) || targetPower < 0)
            throw new ArgumentOutOfRangeException(nameof(targetPower));

        return Match(samples, targetPower, Measure(samples));
    }

    /// <summary>Uses the measured mean-square power of this unchanged buffer; no additional analysis pass.</summary>
    public static Result Match(Span<float> samples, double targetPower, double measuredPower)
    {
        Result result = CalculateGain(targetPower, measuredPower);
        if (!result.Matched || measuredPower == targetPower) return result;
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(samples[i] * result.Gain);
        return result;
    }

    /// <summary>Matches buffer power with a bounded linear gain transition from the preceding buffer.</summary>
    public static Result MatchRamped(Span<float> samples, double targetPower, double measuredPower,
        double startGain, int transitionSamples, double maximumGain)
    {
        if (!double.IsFinite(maximumGain) || maximumGain < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumGain));
        if (!double.IsFinite(startGain) || startGain < 0 || startGain > maximumGain)
            throw new ArgumentOutOfRangeException(nameof(startGain));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transitionSamples);
        Result constant = CalculateGain(targetPower, measuredPower);
        if (samples.IsEmpty || measuredPower == 0) return new Result(startGain, constant.Matched);

        int head = Math.Min(transitionSamples, samples.Length);
        double prefixEnergy = 0, endEnergy = 0, crossEnergy = 0, startEnergy = 0;
        double step = head > 1 ? 1d / (head - 1) : 1;
        for (int i = 0; i < head; i++)
        {
            double endWeight = head > 1 ? i * step : 1;
            double startWeight = 1 - endWeight;
            double energy = samples[i] * (double)samples[i];
            prefixEnergy += energy;
            endEnergy += energy * endWeight * endWeight;
            crossEnergy += energy * endWeight * startWeight;
            startEnergy += energy * startWeight * startWeight;
        }

        // The gain envelope's output power is a*g^2 + 2*b*g + c.
        double a = Math.Max(0, measuredPower - prefixEnergy / samples.Length) + endEnergy / samples.Length;
        double b = crossEnergy / samples.Length * startGain;
        double c = startEnergy / samples.Length * startGain * startGain;
        double remaining = targetPower - c;
        double gain = 0;
        bool attainable = remaining >= 0 && a > 0;
        if (attainable)
        {
            // This positive-root form avoids subtraction of nearly equal values.
            double denominator = b + Math.Sqrt(b * b + a * remaining);
            gain = denominator > 0 ? remaining / denominator : 0;
        }
        if (a == 0) { gain = constant.Gain; attainable = c == targetPower; }
        bool matched = attainable && gain <= maximumGain;
        gain = Math.Min(gain, maximumGain);

        for (int i = 0; i < head; i++)
        {
            double weight = head > 1 ? i * step : 1;
            samples[i] = (float)(samples[i] * (startGain + (gain - startGain) * weight));
        }
        for (int i = head; i < samples.Length; i++) samples[i] = (float)(samples[i] * gain);
        return new Result(gain, matched);
    }

    /// <summary>Computes the matching scalar without processing PCM, for fused output passes.</summary>
    public static Result CalculateGain(double targetPower, double measuredPower)
    {
        if (!double.IsFinite(targetPower) || targetPower < 0)
            throw new ArgumentOutOfRangeException(nameof(targetPower));
        if (!double.IsFinite(measuredPower) || measuredPower < 0)
            throw new ArgumentOutOfRangeException(nameof(measuredPower));
        if (measuredPower == targetPower) return new Result(1, true);
        if (measuredPower == 0) return new Result(1, targetPower == 0);
        return new Result(Math.Sqrt(targetPower / measuredPower), true);
    }
}
