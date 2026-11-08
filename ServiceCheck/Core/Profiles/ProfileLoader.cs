using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tsubaki.ServiceCheck.Core.Profiles;

/// <summary>Loads target profiles from bounded UTF-8 JSON files.</summary>
public static class ProfileLoader
{
    private const int MaxProfileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        MaxDepth = 16
    };

    /// <summary>Reads a bounded JSON file into a target profile.</summary>
    public static async Task<TargetProfile> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[MaxProfileBytes + 1];
        var length = 0;

        while (length < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(
                buffer.AsMemory(length),
                cancellationToken);

            if (bytesRead == 0)
                break;

            length += bytesRead;
        }

        if (length > MaxProfileBytes)
            throw new JsonException(
                $"Profile exceeds the maximum size of {MaxProfileBytes} bytes.");

        cancellationToken.ThrowIfCancellationRequested();

        return Parse(buffer.AsSpan(0, length));
    }

    /// <summary>Parses UTF-8 JSON and verifies the profile schema version.</summary>
    private static TargetProfile Parse(ReadOnlySpan<byte> json)
    {
        if (json.StartsWith("\uFEFF"u8))
            json = json[3..];

        var profile = JsonSerializer.Deserialize<TargetProfile>(
            json,
            JsonOptions)
            ?? throw new JsonException("The profile must be a JSON object.");

        if (profile.SchemaVersion != 1)
            throw new JsonException(
                $"Unsupported schemaVersion {profile.SchemaVersion}. Expected 1.");

        return ProfileValidator.ValidateAndNormalize(profile);
    }
}