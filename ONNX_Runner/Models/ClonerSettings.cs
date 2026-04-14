namespace ONNX_Runner.Models;

/// <summary>
/// Configuration for the OpenVoice Voice Cloning module.
/// </summary>
public class ClonerSettings
{
    /// <summary>
    /// Global kill-switch for voice cloning. If false, the server ignores any cloning requests 
    /// and instantly returns the base Piper voice, drastically saving system resources.
    /// </summary>
    public bool EnableCloning { get; set; } = true;

    /// <summary>
    /// Controls the blending weight in the latent space.
    /// 1.0 = Exact copy of the target voice (Standard).
    /// 0.5 = 50% base voice, 50% target voice.
    /// 1.5 = Exaggerated target voice features.
    /// </summary>
    public float CloneIntensity { get; set; } = 1.0f;

    /// <summary>
    /// Tau parameter for adjusting tone diversity. 
    /// 1.0 = Standard. < 1.0 = More conservative/stable. > 1.0 = More expressive/diverse.
    /// </summary>
    public float ToneTemperature { get; set; } = 1.0f;
}