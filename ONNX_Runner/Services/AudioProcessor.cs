using NAudio.Wave;
using NAudio.Dsp;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public class AudioProcessor(ToneConfig toneConfig)
{
    private readonly int _fftSize = toneConfig.Data.FilterLength;
    private readonly int _hopSize = toneConfig.Data.HopLength;

    public float[] LoadWav(string path)
    {
        using var reader = new AudioFileReader(path);
        var samples = new float[reader.Length / 4];
        int read = reader.Read(samples, 0, samples.Length);
        return [.. samples.Take(read)];
    }

    public float[] Resample(float[] samples, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate) return samples;

        var resampler = new WdlResampler();
        resampler.SetRates(sourceRate, targetRate);
        resampler.SetFeedMode(true);

        int expectedFrames = (int)((long)samples.Length * targetRate / sourceRate);
        float[] outBuffer = new float[expectedFrames + 100];

        resampler.ResamplePrepare(samples.Length, 1, out float[] inBuffer, out int inBufferOffset);
        Array.Copy(samples, 0, inBuffer, inBufferOffset, samples.Length);

        int written = resampler.ResampleOut(outBuffer, 0, expectedFrames, 1, 1);

        return [.. outBuffer.Take(written)];
    }

    public float[,] GetMagnitudeSpectrogram(float[] samples)
    {
        // Розрахунок кадрів динамічний
        int numFrames = (samples.Length - _fftSize) / _hopSize + 1;
        if (numFrames <= 0) return new float[0, 0];

        // Кількість частотних бінів: (N/2) + 1
        var spectrogram = new float[numFrames, (_fftSize / 2) + 1];

        // Вікно Ганна підлаштовується під _fftSize
        float[] window = new float[_fftSize];
        for (int i = 0; i < _fftSize; i++)
            window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (_fftSize - 1))));

        int m = (int)Math.Log2(_fftSize);

        for (int i = 0; i < numFrames; i++)
        {
            var complex = new Complex[_fftSize];
            for (int j = 0; j < _fftSize; j++)
            {
                complex[j].X = samples[i * _hopSize + j] * window[j];
                complex[j].Y = 0;
            }

            FastFourierTransform.FFT(true, m, complex);

            for (int j = 0; j <= _fftSize / 2; j++)
            {
                spectrogram[i, j] = (float)Math.Sqrt(complex[j].X * complex[j].X + complex[j].Y * complex[j].Y);
            }
        }

        return spectrogram;
    }
}