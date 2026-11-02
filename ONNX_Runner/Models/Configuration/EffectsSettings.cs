namespace ONNX_Runner.Models.Configuration;

/// <summary>
/// Global configuration for the Audio Effects Engine.
/// </summary>
public class EffectsSettings
{
    /// <summary>Master switch for equalizer profiles, character effects, and spatial environments, including reverb tails.</summary>
    public bool EnableGlobalEffects { get; set; } = true;

    /// <summary>Ordered playback profiles inherited by omitted/null request arrays; empty arrays bypass the layer.</summary>
    public string?[]? DefaultEqualizer { get; set; } = [];

    // Character Effects (Voice)
    public string DefaultEffect { get; set; } = "None";
    public float DefaultIntensity { get; set; } = 1.0f;

    // Spatial Effects (Environment)
    public string DefaultEnvironment { get; set; } = "None";
    public float DefaultEnvironmentIntensity { get; set; } = 0.25f;

    /// <summary>
    /// Allows the active spatial reverb tail to decay naturally to silence instead of cutting off abruptly.
    /// Only applies to spatial environments (e.g., Cave, ConcreteHall); character effects remain unaffected.
    /// </summary>
    public bool ExtendReverbTailOnFinish { get; set; } = true;

    /// <summary>
    /// The amplitude threshold (0.0 to 1.0) at which the spatial reverb tail is considered silent
    /// and safely cut off. 0.005 (~-46 dBFS) is the default perceptual silence threshold. 
    /// Lower values (e.g., 0.001) yield mathematically perfect tails but longer audio files.
    /// Higher values (e.g., 0.01) cut the tail faster, suitable for interactive or game audio.
    /// </summary>
    public float ReverbTailSilenceFloor { get; set; } = 0.005f;
}
