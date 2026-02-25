using NAudio.Wave;

namespace ONNX_Runner;

/// <summary>
/// Handles the loading and conversion of audio files for the neural network.
/// </summary>
public static class AudioProcessor
{
    /// <summary>
    /// Reads a .wav file and converts its PCM data into a normalized float array.
    /// </summary>
    public static float[] LoadVoiceSample(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Voice sample not found at: {filePath}");
        }

        // AudioFileReader automatically handles format conversion (16-bit, 24-bit) 
        // and normalizes the output to floats between -1.0f and 1.0f
        using var reader = new AudioFileReader(filePath);

        // Models typically expect Mono audio (1 channel). 
        // If the sample is stereo, we should ideally mix it down, but AudioFileReader 
        // reads interleaved samples. For standard TTS, providing a mono file is best.
        if (reader.WaveFormat.Channels > 1)
        {
            Console.WriteLine("Warning: Stereo audio detected. The model expects mono audio for best results.");
        }

        var samples = new List<float>();
        float[] buffer = new float[reader.WaveFormat.SampleRate];
        int samplesRead;

        // Read the audio file in chunks to prevent memory spikes
        while ((samplesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < samplesRead; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        Console.WriteLine($"Voice sample loaded: {samples.Count} samples at {reader.WaveFormat.SampleRate}Hz");
        return [.. samples];
    }
}