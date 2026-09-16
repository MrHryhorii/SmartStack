using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using ONNX_Runner.Models;
using System.Buffers;
using System.Numerics; // REQUIRED FOR SIMD (Hardware Acceleration)
using System.Runtime.InteropServices; // REQUIRED FOR MemoryMarshal (fast flat access to rectangular arrays)

namespace ONNX_Runner.Services;

/// <summary>
/// High-performance audio processing engine.
/// Handles I/O operations, format normalization, and heavy DSP tasks like FFT and Spectrogram extraction.
/// Heavily utilizes ArrayPool to achieve Zero-Allocation during active processing.
/// </summary>
public class AudioProcessor
{
    private const float OpenVoiceMagnitudeEpsilon = 1e-6f;

    private readonly int _fftSize;
    private readonly int _hopSize;
    private readonly int _reflectPadding;
    private readonly float _scaledMagnitudeEpsilon;

    // OpenVoice uses torch.hann_window(periodic: true), so the denominator is N rather than N - 1.
    private readonly float[] _hanningWindow;

    public AudioProcessor(ToneConfig toneConfig)
    {
        _fftSize = toneConfig.Data.FilterLength;
        _hopSize = toneConfig.Data.HopLength;

        if (_fftSize <= 0 || (_fftSize & (_fftSize - 1)) != 0)
            throw new ArgumentException("OpenVoice FFT size must be a positive power of two.", nameof(toneConfig));

        if (_hopSize <= 0 || _hopSize > _fftSize)
            throw new ArgumentException("OpenVoice hop size must be between 1 and the FFT size.", nameof(toneConfig));

        _reflectPadding = (_fftSize - _hopSize) / 2;

        // NAudio scales the forward FFT by 1/N. Scale epsilon by 1/N^2 so multiplying
        // the final magnitude by N remains equivalent to OpenVoice's unnormalized STFT.
        _scaledMagnitudeEpsilon = OpenVoiceMagnitudeEpsilon / (_fftSize * (float)_fftSize);

        _hanningWindow = new float[_fftSize];
        for (int i = 0; i < _fftSize; i++)
        {
            _hanningWindow[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / _fftSize));
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
    private sealed class FloatArrayWaveProvider(float[] samples, int validLength, int sampleRate) : IWaveProvider
    {
        private readonly float[] _samples = samples;
        private readonly int _validLength = validLength;
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        /// <summary>
        /// Copies available float samples into the requested byte buffer.
        /// </summary>
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
    ///
    /// SAFETY NOTES:
    ///   - The output buffer is sized via expectedLength, which scales its safety margin
    ///     relative to targetRate (rather than a fixed sample count) so the margin stays
    ///     proportionally correct whether resampling down to 8kHz or up to 96kHz+.
    ///   - The resampling loop is wrapped in try/finally: if WdlResamplingSampleProvider
    ///     throws mid-read (e.g. malformed source audio), the rented buffer is still
    ///     returned to the pool instead of leaking, preserving the Zero-Allocation contract
    ///     even on the error path.
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

        // Safety margin scales with targetRate: a fixed sample-count margin (e.g. +2000)
        // would be a generous ~250ms at 8kHz but a thin ~21ms at 96kHz. Using a relative
        // margin (5% of the expected length, floored at 256 samples) keeps the buffer
        // adequately sized across the full range of TTS sample rates.
        int rawExpectedLength = (int)Math.Ceiling((double)length * targetRate / sourceRate);
        int safetyMargin = Math.Max(256, rawExpectedLength / 20); // 5% margin, 256-sample floor
        int expectedLength = rawExpectedLength + safetyMargin;

        // Rent memory for the output
        float[] buffer = ArrayPool<float>.Shared.Rent(expectedLength);

        try
        {
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
        catch
        {
            // Preserve the Zero-Allocation contract on the error path: return the
            // rented buffer before propagating, so a malformed source file or an
            // internal resampler fault never leaks pooled memory.
            ArrayPool<float>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>
    /// Normalizes an audio buffer to target integrated loudness (LUFS, ITU-R BS.1770-4) in place,
    /// applying VolumeShifter's soft-knee limiter to prevent peak clipping.
    /// Used for reference audio to ensure consistent tone embeddings during voice cloning.
    /// Runs exclusively during fingerprint creation (startup/cache miss), never per synthesis request.
    /// </summary>
    public void NormalizeLufs(Span<float> buffer, int sampleRate, float targetLufs = -23f)
    {
        if (buffer.Length == 0) return;

        double measured = LufsMeter.MeasureIntegratedLoudness(buffer, sampleRate);
        float gain = LufsMeter.GainForTarget(measured, targetLufs);
        VolumeShifter.ApplyVolume(buffer, gain);
    }

    /// <summary>
    /// Extracts the OpenVoice-compatible linear magnitude spectrogram in [frames, bins] layout.
    /// The waveform is reflect-padded before STFT, matching OpenVoice spectrogram_torch(center: false).
    /// </summary>
    public float[,] GetMagnitudeSpectrogram(ReadOnlySpan<float> samples)
    {
        float[]? padded = RentReflectPadded(samples, out int paddedLength);
        if (padded == null) return new float[0, 0];

        try
        {
            int numFrames = GetFrameCount(paddedLength);
            if (numFrames <= 0) return new float[0, 0];

            int bins = (_fftSize / 2) + 1;
            var spectrogram = new float[numFrames, bins];
            int tensorSize = checked(numFrames * bins);

            // Rectangular arrays are contiguous in row-major order.
            System.Diagnostics.Debug.Assert(tensorSize == spectrogram.Length,
                "Spectrogram element count must match frames * bins.");

            Span<float> destination = MemoryMarshal.CreateSpan(ref spectrogram[0, 0], tensorSize);
            var complex = new NAudio.Dsp.Complex[_fftSize];
            int fftPower = (int)Math.Log2(_fftSize);
            int vectorSize = Vector<float>.Count;

            for (int frameIndex = 0; frameIndex < numFrames; frameIndex++)
            {
                ReadOnlySpan<float> frame = padded.AsSpan(frameIndex * _hopSize, _fftSize);
                FillWindowedFrame(frame, complex, vectorSize);
                FastFourierTransform.FFT(true, fftPower, complex);

                int rowOffset = frameIndex * bins;
                for (int bin = 0; bin < bins; bin++)
                {
                    destination[rowOffset + bin] = GetOpenVoiceMagnitude(complex[bin]);
                }
            }

            return spectrogram;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(padded);
        }
    }

    /// <summary>
    /// Runtime OpenVoice path. Produces the same spectrogram directly in [bins, frames] layout
    /// required by the Tone Color Converter. The caller owns the returned pooled buffer.
    /// </summary>
    public (float[]? Buffer, int Frames, int Bins) GetColorizerSpectrogram(ReadOnlySpan<float> samples)
    {
        float[]? padded = RentReflectPadded(samples, out int paddedLength);
        if (padded == null) return (null, 0, 0);

        int numFrames = GetFrameCount(paddedLength);
        if (numFrames <= 0)
        {
            ArrayPool<float>.Shared.Return(padded);
            return (null, 0, 0);
        }

        int bins = (_fftSize / 2) + 1;
        int tensorSize = checked(numFrames * bins);
        float[] spectrogram = ArrayPool<float>.Shared.Rent(tensorSize);
        NAudio.Dsp.Complex[] complex = ArrayPool<NAudio.Dsp.Complex>.Shared.Rent(_fftSize);

        try
        {
            Span<float> destination = spectrogram.AsSpan(0, tensorSize);
            int fftPower = (int)Math.Log2(_fftSize);
            int vectorSize = Vector<float>.Count;

            for (int frameIndex = 0; frameIndex < numFrames; frameIndex++)
            {
                ReadOnlySpan<float> frame = padded.AsSpan(frameIndex * _hopSize, _fftSize);
                FillWindowedFrame(frame, complex, vectorSize);
                FastFourierTransform.FFT(true, fftPower, complex);

                // The converter consumes [1, bins, frames], so write directly in transposed layout.
                for (int bin = 0; bin < bins; bin++)
                {
                    destination[(bin * numFrames) + frameIndex] = GetOpenVoiceMagnitude(complex[bin]);
                }
            }

            return (spectrogram, numFrames, bins);
        }
        catch
        {
            ArrayPool<float>.Shared.Return(spectrogram);
            throw;
        }
        finally
        {
            ArrayPool<NAudio.Dsp.Complex>.Shared.Return(complex);
            ArrayPool<float>.Shared.Return(padded);
        }
    }

    // OpenVoice pads by (n_fft - hop_size) / 2 on both sides with reflect mode.
    private float[]? RentReflectPadded(ReadOnlySpan<float> samples, out int paddedLength)
    {
        paddedLength = 0;

        // Reflect padding excludes the edge sample and therefore requires input longer than the pad.
        if (samples.Length <= _reflectPadding) return null;

        paddedLength = checked(samples.Length + (_reflectPadding * 2));
        float[] padded = ArrayPool<float>.Shared.Rent(paddedLength);
        Span<float> destination = padded.AsSpan(0, paddedLength);

        samples.CopyTo(destination.Slice(_reflectPadding, samples.Length));

        for (int i = 0; i < _reflectPadding; i++)
        {
            destination[_reflectPadding - 1 - i] = samples[i + 1];
            destination[_reflectPadding + samples.Length + i] = samples[samples.Length - 2 - i];
        }

        return padded;
    }

    private int GetFrameCount(int paddedLength)
    {
        if (paddedLength < _fftSize) return 0;

        return ((paddedLength - _fftSize) / _hopSize) + 1;
    }

    private void FillWindowedFrame(
        ReadOnlySpan<float> frame,
        Span<NAudio.Dsp.Complex> complex,
        int vectorSize)
    {
        int i = 0;

        for (; i <= _fftSize - vectorSize; i += vectorSize)
        {
            var frameVector = new Vector<float>(frame.Slice(i));
            var windowVector = new Vector<float>(_hanningWindow, i);
            var windowed = frameVector * windowVector;

            for (int lane = 0; lane < vectorSize; lane++)
            {
                complex[i + lane].X = windowed[lane];
                complex[i + lane].Y = 0f;
            }
        }

        for (; i < _fftSize; i++)
        {
            complex[i].X = frame[i] * _hanningWindow[i];
            complex[i].Y = 0f;
        }
    }

    private float GetOpenVoiceMagnitude(NAudio.Dsp.Complex value)
    {
        float power = (value.X * value.X) + (value.Y * value.Y) + _scaledMagnitudeEpsilon;
        return MathF.Sqrt(power) * _fftSize;
    }
}
