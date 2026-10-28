using ONNX_Runner.Models;

internal static class FlangerMixingChecks
{
    /// <summary>Checks the intrinsic Flanger path and measured output correction.</summary>
    internal static void Run(Action<string, Action> check)
    {
        ModulationCurveChecks.Run(check, VoiceEffectType.Flanger);
        ModulationLevelChecks.Run(check, VoiceEffectType.Flanger);
    }
}
