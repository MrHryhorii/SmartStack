using System.Text;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Regression;

internal static class Program
{
    private const int ProfileSizeLimit = 64 * 1024;

    private const string ValidProfileJson = """
        {"schemaVersion":1,"name":"Fixture","target":{"id":"fixture","baseUrl":"http://127.0.0.1:5045/"}}
        """;

    /// <summary>Runs regression checks in an isolated temporary directory.</summary>
    private static async Task<int> Main()
    {
        var directory = Directory.CreateTempSubdirectory(
            "TsubakiCheck-Regression-").FullName;

        try
        {
            return await RunChecksAsync(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Executes all registered checks and reports their results.</summary>
    private static async Task<int> RunChecksAsync(string directory)
    {
        (string Name, Func<Task> Run)[] checks =
        [
            ("Written profile loads",
                () => VerifyGeneratedProfileAsync(directory)),

            ("Loaded service root normalized",
                () => VerifyLoadedServiceRootAsync(directory)),

            ("Invalid loaded settings rejected",
                () => VerifyLoadedSettingsRejectedAsync(directory)),

            ("UTF-8 BOM accepted",
                () => VerifyValidProfileAsync(
                    directory, "\uFEFF" + ValidProfileJson)),

            ("Exact size limit accepted",
                () => VerifyValidProfileAsync(
                    directory, ValidProfileJson.PadRight(ProfileSizeLimit))),

            ("Oversized profile rejected",
                () => VerifyRejectedProfileAsync(
                    directory, ValidProfileJson.PadRight(ProfileSizeLimit + 1))),

            ("Malformed JSON rejected",
                () => VerifyRejectedProfileAsync(directory, "{")),

            ("Missing required fields rejected",
                () => VerifyRejectedProfileAsync(directory, "{}")),

            ("Null root rejected",
                () => VerifyRejectedProfileAsync(directory, "null")),

            ("Unsupported schema rejected",
                () => VerifyRejectedProfileAsync(
                    directory,
                    ValidProfileJson.Replace(
                        "\"schemaVersion\":1", "\"schemaVersion\":2"))),

            ("Unknown root property rejected",
                () => VerifyRejectedProfileAsync(
                    directory,
                    ValidProfileJson.Insert(1, "\"unexpected\":true,"))),

            ("Unknown nested property rejected",
                () => VerifyRejectedProfileAsync(
                    directory,
                    ValidProfileJson.Replace(
                        "\"baseUrl\":", "\"unexpected\":true,\"baseUrl\":"))),

            ("Duplicate property rejected",
                () => VerifyRejectedProfileAsync(
                    directory,
                    ValidProfileJson.Insert(1, "\"schemaVersion\":1,"))),

            ("Null required string rejected",
                () => VerifyRejectedProfileAsync(
                    directory,
                    ValidProfileJson.Replace(
                        "\"name\":\"Fixture\"", "\"name\":null"))),

            ("Null settings object rejected",
                () => VerifyRejectedProfileAsync(
                    directory,
                    ValidProfileJson.Insert(1, "\"synthesis\":null,"))),

            ("Cancellation honored",
                () => VerifyCancellationAsync(directory)),

            ("Missing file reported",
                () => ExpectExceptionAsync<FileNotFoundException>(
                    () => ProfileLoader.LoadAsync(
                        Path.Combine(directory, "missing.json")))),

            ("Existing file preserved",
                () => VerifyExistingFileAsync(directory)),

            .. ProfileUrlChecks.CreateChecks(),
            .. ProfileValidationChecks.CreateChecks(),
            .. HttpClientChecks.CreateChecks(),
            .. WavInspectorChecks.CreateChecks()
        ];

        var failures = 0;

        foreach (var (name, run) in checks)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;

                Console.Error.WriteLine(
                    $"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine(
            $"Checks: {checks.Length}; passed: {checks.Length - failures}; failed: {failures}.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>Verifies that the generated example can be loaded.</summary>
    private static async Task VerifyGeneratedProfileAsync(string directory)
    {
        var path = Path.Combine(directory, "generated.json");

        await ProfileWriter.WriteExampleAsync(path);
        var profile = await ProfileLoader.LoadAsync(path);

        if (profile.Target.Id != "tsubaki-local"
            || profile.Target.BaseUrl != "http://127.0.0.1:5045/"
            || profile.Synthesis.Voice != "piper_base"
            || profile.Synthesis.ResponseFormat != "wav")
        {
            throw new InvalidOperationException(
                "The generated profile contains unexpected values.");
        }
    }

    /// <summary>Verifies that valid JSON retains its values and defaults.</summary>
    private static async Task VerifyValidProfileAsync(
        string directory,
        string json)
    {
        var path = await WriteFixtureAsync(directory, json);
        var profile = await ProfileLoader.LoadAsync(path);

        if (profile.Name != "Fixture"
            || profile.Target.Id != "fixture"
            || profile.Synthesis.Voice != "piper_base")
        {
            throw new InvalidOperationException(
                "Loaded values or effective defaults are incorrect.");
        }
    }

    /// <summary>Verifies that invalid profile JSON is rejected.</summary>
    private static async Task VerifyRejectedProfileAsync(
        string directory,
        string json)
    {
        var path = await WriteFixtureAsync(directory, json);

        await ExpectExceptionAsync<JsonException>(
            () => ProfileLoader.LoadAsync(path));
    }

    /// <summary>Verifies that a cancelled token prevents loading.</summary>
    private static async Task VerifyCancellationAsync(string directory)
    {
        var path = await WriteFixtureAsync(directory, ValidProfileJson);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await ExpectExceptionAsync<OperationCanceledException>(
            () => ProfileLoader.LoadAsync(path, cancellation.Token));
    }

    /// <summary>Verifies that example generation preserves existing files.</summary>
    private static async Task VerifyExistingFileAsync(string directory)
    {
        const string original = "Existing profile content.";
        var path = Path.Combine(directory, "existing.json");

        await File.WriteAllTextAsync(path, original);

        await ExpectExceptionAsync<IOException>(
            () => ProfileWriter.WriteExampleAsync(path));

        var actual = await File.ReadAllTextAsync(path);

        if (actual != original)
            throw new InvalidOperationException(
                "Example generation modified an existing file.");
    }

    /// <summary>Writes a UTF-8 fixture without an implicit BOM.</summary>
    private static async Task<string> WriteFixtureAsync(
        string directory,
        string json)
    {
        var path = Path.Combine(directory, "fixture.json");

        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(json));

        return path;
    }

    /// <summary>Requires an operation to throw the expected exception type.</summary>
    private static async Task ExpectExceptionAsync<TException>(
        Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} was not thrown.");
    }

    /// <summary>Verifies that loading normalizes the configured proxy prefix.</summary>
    private static async Task VerifyLoadedServiceRootAsync(string directory)
    {
        var json = ValidProfileJson.Replace(
            "http://127.0.0.1:5045/",
            "https://tts.example.test/speech",
            StringComparison.Ordinal);

        var path = await WriteFixtureAsync(directory, json);
        var profile = await ProfileLoader.LoadAsync(path);

        if (profile.Target.BaseUrl != "https://tts.example.test/speech/")
            throw new InvalidOperationException(
                "Loading did not normalize the configured service root.");
    }

    /// <summary>Verifies that loading rejects valid JSON with invalid settings.</summary>
    private static async Task VerifyLoadedSettingsRejectedAsync(string directory)
    {
        var json = ValidProfileJson.Insert(
            1,
            "\"synthesis\":{\"speed\":0},");

        var path = await WriteFixtureAsync(directory, json);

        await ExpectExceptionAsync<ArgumentException>(
            () => ProfileLoader.LoadAsync(path));
    }
}