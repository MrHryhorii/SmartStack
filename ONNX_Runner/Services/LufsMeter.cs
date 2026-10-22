namespace ONNX_Runner.Services;

/// <summary>
/// Offline mono integrated loudness: BS.1770 K-weighting, 400 ms blocks,
/// 75% overlap, -70 LUFS absolute gate and -10 LU relative gate.
/// References shorter than 400 ms use one short block as an explicit extension.
/// </summary>
public static class LufsMeter
{
    private struct Biquad(double b0, double b1, double b2, double a1, double a2)
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

    private static (Biquad Shelf, Biquad HighPass) CreateKWeightingFilters(int sampleRate)
    {
        // Bilinear reconstruction of the BS.1770 48 kHz coefficient tables.
        // The RLB numerator is [1, -2, 1]; a normalized RBJ high-pass is different.
        const double shelfFrequency = 1681.974450955533;
        const double shelfGain = 3.999843853973347;
        const double shelfQ = .7071752369554196;
        double k = Math.Tan(Math.PI * shelfFrequency / sampleRate);
        double vh = Math.Pow(10, shelfGain / 20);
        double vb = Math.Pow(vh, .4996667741545416);
        double a0 = 1 + k / shelfQ + k * k;
        var shelf = new Biquad(
            (vh + vb * k / shelfQ + k * k) / a0,
            2 * (k * k - vh) / a0,
            (vh - vb * k / shelfQ + k * k) / a0,
            2 * (k * k - 1) / a0,
            (1 - k / shelfQ + k * k) / a0);

        const double highPassFrequency = 38.13547087602444;
        const double highPassQ = .5003270373238773;
        k = Math.Tan(Math.PI * highPassFrequency / sampleRate);
        a0 = 1 + k / highPassQ + k * k;
        var highPass = new Biquad(1, -2, 1,
            2 * (k * k - 1) / a0, (1 - k / highPassQ + k * k) / a0);
        return (shelf, highPass);
    }

    /// <summary>Returns gated mono LUFS, or negative infinity for silence or invalid PCM.</summary>
    public static double MeasureIntegratedLoudness(ReadOnlySpan<float> samples, int sampleRate)
    {
        var analyzer = new Analyzer(sampleRate);
        analyzer.BeginChunk();
        foreach (float sample in samples) analyzer.Add(sample);
        return analyzer.FinishChunk();
    }

    /// <summary>
    /// Reusable segment meter: filter state can span chunks, while gates are evaluated
    /// for the ready segment. Four 100 ms energy bins replace a full squared-PCM copy.
    /// The reusable block list grows only when a longer segment is encountered.
    /// </summary>
    internal sealed class Analyzer
    {
        private const double AbsolutePower = 1.172465304582298e-7;
        private readonly int _hopSamples, _blockSamples;
        private readonly double[] _bins;
        private readonly List<double> _powers = new(128);
        private Biquad _shelf, _highPass;
        private int _skip, _binSamples, _binPosition, _samples, _nextBlock;
        private double _binEnergy, _windowEnergy, _totalEnergy, _absoluteSum;
        private bool _invalid;

        internal Analyzer(int sampleRate, int initialSkip = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
            _hopSamples = Math.Max(1, sampleRate / 10);
            _blockSamples = Math.Max(1, (int)Math.Round(.4 * sampleRate));
            _bins = new double[_blockSamples / _hopSamples];
            _skip = initialSkip;
            (_shelf, _highPass) = CreateKWeightingFilters(sampleRate);
        }

        internal void BeginChunk()
        {
            Array.Clear(_bins);
            _powers.Clear();
            _binSamples = _binPosition = _samples = 0;
            _nextBlock = _blockSamples;
            _binEnergy = _windowEnergy = _totalEnergy = _absoluteSum = 0;
            _invalid = false;
        }

        internal void Add(float sample)
        {
            if (_skip > 0) { _skip--; return; }
            if (!float.IsFinite(sample)) { _invalid = true; sample = 0; }
            double weighted = _highPass.Process(_shelf.Process(sample));
            double energy = weighted * weighted;
            _totalEnergy += energy;
            _binEnergy += energy;
            _samples++;
            _binSamples++;
            if (_binSamples == _hopSamples)
            {
                _windowEnergy += _binEnergy - _bins[_binPosition];
                _bins[_binPosition] = _binEnergy;
                if (++_binPosition == _bins.Length) _binPosition = 0;
                _binEnergy = 0;
                _binSamples = 0;
            }
            if (_samples != _nextBlock) return;
            _nextBlock += _hopSamples;
            // Include the partial bin: 11025 Hz needs exactly 4410 samples per
            // block and a 1102-sample hop, not four rounded 100 ms bins.
            double power = (_windowEnergy + _binEnergy) / _blockSamples;
            if (power <= AbsolutePower) return;
            _powers.Add(power);
            _absoluteSum += power;
        }

        internal double FinishChunk()
        {
            if (_invalid || _samples == 0) return double.NegativeInfinity;
            if (_samples < _blockSamples)
            {
                double shortPower = _totalEnergy / _samples;
                return shortPower > AbsolutePower ? Loudness(shortPower) : double.NegativeInfinity;
            }
            if (_powers.Count == 0) return double.NegativeInfinity;
            double relativePower = _absoluteSum / _powers.Count / 10;
            double sum = 0;
            int count = 0;
            foreach (double power in _powers)
            {
                if (power <= relativePower) continue;
                sum += power;
                count++;
            }
            return count == 0 ? double.NegativeInfinity : Loudness(sum / count);
        }
    }

    private static double Loudness(double power) => -.691 + 10 * Math.Log10(power);

    /// <summary>Returns a relative linear gain; invalid or silent measurements retain unity.</summary>
    public static float GainForTarget(double measuredLufs, double targetLufs)
    {
        if (!double.IsFinite(measuredLufs) || !double.IsFinite(targetLufs)) return 1;
        float gain = (float)Math.Pow(10, (targetLufs - measuredLufs) / 20);
        return float.IsFinite(gain) && gain > 0 ? gain : 1;
    }
}
