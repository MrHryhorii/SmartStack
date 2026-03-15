using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public class AudioProcessor(ToneConfig toneConfig)
{
    private readonly int _fftSize = toneConfig.Data.FilterLength;
    private readonly int _hopSize = toneConfig.Data.HopLength;

    public (float[] samples, int sampleRate) LoadWav(string path)
    {
        using var reader = new AudioFileReader(path);
        int rate = reader.WaveFormat.SampleRate; // Дізнаємося реальну частоту файлу

        var allSamples = new List<float>();
        float[] buffer = new float[rate];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            allSamples.AddRange(buffer.Take(read));
        }

        Console.WriteLine($"[DEBUG-FILE] Path: {Path.GetFileName(path)}, Rate: {rate}Hz, Samples: {allSamples.Count}");
        return (allSamples.ToArray(), rate);
    }

    public float[] Resample(float[] samples, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate) return samples;

        // Готуємо формат та джерело даних
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 1);

        // Перетворюємо float[] у байтовий потік, який розуміє NAudio
        byte[] byteArray = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, byteArray, 0, byteArray.Length);

        using var ms = new MemoryStream(byteArray);
        var rawSource = new RawSourceWaveStream(ms, waveFormat);
        var sampleProvider = rawSource.ToSampleProvider();

        // Створюємо ресемплер
        var resampler = new WdlResamplingSampleProvider(sampleProvider, targetRate);

        // Читаємо результат до останнього семпла
        var outSamples = new List<float>();
        float[] buffer = new float[targetRate]; // Читаємо порціями по 1 секунді
        int read;

        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            outSamples.AddRange(buffer.Take(read));
        }

        return outSamples.ToArray();
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
                spectrogram[i, j] = (float)Math.Sqrt(complex[j].X * complex[j].X + complex[j].Y * complex[j].Y) * _fftSize;
            }
        }

        return spectrogram;
    }
}