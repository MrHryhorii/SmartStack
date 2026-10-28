bool failuresOnly = args.Contains("--failures-only", StringComparer.OrdinalIgnoreCase);
int total = 0, failures = 0;
CassetteIntensityChecks.Run(Check);
CassettePlaybackChecks.Run(Check);
CassetteVoiceLevelChecks.Run(Check);
CassetteSampleRateChecks.Run(Check);
AudioEffectSampleRateChecks.Run((name, action) =>
{
    if (name.StartsWith("LoFiTape", StringComparison.Ordinal) || name.StartsWith("Cassette", StringComparison.Ordinal)) Check(name, action);
});
Console.WriteLine($"Cassette checks: {total - failures}/{total} passed.");
return failures == 0 ? 0 : 1;

void Check(string name, Action action)
{
    total++;
    try { action(); if (!failuresOnly) Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}
