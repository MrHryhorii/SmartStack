using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using ONNX_Runner.Models;
using System.Buffers;
using System.Numerics; // ДОДАНО ДЛЯ SIMD

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
            // MathF замість Math (уникаємо конвертації double -> float)
            _hanningWindow[i] = (float)(0.5 * (1.0 - MathF.Cos(2.0f * MathF.PI * i / (_fftSize - 1))));
        }
    }

    // Читає WAV, зводить в моно (якщо потрібно), змінює частоту (якщо потрібно) і повертає орендований буфер з нормалізованими даними
    public (float[] Buffer, int Length) LoadAndNormalizeWav(string path, int targetSampleRate)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;

        // ПЕРЕВІРКА НА СТЕРЕО ТА ЗВЕДЕННЯ В МОНО
        if (reader.WaveFormat.Channels == 2)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"      [INFO] Файл стерео. Зводимо в моно (50% L / 50% R)...");
            Console.ResetColor();
            provider = new StereoToMonoSampleProvider(provider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }
        else if (reader.WaveFormat.Channels > 2)
        {
            provider = provider.ToMono(); // Для 5.1 / 7.1
        }

        // РЕСЕМПЛІНГ
        if (provider.WaveFormat.SampleRate != targetSampleRate)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"      [INFO] Зміна частоти з {provider.WaveFormat.SampleRate}Hz на {targetSampleRate}Hz...");
            Console.ResetColor();
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
        }

        // ЗЧИТУВАННЯ ПРЯМО В ОРЕНДОВАНИЙ МАСИВ
        // Виділяємо буфер із запасом на 30 секунд аудіо
        int initialSize = targetSampleRate * 30;
        float[] buffer = ArrayPool<float>.Shared.Rent(initialSize);

        int totalRead = 0;
        int read;

        // Читаємо шматками по 1 секунді (щоб не перевантажувати пам'ять)
        float[] chunk = ArrayPool<float>.Shared.Rent(targetSampleRate);
        try
        {
            while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
            {
                // Якщо буфер заповнений, орендуємо вдвічі більший і копіюємо дані
                if (totalRead + read > buffer.Length)
                {
                    float[] newBuffer = ArrayPool<float>.Shared.Rent(buffer.Length * 2);
                    Array.Copy(buffer, newBuffer, totalRead);
                    ArrayPool<float>.Shared.Return(buffer); // Повертаємо старий буфер
                    buffer = newBuffer;
                }

                Array.Copy(chunk, 0, buffer, totalRead, read);
                totalRead += read;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(chunk); // Завжди повертаємо тимчасовий буфер (chunk)
        }

        return (buffer, totalRead);
    }

    public class FloatArrayWaveProvider(float[] samples, int validLength, int sampleRate) : IWaveProvider
    {
        private readonly float[] _samples = samples;
        private readonly int _validLength = validLength;
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(byte[] buffer, int offset, int count)
        {
            int floatsRequired = count / 4;
            int floatsAvailable = _validLength - _position; // Читаємо тільки до межі корисних даних!
            int floatsToRead = Math.Min(floatsRequired, floatsAvailable);

            if (floatsToRead > 0)
            {
                Buffer.BlockCopy(_samples, _position * 4, buffer, offset, floatsToRead * 4);
                _position += floatsToRead;
            }
            return floatsToRead * 4;
        }
    }

    public (float[] Buffer, int Length) Resample(float[] samples, int length, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate)
        {
            // Орендуємо масив, щоб дотримуватись єдиного правила: "Resample завжди повертає орендований масив"
            float[] cloneBuffer = ArrayPool<float>.Shared.Rent(length);
            Array.Copy(samples, cloneBuffer, length);
            return (cloneBuffer, length);
        }

        var provider = new FloatArrayWaveProvider(samples, length, sourceRate);
        var resampler = new WdlResamplingSampleProvider(provider.ToSampleProvider(), targetRate);

        int expectedLength = (int)Math.Ceiling((double)length * targetRate / sourceRate) + 2000;

        // ОРЕНДУЄМО МАСИВ
        float[] buffer = ArrayPool<float>.Shared.Rent(expectedLength);

        int totalRead = 0;
        int read;

        // Читаємо звук ПРЯМО в орендований масив
        while ((read = resampler.Read(buffer, totalRead, buffer.Length - totalRead)) > 0)
        {
            totalRead += read;
            if (totalRead >= buffer.Length) break;
        }

        // Повертаємо орендований буфер і кількість корисних даних у ньому
        return (buffer, totalRead);
    }

    // Приймає ReadOnlySpan (дивиться на масив без копіювання)
    public float[,] GetMagnitudeSpectrogram(ReadOnlySpan<float> samples)
    {
        int numFrames = (samples.Length - _fftSize) / _hopSize + 1;
        if (numFrames <= 0) return new float[0, 0];

        var spectrogram = new float[numFrames, (_fftSize / 2) + 1];
        int m = (int)Math.Log2(_fftSize);

        // Створюємо масив Complex ОДИН РАЗ
        var complex = new NAudio.Dsp.Complex[_fftSize];

        // Дізнаємося, скільки чисел за раз може обробити процесор (зазвичай 8 для AVX2)
        int vectorSize = Vector<float>.Count;

        for (int i = 0; i < numFrames; i++)
        {
            var frame = samples.Slice(i * _hopSize, _fftSize);
            int j = 0;

            // SIMD-векторизація (Апаратне прискорення)
            for (; j <= _fftSize - vectorSize; j += vectorSize)
            {
                // Завантажуємо 8 чисел з аудіо та 8 чисел з вікна Ганна
                var vFrame = new Vector<float>(frame.Slice(j));
                var vWindow = new Vector<float>(_hanningWindow, j);

                // Множимо 8 чисел за ОДИН такт процесора
                var vResult = vFrame * vWindow;

                // Записуємо результат
                for (int k = 0; k < vectorSize; k++)
                {
                    complex[j + k].X = vResult[k];
                    complex[j + k].Y = 0f;
                }
            }

            // Обробляємо хвіст (якщо _fftSize не ділиться на 8 націло)
            for (; j < _fftSize; j++)
            {
                complex[j].X = frame[j] * _hanningWindow[j];
                complex[j].Y = 0f;
            }

            // Виконуємо Швидке Перетворення Фур'є
            FastFourierTransform.FFT(true, m, complex);

            for (int k = 0; k <= _fftSize / 2; k++)
            {
                // MathF.Sqrt виконує операцію напряму в float (без конвертації в double)
                spectrogram[i, k] = MathF.Sqrt(complex[k].X * complex[k].X + complex[k].Y * complex[k].Y) * _fftSize;
            }
        }

        return spectrogram;
    }
}