bool failuresOnly = args.Contains("--failures-only", StringComparer.OrdinalIgnoreCase);
int total = 0, failures = 0;

CrusherMixingChecks.Run(Check);
CrusherCurveChecks.Run(Check);
BitcrusherQuantizationChecks.Run(Check);
CharacterStrengthChecks.Run(CheckCrusher);
AudioEffectSampleRateChecks.Run(CheckCrusher);

Console.WriteLine($"Bitcrusher checks: {total - failures}/{total} passed.");
return failures == 0 ? 0 : 1;

void CheckCrusher(string name, Action action)
{
    if (name.StartsWith("Bitcrusher", StringComparison.Ordinal)) Check(name, action);
}

void Check(string name, Action action)
{
    total++;
    try
    {
        action();
        if (!failuresOnly) Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}
