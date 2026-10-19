using ONNX_Runner.Models;

internal static class ChorusMixingChecks
{
    /// <summary>Checks the current eased Chorus path and its prepared output correction.</summary>
    internal static void Run(Action<string, Action> check) => ModulationCurveChecks.Run(check, VoiceEffectType.Chorus);
}
