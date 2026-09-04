namespace ONNX_Runner.Services;

/// <summary>
/// Measures integrated loudness per ITU-R BS.1770-4 (LUFS / EBU R128) and computes the target gain.
/// 
/// This approach is deliberately chosen over a simple flat RMS average to address two critical measurement issues:
/// 1. K-weighting: Applies psychoacoustic filtering (head effect + RLB high-pass) so bass-heavy 
///    voices aren't disproportionately measured as louder than they perceptually are.
/// 2. Gating: Excludes silence and pauses by analyzing overlapping 400ms blocks. It applies an 
///    absolute gate (-70 LUFS) and a relative gate (-10 LU below the ungated average). This prevents 
///    room-tone or silence at the edges of a reference clip from dragging down the average 
///    and over-boosting the actual speech.
/// 
/// Performance: Runs exclusively during voice-fingerprint creation, never per synthesis request, 
/// making the extra computational cost of K-weighting and gating a non-issue.
/// 
/// Implementation note: BS.1770-4 specifies K-weighting coefficients only for 48kHz. Because TTS 
/// models run at arbitrary rates (e.g., 16kHz, 22050Hz), the filters are dynamically re-derived 
/// per sample rate using RBJ Audio EQ Cookbook bilinear transforms (matches libebur128/pyloudnorm).
/// </summary>
public static class LufsMeter
{
    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateOffsetLu = -10.0;
    private const double BlockSeconds = 0.400;
    private const double BlockStepSeconds = 0.100; // 75% overlap between consecutive blocks

    /// <summary>
    /// A single second-order IIR section (Direct Form II Transposed). Kept private and minimal
    /// on purpose — this is only ever used as the two fixed K-weighting stages below, not a
    /// general-purpose filter utility (see Dsp.cs / SpatialEffectsEngine.cs for those).
    /// </summary>
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1, _z2;

