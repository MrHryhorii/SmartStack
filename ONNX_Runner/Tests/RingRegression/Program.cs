int total = 0, failures = 0;
AllocationChecks.Run(Check);
RingMixingChecks.Run(Check);
Console.WriteLine($"Result: {total - failures}/{total} RingModulator checks passed");
return failures == 0 ? 0 : 1;

void Check(string name, Action action)
{
    total++;
    try { action(); }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {error.Message}");
    }
}
