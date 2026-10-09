namespace ONNX_Runner.Services;

// Test-only boundary spy. The real text pipeline calls this in place of the native
// eSpeak bridge; returning the core unchanged exposes punctuation and spacing decisions.
// No pronunciation, native library, or waveform behavior is simulated or asserted.
public sealed class EspeakWrapper
{
    public List<(string Text, string Voice)> Calls { get; } = [];

    public bool TryGetIpaPhonemes(string text, string voice, out string phonemes)
    {
        Calls.Add((text, voice));
        phonemes = text;
        return true;
    }
}