        public double Process(double x)
        {
            double y = b0 * x + _z1;
            _z1 = b1 * x - a1 * y + _z2;
            _z2 = b2 * x - a2 * y;
            return y;
        }
    }

    /// <summary>
    /// Builds the two K-weighting stages for an arbitrary sample rate.
    /// </summary>
    private static (Biquad Stage1, Biquad Stage2) CreateKWeightingFilters(int sampleRate)
    {
        double fs = sampleRate;

        // --- Stage 1: high-shelf "head effect" boost ---
        const double f0Shelf = 1681.9744509555319;
        const double gainDb = 3.99984385397;
        const double qShelf = 0.7071752369554193;

        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * f0Shelf / fs;
        double alpha = Math.Sin(w0) / (2.0 * qShelf);
        double cosW0 = Math.Cos(w0);
        double sqrtA = Math.Sqrt(a);

        double b0S = a * ((a + 1) + (a - 1) * cosW0 + 2 * sqrtA * alpha);
        double b1S = -2 * a * ((a - 1) + (a + 1) * cosW0);
        double b2S = a * ((a + 1) + (a - 1) * cosW0 - 2 * sqrtA * alpha);
        double a0S = (a + 1) - (a - 1) * cosW0 + 2 * sqrtA * alpha;
        double a1S = 2 * ((a - 1) - (a + 1) * cosW0);
        double a2S = (a + 1) - (a - 1) * cosW0 - 2 * sqrtA * alpha;

        var stage1 = new Biquad(b0S / a0S, b1S / a0S, b2S / a0S, a1S / a0S, a2S / a0S);

        // --- Stage 2: RLB-weighting high-pass ---
        const double f0Hp = 38.13547087613982;
        const double qHp = 0.5003270373238773;

        double w0Hp = 2.0 * Math.PI * f0Hp / fs;
        double alphaHp = Math.Sin(w0Hp) / (2.0 * qHp);
        double cosW0Hp = Math.Cos(w0Hp);

        double b0H = (1 + cosW0Hp) / 2;
        double b1H = -(1 + cosW0Hp);
        double b2H = (1 + cosW0Hp) / 2;
        double a0H = 1 + alphaHp;
        double a1H = -2 * cosW0Hp;
        double a2H = 1 - alphaHp;

        var stage2 = new Biquad(b0H / a0H, b1H / a0H, b2H / a0H, a1H / a0H, a2H / a0H);

        return (stage1, stage2);
    }

    /// <summary>
    /// Measures the integrated (gated) loudness of a mono buffer, in LUFS, per BS.1770-4.
    /// Returns <see cref="double.NegativeInfinity"/> for a silent or empty buffer.
    /// </summary>
    public static double MeasureIntegratedLoudness(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0) return double.NegativeInfinity;

        var (stage1, stage2) = CreateKWeightingFilters(sampleRate);

        var weighted = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            weighted[i] = stage2.Process(stage1.Process(samples[i]));
        }

        int blockSize = (int)(BlockSeconds * sampleRate);
        int stepSize = (int)(BlockStepSeconds * sampleRate);
        if (blockSize <= 0 || weighted.Length < blockSize)
        {
            // Clip shorter than one 400ms block: measure it as a single block rather than
            // producing no reading at all — short reference clips still deserve a result.
            blockSize = weighted.Length;
            stepSize = blockSize;
        }

        var blockLoudness = new List<double>();
        var blockMeanSquares = new List<double>();

        for (int start = 0; start + blockSize <= weighted.Length; start += stepSize)
        {
            double sumSquares = 0;
            for (int i = start; i < start + blockSize; i++)
            {
                sumSquares += weighted[i] * weighted[i];
            }

            double meanSquare = sumSquares / blockSize;
            if (meanSquare <= 0) continue; // Digitally silent block — would give log10(0).

            blockMeanSquares.Add(meanSquare);
            blockLoudness.Add(-0.691 + 10.0 * Math.Log10(meanSquare));
        }

        if (blockMeanSquares.Count == 0) return double.NegativeInfinity;

        // Absolute gate: discard blocks quieter than -70 LUFS.
        var absoluteGated = new List<double>();
        for (int i = 0; i < blockLoudness.Count; i++)
        {
            if (blockLoudness[i] >= AbsoluteGateLufs) absoluteGated.Add(blockMeanSquares[i]);
        }
        if (absoluteGated.Count == 0) return double.NegativeInfinity;

        double avgMeanSquare1 = absoluteGated.Average();
        double avgLoudness1 = -0.691 + 10.0 * Math.Log10(avgMeanSquare1);

        // Relative gate: additionally discard blocks more than 10 LU below that average.
        double relativeThreshold = avgLoudness1 + RelativeGateOffsetLu;
        var relativeGated = new List<double>();
        for (int i = 0; i < blockLoudness.Count; i++)
        {
            if (blockLoudness[i] >= AbsoluteGateLufs && blockLoudness[i] >= relativeThreshold)
            {
                relativeGated.Add(blockMeanSquares[i]);
            }
        }
        // If relative gating removed everything (e.g. a very short, very uniform clip), fall
        // back to the absolute-gated result rather than reporting silence for real audio.
        if (relativeGated.Count == 0) return avgLoudness1;

        double finalMeanSquare = relativeGated.Average();
        return -0.691 + 10.0 * Math.Log10(finalMeanSquare);
    }

    /// <summary>
    /// Computes the linear gain needed to move a buffer measured at <paramref name="measuredLufs"/>
    /// to <paramref name="targetLufs"/>. Returns 1.0 (no change) for a silent buffer, since there's
    /// nothing meaningful to normalize.
    /// </summary>
    public static float GainForTarget(double measuredLufs, double targetLufs)
    {
        if (double.IsNegativeInfinity(measuredLufs)) return 1f;
        return (float)Math.Pow(10.0, (targetLufs - measuredLufs) / 20.0);
    }
}
