namespace Tsubaki.ServiceCheck.Core.Profiles;

/// <summary>Validates profile settings and normalizes the service root.</summary>
public static class ProfileValidator
{
    private const int MaxTimeoutMs = 900_000;
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private const int MaxAudioBytes = 64 * 1024 * 1024;
    private const int MaxInputCharacters = 10_000;

    /// <summary>Returns a normalized profile or rejects invalid settings.</summary>
    public static TargetProfile ValidateAndNormalize(TargetProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.Target);
        ArgumentNullException.ThrowIfNull(profile.Output);

        if (profile.SchemaVersion != 1)
            throw new ArgumentException("schemaVersion must be 1.");

        RequireLabel(profile.Name, "name");
        RequireLabel(profile.Target.Id, "target.id");

        var root = ProfileUrlRules.NormalizeBaseUrl(profile.Target.BaseUrl);

        ValidateRoutes(profile.Routes, root.AbsoluteUri);
        ValidateTimeouts(profile.TimeoutsMs);
        ValidateLimits(profile.Limits);
        ValidateSynthesis(profile.Synthesis, profile.Limits.MaxInputCharacters);
        ValidateAuthentication(profile.Authentication);

        return profile with
        {
            Target = profile.Target with
            {
                BaseUrl = root.AbsoluteUri
            }
        };
    }

    /// <summary>Validates all configured routes against the service root.</summary>
    private static void ValidateRoutes(ProfileRoutes routes, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(routes);

        (string Field, string Value)[] values =
        [
            ("routes.health", routes.Health),
            ("routes.healthFallback", routes.HealthFallback),
            ("routes.status", routes.Status),
            ("routes.voices", routes.Voices),
            ("routes.speech", routes.Speech)
        ];

        foreach (var (field, value) in values)
        {
            try
            {
                ProfileUrlRules.Resolve(baseUrl, value);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    $"{field}: {exception.Message}",
                    exception);
            }
        }
    }

    /// <summary>Validates independent operation and run deadlines.</summary>
    private static void ValidateTimeouts(ProfileTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);

        RequireRange(timeouts.Dns, MaxTimeoutMs, "timeoutsMs.dns");
        RequireRange(timeouts.Tcp, MaxTimeoutMs, "timeoutsMs.tcp");
        RequireRange(timeouts.Metadata, MaxTimeoutMs, "timeoutsMs.metadata");
        RequireRange(timeouts.Speech, MaxTimeoutMs, "timeoutsMs.speech");
        RequireRange(timeouts.Run, MaxTimeoutMs, "timeoutsMs.run");
    }

    /// <summary>Validates response size and input length limits.</summary>
    private static void ValidateLimits(ProfileLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        RequireRange(
            limits.MaxMetadataBytes,
            MaxMetadataBytes,
            "limits.maxMetadataBytes");

        RequireRange(
            limits.MaxAudioBytes,
            MaxAudioBytes,
            "limits.maxAudioBytes");

        RequireRange(
            limits.MaxInputCharacters,
            MaxInputCharacters,
            "limits.maxInputCharacters");
    }

    /// <summary>Validates the supported diagnostic speech settings.</summary>
    private static void ValidateSynthesis(
        ProfileSynthesis synthesis,
        int maxInputCharacters)
    {
        ArgumentNullException.ThrowIfNull(synthesis);

        RequireLabel(synthesis.Model, "synthesis.model");
        RequireLabel(synthesis.Voice, "synthesis.voice");

        if (synthesis.ResponseFormat != "wav")
            throw new ArgumentException(
                "synthesis.responseFormat must be 'wav'.");

        if (synthesis.Stream)
            throw new ArgumentException(
                "synthesis.stream must be false.");

        if (!double.IsFinite(synthesis.Speed)
            || synthesis.Speed is < 0.25 or > 4.0)
        {
            throw new ArgumentException(
                "synthesis.speed must be a finite number between 0.25 and 4.0.");
        }

        if (string.IsNullOrWhiteSpace(synthesis.Input))
            throw new ArgumentException(
                "synthesis.input must not be blank.");

        if (synthesis.Input.Length > maxInputCharacters)
            throw new ArgumentException(
                "synthesis.input exceeds limits.maxInputCharacters.");
    }

    /// <summary>Validates authentication configuration without reading secrets.</summary>
    private static void ValidateAuthentication(
        ProfileAuthentication authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);

        if (authentication.Type is not ("none" or "bearer"))
            throw new ArgumentException(
                "authentication.type must be 'none' or 'bearer'.");

        if (authentication.Type == "none")
        {
            if (authentication.TokenEnvironmentVariable is not null)
                throw new ArgumentException(
                    "authentication.tokenEnvironmentVariable requires bearer authentication.");

            return;
        }

        var variableName = authentication.TokenEnvironmentVariable;

        if (string.IsNullOrWhiteSpace(variableName)
            || variableName.Any(char.IsControl)
            || variableName.Contains('='))
        {
            throw new ArgumentException(
                "authentication.tokenEnvironmentVariable must name an environment variable.");
        }
    }

    /// <summary>Requires a nonblank label without control characters.</summary>
    private static void RequireLabel(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException(
                $"{field} must be nonblank and must not contain control characters.");
    }

    /// <summary>Requires a positive integer within the configured ceiling.</summary>
    private static void RequireRange(int value, int maximum, string field)
    {
        if (value < 1 || value > maximum)
            throw new ArgumentException(
                $"{field} must be between 1 and {maximum}.");
    }
}