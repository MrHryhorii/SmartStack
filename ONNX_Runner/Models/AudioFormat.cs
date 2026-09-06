namespace ONNX_Runner.Models;

/// <summary>
/// Defines the supported audio output formats for the TTS engine.
/// 
/// Used in AudioStreamManager.
/// </summary>
public enum AudioFormat
{
    Wav,
    Mp3,
    Opus,
    Pcm,
    B64Json
}