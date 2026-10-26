using System.Text.Json;

int checks = 0, failures = 0;
CodecMixingChecks.Run(Check);
CodecLoudnessChecks.Run(Check);
TelephoneMixingChecks.Run(Check);
// Reuse only the existing exhaustive G.711 fixtures; skip unrelated glitch scenarios.
AudioEffectsChecks.Run((name, action) =>
{
    if (name.StartsWith("G.711 ") || name.StartsWith("Mu-law ")) Check(name, action);
});
Console.WriteLine($"Codec pipeline: {checks - failures}/{checks} passed.");
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
