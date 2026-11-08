using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Defines regression checks for complete profile validation.</summary>
internal static class ProfileValidationChecks
{
    private const string BaseUrl = "https://tts.example.test/speech";
    private const string NormalizedBaseUrl = "https://tts.example.test/speech/";

    private static readonly (
        string Name,
        Func<TargetProfile, TargetProfile> Change)[] ValidProfiles =
    [
        ("Default profile accepted", p => p),

        ("Minimum speed accepted", p => p with
        {
            Synthesis = p.Synthesis with { Speed = 0.25 }
        }),

        ("Maximum speed accepted", p => p with
        {
            Synthesis = p.Synthesis with { Speed = 4.0 }
        }),

        ("Shorter run deadline accepted", p => p with
        {
            TimeoutsMs = p.TimeoutsMs with { Run = 1 }
        }),

        ("Minimum timeouts accepted", p => p with
        {
            TimeoutsMs = new ProfileTimeouts
            {
                Dns = 1,
                Tcp = 1,
                Metadata = 1,
                Speech = 1,
                Run = 1
            }
        }),

        ("Maximum timeouts accepted", p => p with
        {
            TimeoutsMs = new ProfileTimeouts
            {
                Dns = 900_000,
                Tcp = 900_000,
                Metadata = 900_000,
                Speech = 900_000,
                Run = 900_000
            }
        }),

        ("Input at configured cap accepted", p => p with
        {
            Synthesis = p.Synthesis with { Input = new string('x', 200) }
        }),

        ("Multiline speech input accepted", p => p with
        {
            Synthesis = p.Synthesis with { Input = "First line.\nSecond line." }
        }),

        ("Minimum limits accepted", p => p with
        {
            Limits = new ProfileLimits
            {
                MaxMetadataBytes = 1,
                MaxAudioBytes = 1,
                MaxInputCharacters = 1
            },
            Synthesis = p.Synthesis with { Input = "A" }
        }),

        ("Maximum limits accepted", p => p with
        {
            Limits = new ProfileLimits
            {
                MaxMetadataBytes = 4 * 1024 * 1024,
                MaxAudioBytes = 64 * 1024 * 1024,
                MaxInputCharacters = 10_000
            }
        }),

        ("Bearer configuration accepted", p => p with
        {
            Authentication = new ProfileAuthentication
            {
                Type = "bearer",
                TokenEnvironmentVariable = "TSUBAKI_CHECK_TEST_TOKEN"
            }
        }),

        ("Disabled audio retention preserved", p => p with
        {
            Output = p.Output with { RetainAudio = false }
        })
    ];

    private static readonly (
        string Name,
        Func<TargetProfile, TargetProfile> Change)[] InvalidProfiles =
    [
        ("Unsupported profile schema rejected", p => p with
        {
            SchemaVersion = 2
        }),

        ("Blank profile name rejected", p => p with
        {
            Name = " "
        }),

        ("Control character in label rejected", p => p with
        {
            Name = "Fixture\nName"
        }),

        ("Blank target ID rejected", p => p with
        {
            Target = p.Target with { Id = "" }
        }),

        ("Invalid profile base URL rejected", p => p with
        {
            Target = p.Target with { BaseUrl = "not-a-url" }
        }),

        ("Blank model rejected", p => p with
        {
            Synthesis = p.Synthesis with { Model = "" }
        }),

        ("Blank voice rejected", p => p with
        {
            Synthesis = p.Synthesis with { Voice = " " }
        }),

        ("Unsupported response format rejected", p => p with
        {
            Synthesis = p.Synthesis with { ResponseFormat = "mp3" }
        }),

        ("Streaming synthesis rejected", p => p with
        {
            Synthesis = p.Synthesis with { Stream = true }
        }),

        ("Empty speech input rejected", p => p with
        {
            Synthesis = p.Synthesis with { Input = "" }
        }),

        ("Whitespace speech input rejected", p => p with
        {
            Synthesis = p.Synthesis with { Input = " \n\t" }
        }),

        ("Input above configured cap rejected", p => p with
        {
            Synthesis = p.Synthesis with { Input = new string('x', 201) }
        }),

        ("Unknown authentication type rejected", p => p with
        {
            Authentication = new ProfileAuthentication { Type = "basic" }
        }),

        ("Unused token reference rejected", p => p with
        {
            Authentication = new ProfileAuthentication
            {
                Type = "none",
                TokenEnvironmentVariable = "TSUBAKI_CHECK_TEST_TOKEN"
            }
        }),

        ("Missing bearer reference rejected", p => p with
        {
            Authentication = new ProfileAuthentication { Type = "bearer" }
        }),

        ("Malformed bearer reference rejected", p => p with
        {
            Authentication = new ProfileAuthentication
            {
                Type = "bearer",
                TokenEnvironmentVariable = "INVALID=NAME"
            }
        }),

        ("Control character in bearer reference rejected", p => p with
        {
            Authentication = new ProfileAuthentication
            {
                Type = "bearer",
                TokenEnvironmentVariable = "INVALID\nNAME"
            }
        })
    ];

