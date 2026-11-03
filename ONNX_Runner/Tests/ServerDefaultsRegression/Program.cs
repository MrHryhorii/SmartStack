using System.Text.Json;

var run = new TestRun();
ConfigurationChecks.Run(run);
RequestAdapterChecks.Run(run.Check);
await StreamingJsonChecks.Run(run);
if (args.Contains("--synthesis")) await SynthesisOverrideChecks.Run(run);
int httpIndex = Array.IndexOf(args, "--http");
if (httpIndex >= 0) await HttpOverrideChecks.Run(run, Path.GetFullPath(args[httpIndex + 1]));
int reportIndex = Array.IndexOf(args, "--report");
if (reportIndex >= 0) File.WriteAllText(args[reportIndex + 1], JsonSerializer.Serialize(new
{
    run.Checks, run.Failures, run.Evidence, Runtime = Environment.Version.ToString()
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { run.Checks, run.Failures }));
return run.Failures == 0 ? 0 : 1;

internal sealed class TestRun
{
    internal int Checks { get; private set; }
    internal int Failures { get; private set; }
    internal List<object> Evidence { get; } = [];

    internal void Check(string name, Action body)
    {
        Checks++;
        try { body(); }
        catch (Exception error) { Failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }

    internal async Task CheckAsync(string name, Func<Task> body)
    {
        Checks++;
        try { await body(); }
        catch (Exception error) { Failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
