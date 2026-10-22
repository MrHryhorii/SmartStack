using System.Runtime.CompilerServices;

internal static class AllocationChecks
{
    /// <summary>Checks the test host and verifies that real managed allocations remain observable.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Exact allocation checks run without background GC", () =>
        {
            // Background GC can add phantom counter deltas: dotnet/runtime#134724.
            bool configured = GC.GetConfigurationVariables().TryGetValue("ConcurrentGC", out object? value);
            Require(configured && value is false,
                "Background GC must be disabled in this test host for exact allocation checks.");
        });
        check("Allocation counter detects a real managed allocation", () =>
        {
            GC.KeepAlive(Allocate());
            long before = GC.GetAllocatedBytesForCurrentThread();
            object value = Allocate();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(value);
            Require(allocated > 0, "The allocation counter did not detect the positive control.");
        });
    }

    /// <summary>Keeps the positive control observable across optimized builds.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Allocate() => new byte[37];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
