using System.Text.Json.Serialization;

namespace ONNX_Runner.Models;

public class ToneConfig
{
    [JsonPropertyName("data")]
    public ToneDataConfig Data { get; set; } = new();

    [JsonPropertyName("model")]
    public ToneModelConfig Model { get; set; } = new();
}

public class ToneDataConfig
{
    [JsonPropertyName("sampling_rate")]
    public int SamplingRate { get; set; }

    [JsonPropertyName("filter_length")]
    public int FilterLength { get; set; }

    [JsonPropertyName("hop_length")]
    public int HopLength { get; set; }
}

public class ToneModelConfig
{
    [JsonPropertyName("gin_channels")]
    public int GinChannels { get; set; }
}