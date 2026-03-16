namespace ONNX_Runner.Models;

public class HardwareSettings
{
    public double TotalVramGb { get; set; } = 8.0;
    public double VramPerRequestGb { get; set; } = 0.6;
    public double CpuCoresUsageMultiplier { get; set; } = 0.75;
    // Скільки секунд звуку OpenVoice обробляє за 1 раз
    public int OpenVoiceChunkSeconds { get; set; } = 15;
}