using ONNX_Runner.Models;

internal static class ChorusMixingChecks
{
    /// <summary>Checks the intrinsic Chorus path and measured output correction.</summary>
    internal static void Run(Action<string, Action> check)
    {
        ModulationCurveChecks.Run(check, VoiceEffectType.Chorus);
        ModulationLevelChecks.Run(check, VoiceEffectType.Chorus);
    }
}