    private static readonly (
        string Name,
        Func<ProfileTimeouts, int, ProfileTimeouts> Set)[] TimeoutFields =
    [
        ("timeoutsMs.dns", (p, v) => p with { Dns = v }),
        ("timeoutsMs.tcp", (p, v) => p with { Tcp = v }),
        ("timeoutsMs.metadata", (p, v) => p with { Metadata = v }),
        ("timeoutsMs.speech", (p, v) => p with { Speech = v }),
        ("timeoutsMs.run", (p, v) => p with { Run = v })
    ];

    private static readonly (
        string Name,
        int Maximum,
        Func<ProfileLimits, int, ProfileLimits> Set)[] LimitFields =
    [
        ("limits.maxMetadataBytes", 4 * 1024 * 1024,
            (p, v) => p with { MaxMetadataBytes = v }),

        ("limits.maxAudioBytes", 64 * 1024 * 1024,
            (p, v) => p with { MaxAudioBytes = v }),

        ("limits.maxInputCharacters", 10_000,
            (p, v) => p with { MaxInputCharacters = v })
    ];

    private static readonly (
        string Name,
        Func<ProfileRoutes, string, ProfileRoutes> Set)[] RouteFields =
    [
        ("routes.health", (p, v) => p with { Health = v }),
        ("routes.healthFallback", (p, v) => p with { HealthFallback = v }),
        ("routes.status", (p, v) => p with { Status = v }),
        ("routes.voices", (p, v) => p with { Voices = v }),
        ("routes.speech", (p, v) => p with { Speech = v })
    ];

    private static readonly (string Name, double Value)[] InvalidSpeeds =
    [
        ("Low speed rejected", 0.24),
        ("High speed rejected", 4.01),
        ("NaN speed rejected", double.NaN),
        ("Positive infinity speed rejected", double.PositiveInfinity),
        ("Negative infinity speed rejected", double.NegativeInfinity)
    ];

    /// <summary>Registers validation checks for the shared runner.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks() =>
    [
        .. ValidProfiles.Select(item => Valid(item.Name, item.Change)),
        .. InvalidProfiles.Select(item => Invalid(item.Name, item.Change)),

        .. TimeoutFields.SelectMany(field => new[]
        {
            Invalid($"Zero {field.Name} rejected", p => p with
            {
                TimeoutsMs = field.Set(p.TimeoutsMs, 0)
            }),

            Invalid($"Excessive {field.Name} rejected", p => p with
            {
                TimeoutsMs = field.Set(p.TimeoutsMs, 900_001)
            })
        }),

        .. LimitFields.SelectMany(field => new[]
        {
            Invalid($"Zero {field.Name} rejected", p => p with
            {
                Limits = field.Set(p.Limits, 0)
            }),

            Invalid($"Excessive {field.Name} rejected", p => p with
            {
                Limits = field.Set(p.Limits, field.Maximum + 1)
            })
        }),

        .. RouteFields.Select(field => Invalid(
            $"Invalid {field.Name} rejected", p => p with
            {
                Routes = field.Set(p.Routes, "/invalid")
            })),

        .. InvalidSpeeds.Select(item => Invalid(item.Name, p => p with
        {
            Synthesis = p.Synthesis with { Speed = item.Value }
        }))
    ];

    /// <summary>Creates a valid profile with an unnormalized service root.</summary>
    private static TargetProfile CreateProfile() => new()
    {
        SchemaVersion = 1,
        Name = "Validation fixture",
        Target = new ProfileTarget
        {
            Id = "validation-fixture",
            BaseUrl = BaseUrl
        }
    };

    /// <summary>Registers a profile that must be accepted.</summary>
    private static (string Name, Func<Task> Run) Valid(
        string name,
        Func<TargetProfile, TargetProfile> change)
    {
        return Check(name, () => VerifyValidProfile(change(CreateProfile())));
    }

    /// <summary>Registers a profile that must be rejected.</summary>
    private static (string Name, Func<Task> Run) Invalid(
        string name,
        Func<TargetProfile, TargetProfile> change)
    {
        return Check(name, () =>
        {
            try
            {
                ProfileValidator.ValidateAndNormalize(change(CreateProfile()));
            }
            catch (ArgumentException)
            {
                return;
            }

            throw new InvalidOperationException(
                "Expected ArgumentException was not thrown.");
        });
    }

    /// <summary>Adapts a synchronous assertion to the asynchronous runner.</summary>
    private static (string Name, Func<Task> Run) Check(
        string name,
        Action action)
    {
        return (name, () =>
        {
            action();
            return Task.CompletedTask;
        }
        );
    }

    /// <summary>Verifies normalization without changing other profile values.</summary>
    private static void VerifyValidProfile(TargetProfile profile)
    {
        var expected = profile with
        {
            Target = profile.Target with { BaseUrl = NormalizedBaseUrl }
        };

        var actual = ProfileValidator.ValidateAndNormalize(profile);

        if (profile.Target.BaseUrl != BaseUrl)
            throw new InvalidOperationException(
                "Validation modified the original profile.");

        if (actual != expected)
            throw new InvalidOperationException(
                "Validation changed unexpected profile values.");
    }
}