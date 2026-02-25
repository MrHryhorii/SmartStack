using NAudio.Wave;
using System.IO;

namespace ONNX_Runner;

public static class AudioProcessor
{
    public static float[] LoadVoiceSample(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Voice sample not found at: {filePath}");

        using var reader = new AudioFileReader(filePath);

        if (reader.WaveFormat.Channels > 1)
            Console.WriteLine("Warning: Stereo audio detected. The model expects mono audio.");

        var samples = new List<float>();
        float[] buffer = new float[reader.WaveFormat.SampleRate];
        int samplesRead;

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

    // Зберігає відбитки голосу у файл .bin
    public static void SaveVoiceSnapshot(string snapshotPath, float[] embeddings, float[] features)
    {
        using var fs = new FileStream(snapshotPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        writer.Write(embeddings.Length);
        foreach (var val in embeddings) writer.Write(val);

        writer.Write(features.Length);
        foreach (var val in features) writer.Write(val);

        Console.WriteLine($"Voice snapshot saved to: {snapshotPath}");
    }

    // Миттєво завантажує готові відбитки голосу
    public static (float[] embeddings, float[] features) LoadVoiceSnapshot(string snapshotPath)
    {
        using var fs = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(fs);

        int embLen = reader.ReadInt32();
        float[] embeddings = new float[embLen];
        for (int i = 0; i < embLen; i++) embeddings[i] = reader.ReadSingle();

        int featLen = reader.ReadInt32();
        float[] features = new float[featLen];
        for (int i = 0; i < featLen; i++) features[i] = reader.ReadSingle();

        return (embeddings, features);
    }
}