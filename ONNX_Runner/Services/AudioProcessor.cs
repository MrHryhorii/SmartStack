using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using ONNX_Runner.Models;
using System.Buffers;
using System.Numerics; // REQUIRED FOR SIMD (Hardware Acceleration)

namespace ONNX_Runner.Services;

/// <summary>
/// A lightweight wrapper that bridges raw float[] arrays directly to NAudio's IWaveProvider.
/// This prevents the heavy allocation overhead of converting float[] to byte[] arrays before resampling.
/// </summary>
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
            // Fast unmanaged memory copy
            Buffer.BlockCopy(_samples, _position * 4, buffer, offset, floatsToRead * 4);
            _position += floatsToRead;
        }
        return floatsToRead * 4;
    }
}

/// <summary>
/// High-performance audio processing engine.
/// Handles I/O operations, format normalization, and heavy DSP tasks like FFT and Spectrogram extraction.
/// Heavily utilizes ArrayPool to achieve Zero-Allocation during active processing.
/// </summary>
public class AudioProcessor
{
    private readonly int _fftSize;
    private readonly int _hopSize;

    // Cached Hanning window prevents recalculating trigonometric functions for every audio frame
    private readonly float[] _hanningWindow;

    public AudioProcessor(ToneConfig toneConfig)
    {
        _fftSize = toneConfig.Data.FilterLength;
        _hopSize = toneConfig.Data.HopLength;

        _hanningWindow = new float[_fftSize];
        for (int i = 0; i < _fftSize; i++)
        {
            // PERFORMANCE NOTE: Using MathF instead of Math ensures the operation stays 
            // strictly in 32-bit float space, avoiding costly double-to-float conversion overhead.
            _hanningWindow[i] = (float)(0.5 * (1.0 - MathF.Cos(2.0f * MathF.PI * i / (_fftSize - 1))));
        }
    }

    /// <summary>
    /// Reads a WAV file, enforces Mono channel mapping, matches the target sample rate, 
    /// and streams the normalized data directly into a rented memory pool buffer.
    /// </summary>
    public (float[] Buffer, int Length) LoadAndNormalizeWav(string path, int targetSampleRate)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;

