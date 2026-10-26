using System.Text.Json;

int checks = 0, failures = 0;
TacticalMixingChecks.Run(Check);
FmMixingChecks.Run(Check);
FmVoicePathChecks.Run(Check);
TelephoneMixingChecks.Run(Check);
CodecMixingChecks.Run(Check);
CharacterStrengthChecks.Run((name, action) =>
{
    if (name.StartsWith("Tactical")) Check(name, action);
});
AudioEffectSampleRateChecks.Run((name, action) =>
{
    if (name.StartsWith("CVSD ") || name.StartsWith("TacticalRadio ") || name.StartsWith("Telephone ")
        || name.StartsWith("G711MuLaw ") || name.StartsWith("G711ALaw ") || name.StartsWith("FmRadio ")) Check(name, action);
});
Console.WriteLine($"Tactical pipeline: {checks - failures}/{checks} passed.");
if (args.Length == 2 && args[0] == "--report")
    File.WriteAllText(args[1], JsonSerializer.Serialize(new { Passed = checks - failures, Failed = failures },
        new JsonSerializerOptions { WriteIndented = true }) + "\n");
Environment.ExitCode = failures == 0 ? 0 : 1;

void Check(string name, Action action)
{
    checks++;
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
