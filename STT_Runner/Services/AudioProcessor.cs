using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace STT_Runner.Services;

/// <summary>
/// Handles incoming audio files, normalizes them, and converts them to the 
/// 16kHz Mono float array format required by Whisper and Silero VAD models.
/// </summary>
public class AudioProcessor
{
    private const int TargetSampleRate = 16000;

    /// <summary>
    /// Reads an uploaded audio file, converts it to Mono, resamples to 16kHz, 
    /// and extracts the raw float samples for neural network inference.
    /// </summary>
    /// <param name="file">The audio file uploaded via HTTP multipart form data.</param>
    /// <returns>A normalized array of floats representing the audio waveform.</returns>
    public async Task<float[]> ProcessIncomingAudioAsync(IFormFile file)
    {
        // Save the incoming stream to a temporary file.
        // NAudio's AudioFileReader works best with physical files rather than memory streams, 
        // as it needs to probe the file headers to determine the codec (MP3, WAV, etc.).
        string tempPath = Path.GetTempFileName();

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            // Open the file with auto-format detection
            using var reader = new AudioFileReader(tempPath);
            ISampleProvider provider = reader;

            // Downmix to Mono if the audio has multiple channels
            if (reader.WaveFormat.Channels == 2)
            {
                Console.WriteLine("[AUDIO] Stereo file detected. Downmixing to mono...");
                provider = new StereoToMonoSampleProvider(provider)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
            }
            else if (reader.WaveFormat.Channels > 2)
            {
                provider = provider.ToMono(); // Handles 5.1 or 7.1 surround
            }

            // Resample to 16000 Hz if necessary
            if (provider.WaveFormat.SampleRate != TargetSampleRate)
            {
                Console.WriteLine($"[AUDIO] Resampling from {provider.WaveFormat.SampleRate}Hz to {TargetSampleRate}Hz...");
                provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);
            }

            // Read all processed samples into memory
            // A 1-minute audio file at 16kHz float takes exactly 3.84 MB of RAM.
            var audioSamples = new List<float>();
            float[] buffer = new float[TargetSampleRate]; // 1-second read buffer
            int read;

            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                // Only take the actual read amount (important for the last chunk)
                audioSamples.AddRange(new ReadOnlySpan<float>(buffer, 0, read).ToArray());
            }

            return [.. audioSamples];
        }
        finally
        {
            // Cleanup: Always delete the temporary file to prevent disk space leaks
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}