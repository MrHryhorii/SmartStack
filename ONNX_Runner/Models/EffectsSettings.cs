namespace ONNX_Runner.Models;

// Перелік усіх доступних ефектів
public enum VoiceEffectType
{
    None,
    Telephone,     // Lo-Fi еквалізація та транзисторний кліппінг
    Overdrive,     // Лампове насичення (Waveshaping)
    Bitcrusher,    // Зниження розрядності та децимація (Ефект 8-біт/Arcade)
    RingModulator, // Кільцева модуляція (Ефект Робота/Далека)
    Flanger,       // Модульована коротка затримка з фідбеком (Металевий космічний звук)
    Chorus         // Модульована довга затримка (Ефект роздвоєння голосу/хору)
}

// Клас для зчитування з appsettings.json
public class EffectsSettings
{
    public bool EnableGlobalEffects { get; set; } = true;
    public string DefaultEffect { get; set; } = "None";
    public float DefaultIntensity { get; set; } = 1.0f;
}