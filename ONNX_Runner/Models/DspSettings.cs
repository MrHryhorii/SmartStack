namespace ONNX_Runner.Models;

public class DspSettings
{
    // Чи вмикати фільтр взагалі?
    public bool EnableLowPassFilter { get; set; } = true;

    // Частота зрізу (все, що вище цієї цифри - буде приглушено)
    public float LowPassCutoffFrequency { get; set; } = 7500f;

    // Q-фактор (0.707 - це плавний природний зріз)
    public float LowPassQFactor { get; set; } = 0.707f;
}