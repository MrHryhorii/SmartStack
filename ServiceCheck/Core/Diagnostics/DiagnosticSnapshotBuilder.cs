using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Core.Diagnostics;

/// <summary>
/// Groups sanitized configuration snapshots for one run.
/// </summary>
public sealed record DiagnosticSnapshots(
    ProfileTarget Target,
    DiagnosticProfileSnapshot Profile,
    DiagnosticRequestSnapshot Request);

/// <summary>
/// Builds sanitized snapshots and deterministic configuration fingerprints.
/// </summary>
public static class DiagnosticSnapshotBuilder
{
    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates snapshots from validated, normalized diagnostic settings.
    /// </summary>
    public static DiagnosticSnapshots Create(
        TargetProfile profile,
        Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(redact);

        var normalized = ProfileValidator.ValidateAndNormalize(profile);
        var request = CreateRequestSnapshot(normalized, redact);

        var target = normalized.Target with
        {
            Id = redact(normalized.Target.Id),
            BaseUrl = redact(normalized.Target.BaseUrl)
        };

        var profileSnapshot = new DiagnosticProfileSnapshot
        {
            Name = redact(normalized.Name),
            Fingerprint = HashValue(new
            {
                normalized.SchemaVersion,
                normalized.Target,
                normalized.Routes,
                RequestFingerprint = request.Fingerprint,
                AuthenticationType = normalized.Authentication.Type,
                normalized.TimeoutsMs,
                normalized.Limits
            }),
            Routes = normalized.Routes with
            {
                Health = redact(normalized.Routes.Health),
                HealthFallback = redact(normalized.Routes.HealthFallback),
                Status = redact(normalized.Routes.Status),
                Voices = redact(normalized.Routes.Voices),
                Speech = redact(normalized.Routes.Speech)
            },
            AuthenticationType = normalized.Authentication.Type,
            TimeoutsMs = normalized.TimeoutsMs,
            Limits = normalized.Limits
        };

        return new DiagnosticSnapshots(target, profileSnapshot, request);
    }

    /// <summary>
    /// Identifies the effective speech payload independently of its target.
    /// </summary>
    private static DiagnosticRequestSnapshot CreateRequestSnapshot(
        TargetProfile profile,
        Func<string, string> redact)
    {
        var synthesis = profile.Synthesis;
        var inputLength = synthesis.Input.Length;
        var inputSha256 = HashBytes(Encoding.UTF8.GetBytes(synthesis.Input));

        var fingerprint = HashValue(new
        {
            Route = profile.Routes.Speech,
            synthesis.Model,
            synthesis.Voice,
            synthesis.ResponseFormat,
            synthesis.Speed,
            synthesis.Stream,
            InputLength = inputLength,
            InputSha256 = inputSha256
        });

        return new DiagnosticRequestSnapshot
        {
            Fingerprint = fingerprint,
            Route = redact(profile.Routes.Speech),
            Model = redact(synthesis.Model),
            Voice = redact(synthesis.Voice),
            ResponseFormat = synthesis.ResponseFormat,
            Speed = synthesis.Speed,
            Stream = synthesis.Stream,
            InputLength = inputLength,
            InputSha256 = inputSha256
        };
    }

    /// <summary>
    /// Hashes a fixed-shape value using deterministic JSON serialization.
    /// </summary>
    private static string HashValue<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            value,
            FingerprintOptions);

        return HashBytes(json);
    }

    /// <summary>
    /// Returns a lowercase SHA-256 digest of the supplied bytes.
    /// </summary>
    private static string HashBytes(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}