        // --- STEREO TO MONO DOWNMIXING ---
        if (reader.WaveFormat.Channels == 2)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"      [INFO] Stereo file detected. Downmixing to mono (50% L / 50% R)...");
            Console.ResetColor();
            provider = new StereoToMonoSampleProvider(provider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }
        else if (reader.WaveFormat.Channels > 2)
        {
            provider = provider.ToMono(); // Handle 5.1 / 7.1 surround formats
        }

        // --- RESAMPLING ---
        if (provider.WaveFormat.SampleRate != targetSampleRate)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"      [INFO] Resampling from {provider.WaveFormat.SampleRate}Hz to {targetSampleRate}Hz...");
            Console.ResetColor();
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
        }

        // --- ZERO-ALLOCATION READING ---
        // Pre-allocate a large buffer (e.g., 30 seconds of audio) from the shared memory pool
        int initialSize = targetSampleRate * 30;
        float[] buffer = ArrayPool<float>.Shared.Rent(initialSize);

        int totalRead = 0;
        int read;

        // Read in 1-second chunks to maintain low active memory footprint
        float[] chunk = ArrayPool<float>.Shared.Rent(targetSampleRate);
        try
        {
            while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
            {
                // Dynamic resizing: If the audio is longer than the current buffer, 
                // rent a larger one, copy the data, and return the old one to the pool.
                if (totalRead + read > buffer.Length)
                {
                    float[] newBuffer = ArrayPool<float>.Shared.Rent(buffer.Length * 2);
                    Array.Copy(buffer, newBuffer, totalRead);
                    ArrayPool<float>.Shared.Return(buffer); // Release the old buffer immediately
                    buffer = newBuffer;
                }

                Array.Copy(chunk, 0, buffer, totalRead, read);
                totalRead += read;
            }
        }
        finally
        {
            // CRITICAL: Always return the temporary chunk array to the pool to prevent memory leaks
            ArrayPool<float>.Shared.Return(chunk);
        }

        return (buffer, totalRead);
    }

    /// <summary>
    /// Specialized internal WaveProvider that restricts reading strictly to the 'validLength' boundary 
    /// of a rented array. Since pooled arrays often contain trailing garbage data from previous uses, 
    /// this safety boundary is critical.
    /// </summary>
    public class FloatArrayWaveProvider(float[] samples, int validLength, int sampleRate) : IWaveProvider
    {
        private readonly float[] _samples = samples;
        private readonly int _validLength = validLength;
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(byte[] buffer, int offset, int count)
        {
            int floatsRequired = count / 4;
            // Respect the valid length boundary, ignoring trailing rented array garbage
            int floatsAvailable = _validLength - _position;
            int floatsToRead = Math.Min(floatsRequired, floatsAvailable);

            if (floatsToRead > 0)
            {
                Buffer.BlockCopy(_samples, _position * 4, buffer, offset, floatsToRead * 4);
                _position += floatsToRead;
            }
            return floatsToRead * 4;
        }
    }

    /// <summary>
    /// Resamples an audio buffer in memory. Strictly adheres to the rule: 
    /// "Resample ALWAYS returns a freshly rented array from the ArrayPool".
    /// </summary>
    public (float[] Buffer, int Length) Resample(float[] samples, int length, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate)
        {
            // Even if rates match, we clone into a rented array to maintain a predictable lifecycle
            // for the caller (who expects to return the result to the pool).
            float[] cloneBuffer = ArrayPool<float>.Shared.Rent(length);
            Array.Copy(samples, cloneBuffer, length);
            return (cloneBuffer, length);
        }

        var provider = new FloatArrayWaveProvider(samples, length, sourceRate);
        var resampler = new WdlResamplingSampleProvider(provider.ToSampleProvider(), targetRate);

        int expectedLength = (int)Math.Ceiling((double)length * targetRate / sourceRate) + 2000;

        // Rent memory for the output
        float[] buffer = ArrayPool<float>.Shared.Rent(expectedLength);

        int totalRead = 0;
        int read;

        // Read resampled audio directly into the rented output buffer
        while ((read = resampler.Read(buffer, totalRead, buffer.Length - totalRead)) > 0)
        {
            totalRead += read;
            if (totalRead >= buffer.Length) break;
        }

        return (buffer, totalRead);
    }

    /// <summary>
    /// Extracts a Linear Magnitude Spectrogram from raw PCM audio samples using Hardware Accelerated FFT.
    /// Uses ReadOnlySpan to inspect the array without copying it, further reducing memory allocations.
    /// </summary>
    public float[,] GetMagnitudeSpectrogram(ReadOnlySpan<float> samples)
    {
        int numFrames = (samples.Length - _fftSize) / _hopSize + 1;
        if (numFrames <= 0) return new float[0, 0];

        var spectrogram = new float[numFrames, (_fftSize / 2) + 1];
        int m = (int)Math.Log2(_fftSize);

        // Pre-allocate the Complex array ONCE per spectrogram generation, rather than per frame
        var complex = new NAudio.Dsp.Complex[_fftSize];

        // Determine how many floats the CPU can process in a single instruction (e.g., 8 for AVX2)
        int vectorSize = Vector<float>.Count;

        for (int i = 0; i < numFrames; i++)
        {
            var frame = samples.Slice(i * _hopSize, _fftSize);
            int j = 0;

            // --- SIMD VECTORIZATION (Hardware Acceleration) ---
            for (; j <= _fftSize - vectorSize; j += vectorSize)
            {
                // Load a vector of audio samples and a vector of Hanning window values simultaneously
                var vFrame = new Vector<float>(frame.Slice(j));
                var vWindow = new Vector<float>(_hanningWindow, j);

                // Multiply multiple numbers in a SINGLE CPU clock cycle
                var vResult = vFrame * vWindow;

                // Write the results back to the complex array
                for (int k = 0; k < vectorSize; k++)
                {
                    complex[j + k].X = vResult[k];
                    complex[j + k].Y = 0f;
                }
            }

            // --- SCALAR TAIL PROCESSING ---
            // Process any remaining elements if _fftSize is not perfectly divisible by the SIMD vector size
            for (; j < _fftSize; j++)
            {
                complex[j].X = frame[j] * _hanningWindow[j];
                complex[j].Y = 0f;
            }

            // Execute the Fast Fourier Transform (In-place)
            FastFourierTransform.FFT(true, m, complex);

            // Calculate the magnitude for each frequency bin
            for (int k = 0; k <= _fftSize / 2; k++)
            {
                // MathF.Sqrt executes directly on 32-bit floats, avoiding double conversion overhead
                spectrogram[i, k] = MathF.Sqrt(complex[k].X * complex[k].X + complex[k].Y * complex[k].Y) * _fftSize;
            }
        }

        return spectrogram;
    }
}