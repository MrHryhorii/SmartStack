using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

/// <summary>
/// Fixed voice-radio filtering for strong, clean reception.
/// Keeps the accepted linear radio response; final peak protection belongs to the output stage.
/// This is an audio-domain approximation, not an RF modem or a digital voice codec.
/// </summary>
internal sealed class FmRadioProcessor
{
    private const int Oversampling = 8;
    private const int ResamplingDelay = 64;
    private const float OutputGain = 1.3728f;
    private static readonly float[] ResamplingKernel = CreateLinearResamplingKernel();
    private static readonly ConcurrentDictionary<int, float[]> ChannelKernels = new();

    private readonly FmRadioEqualizer _microphone;
    private readonly float[] _channelKernel;
    private readonly float[] _channelHistory;
    private readonly float[] _dryHistory;
    private readonly float[] _inputHistory = new float[128];
    private int _inputPosition;
    private int _channelPosition;
    private int _dryPosition;

    internal int DelaySamples { get; }

    internal FmRadioProcessor(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _microphone = new FmRadioEqualizer(sampleRate);
        _channelKernel = ChannelKernels.GetOrAdd(sampleRate, CreateChannelKernel);
        _channelHistory = new float[2 * Capacity(_channelKernel.Length)];
        DelaySamples = LatencyAt(sampleRate);
        _dryHistory = new float[Capacity(DelaySamples + 1)];


    }

    internal void Reset()
    {
        _microphone.Reset();
        Array.Clear(_inputHistory);
        Array.Clear(_channelHistory);
        Array.Clear(_dryHistory);
        _inputPosition = _channelPosition = _dryPosition = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float Process(float sample, out float dry)
    {
        int dryMask = _dryHistory.Length - 1;
        _dryHistory[_dryPosition] = sample;
        dry = _dryHistory[(_dryPosition - DelaySamples) & dryMask];
        _dryPosition = (_dryPosition + 1) & dryMask;

        _inputHistory[_inputPosition] = _inputHistory[_inputPosition + 64] = _microphone.Process(sample, out _);
        float recovered = FmFir.Process(ResamplingKernel, _inputHistory, _inputPosition + 65 - ResamplingKernel.Length);
        _inputPosition = (_inputPosition + 1) & 63;

        _channelHistory[_channelPosition] = _channelHistory[_channelPosition + _channelHistory.Length / 2] = recovered;
        float output = Filter(_channelKernel, _channelHistory, _channelPosition);
        _channelPosition = (_channelPosition + 1) & (_channelHistory.Length / 2 - 1);
        return output * OutputGain;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Filter(float[] kernel, float[] history, int position)
    {
        return FmFir.Process(kernel, history, position + history.Length / 2 - kernel.Length + 1);
    }

    private static float[] CreateLinearResamplingKernel()
    {
        // Collapse the former interpolation/decimation pair at phase zero. With the
        // complementary emphasis pair removed, this is its exact linear host-rate FIR.
        float[] decimation = LowPass(2 * ResamplingDelay + 1, .5 / Oversampling);
        var interpolation = (float[])decimation.Clone();
        for (int phase = 0; phase < Oversampling; phase++)
        {
            double sum = 0;
            for (int i = phase; i < interpolation.Length; i += Oversampling) sum += interpolation[i];
            for (int i = phase; i < interpolation.Length; i += Oversampling) interpolation[i] = (float)(interpolation[i] / sum);
        }
        var kernel = new float[4 * ResamplingDelay / Oversampling + 1];
        for (int i = 0; i < kernel.Length; i++)
        {
            double sum = 0;
            for (int tap = 0; tap < decimation.Length; tap++)
            {
                int other = i * Oversampling - tap;
                if ((uint)other < (uint)interpolation.Length) sum += decimation[tap] * (double)interpolation[other];
            }
            kernel[i] = (float)sum;
        }
        return kernel;
    }

    internal static int LatencyAt(int sampleRate) => FmRadioEqualizer.LatencyAt(sampleRate)
        + 2 * ResamplingDelay / Oversampling + ChannelDelayAt(sampleRate);

    private static int ChannelDelayAt(int sampleRate) =>
        Math.Max(8, (int)Math.Round(sampleRate * (64.0 / 22050)));

    private static float[] CreateChannelKernel(int sampleRate)
    {
        int delay = ChannelDelayAt(sampleRate);
        return LowPass(2 * delay + 1, Math.Min(3200, sampleRate * .45) / sampleRate);
    }

    private static float[] LowPass(int length, double cutoff)
    {
        const double beta = 7.5;
        int delay = length / 2;
        double denominator = BesselI0(beta);
        var kernel = new float[length];
        double sum = 0;
        for (int i = 0; i <= delay; i++)
        {
            int offset = i - delay;
            double sinc = offset == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * offset) / (Math.PI * offset);
            double x = offset / (double)delay;
            float value = (float)(sinc * BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - x * x))) / denominator);
            kernel[i] = kernel[length - 1 - i] = value;
            sum += i == delay ? value : 2.0 * value;
        }
        for (int i = 0; i < length; i++) kernel[i] = (float)(kernel[i] / sum);
        return kernel;
    }

    private static int Capacity(int length)
    {
        int capacity = 1;
        while (capacity < length) capacity <<= 1;
        return capacity;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 40; k++)
        {
            term *= x * x / (4.0 * k * k);
            sum += term;
            if (term < sum * 1e-16) break;
        }
        return sum;
    }
}
