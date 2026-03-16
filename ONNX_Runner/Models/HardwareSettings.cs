namespace ONNX_Runner.Models;

public class HardwareSettings
{
    public double TotalVramGb { get; set; } = 8.0;
    public double VramPerRequestGb { get; set; } = 1.5;
    public double CpuCoresUsageMultiplier { get; set; } = 0.75;
}