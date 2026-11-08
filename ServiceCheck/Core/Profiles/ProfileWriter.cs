using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tsubaki.ServiceCheck.Core.Profiles;

/// <summary>Writes example target profiles as JSON.</summary>
public static class ProfileWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>Writes a local example without overwriting an existing file.</summary>
    public static async Task WriteExampleAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var profile = CreateLocalProfile();
        var json = JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions);

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);

        await stream.WriteAsync(json, cancellationToken);
    }

    /// <summary>Builds the local example using default diagnostic settings.</summary>
    private static TargetProfile CreateLocalProfile() => new()
    {
        SchemaVersion = 1,
        Name = "Tsubaki local",
        Target = new ProfileTarget
        {
            Id = "tsubaki-local",
            BaseUrl = "http://127.0.0.1:5045/"
        }
    };
}