using System.Text.Json;

int checks = 0, failures = 0;
FmMixingChecks.Run(Check);
FmVoicePathChecks.Run(Check);
TelephoneMixingChecks.Run(Check);
CodecMixingChecks.Run(Check);
Console.WriteLine($"FM pipeline: {checks - failures}/{checks} passed.");
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
