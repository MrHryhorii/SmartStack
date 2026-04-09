namespace ONNX_Runner.Models;

// Перелік усіх доступних ефектів
public enum VoiceEffectType
{
    None,
    VintageRadio,
    Telephone,
    VinylRecord,
    Megaphone,
    Robot,
    Overdrive,
    Alien,
    Whisper,
    Arcade
}

// Клас для зчитування з appsettings.json
public class EffectsSettings
{
    public bool EnableGlobalEffects { get; set; } = true;
    public string DefaultEffect { get; set; } = "None";
    public float DefaultIntensity { get; set; } = 1.0f;
}