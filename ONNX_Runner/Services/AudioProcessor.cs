using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using ONNX_Runner.Models;
using System.Buffers;

namespace ONNX_Runner.Services;

// Запобігає конвертації float[] у byte[] перед ресемплінгом
public class FloatArrayWaveProvider(float[] samples, int sampleRate) : IWaveProvider
{
    private readonly float[] _samples = samples;
    private int _position;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

    public int Read(byte[] buffer, int offset, int count)
    {
        int floatsRequired = count / 4;
        int floatsAvailable = _samples.Length - _position;
        int floatsToRead = Math.Min(floatsRequired, floatsAvailable);

        if (floatsToRead > 0)
        {
            Buffer.BlockCopy(_samples, _position * 4, buffer, offset, floatsToRead * 4);
            _position += floatsToRead;
        }
        return floatsToRead * 4;
    }
}

public class AudioProcessor
{
    private readonly int _fftSize;
    private readonly int _hopSize;
    private readonly float[] _hanningWindow; // Кешуємо вікно раз і назавжди

    public AudioProcessor(ToneConfig toneConfig)
    {
        _fftSize = toneConfig.Data.FilterLength;
        _hopSize = toneConfig.Data.HopLength;

        _hanningWindow = new float[_fftSize];
        for (int i = 0; i < _fftSize; i++)
        {
            _hanningWindow[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (_fftSize - 1))));
        }
    }

    public (float[] samples, int sampleRate) LoadWav(string path)
    {
        using var reader = new AudioFileReader(path);
        int rate = reader.WaveFormat.SampleRate;

        var allSamples = new List<float>();
        float[] buffer = new float[rate];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            allSamples.AddRange(buffer.Take(read));
        }
        return (allSamples.ToArray(), rate);
    }

    public float[] Resample(float[] samples, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate) return samples;

        // Використовуємо новий провайдер (НУЛЬ КОПІЮВАНЬ ПАМ'ЯТІ!)
        var provider = new FloatArrayWaveProvider(samples, sourceRate);
        var resampler = new WdlResamplingSampleProvider(provider.ToSampleProvider(), targetRate);

        // Попередньо виділяємо точну кількість пам'яті
        int expectedLength = (int)((double)samples.Length * targetRate / sourceRate) + 1000;
        var outSamples = new List<float>(expectedLength);

        // Беремо буфер з пулу, щоб не напрягати GC
        float[] buffer = ArrayPool<float>.Shared.Rent(targetRate);
        try
        {
            int read;
            while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                outSamples.AddRange(new ArraySegment<float>(buffer, 0, read));
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }

        return [.. outSamples];
    }

    // Приймає ReadOnlySpan (дивиться на масив без копіювання)
    public float[,] GetMagnitudeSpectrogram(ReadOnlySpan<float> samples)
    {
        int numFrames = (samples.Length - _fftSize) / _hopSize + 1;
        if (numFrames <= 0) return new float[0, 0];

        var spectrogram = new float[numFrames, (_fftSize / 2) + 1];
        int m = (int)Math.Log2(_fftSize);

        // ОПТИМІЗАЦІЯ ПАМ'ЯТІ: Створюємо масив Complex ОДИН РАЗ, а не тисячі разів у циклі
        var complex = new Complex[_fftSize];

        for (int i = 0; i < numFrames; i++)
        {
            var frame = samples.Slice(i * _hopSize, _fftSize);

            for (int j = 0; j < _fftSize; j++)
            {
                complex[j].X = frame[j] * _hanningWindow[j];
                complex[j].Y = 0;
            }

            FastFourierTransform.FFT(true, m, complex);

            for (int j = 0; j <= _fftSize / 2; j++)
            {
                spectrogram[i, j] = (float)Math.Sqrt(complex[j].X * complex[j].X + complex[j].Y * complex[j].Y) * _fftSize;
            }
        }

        return spectrogram;
    }
}