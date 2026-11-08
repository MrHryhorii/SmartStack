using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>
/// Verifies result aggregation and safe configuration snapshots.
/// </summary>
internal static class DiagnosticResultChecks
{
    /// <summary>Returns result-model regression checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            Check("Required failure and interruption priorities verified", VerifyPriorities),
            Check("Missing or duplicate required evidence rejected", VerifyRequiredEvidence),
            Check("Supporting gaps and deliberate skips distinguished", VerifySupportingCoverage),
            Check("Presentation changes preserve profile fingerprints", VerifyPresentationChanges),
            Check("Target settings preserve the request fingerprint", VerifyTargetChanges),
            Check("Speech settings change both fingerprints", VerifySpeechChanges),
            Check("Snapshot exports redact secrets and omit input text", VerifySafeSnapshots)
        ];
    }

    /// <summary>Preserves failure priority while treating interruptions as incomplete.</summary>
    private static void VerifyPriorities()
    {
        var checks = CreateRequiredChecks();

        RequireOutcome(DiagnosticOutcome.Passed, checks);

        checks[0] = checks[0] with
        {
            Status = DiagnosticCheckStatus.Failed
        };

        RequireOutcome(
            DiagnosticOutcome.Failed,
            [
                .. checks,
                CreateCheck(
                    "service.health",
                    DiagnosticCheckRole.Supporting,
                    DiagnosticCheckStatus.Error)
            ]);

        DiagnosticExecutionState[] interruptedStates =
        [
            DiagnosticExecutionState.Cancelled,
            DiagnosticExecutionState.TimedOut,
            DiagnosticExecutionState.Error
        ];

        foreach (var state in interruptedStates)
        {
            RequireOutcome(DiagnosticOutcome.Incomplete, checks, state);
        }
    }

    /// <summary>Rejects missing, duplicated, or unverified required observations.</summary>
    private static void VerifyRequiredEvidence()
    {
        var checks = CreateRequiredChecks();

        RequireOutcome(DiagnosticOutcome.Incomplete, []);
        RequireOutcome(DiagnosticOutcome.Incomplete, checks[1..]);
        RequireOutcome(DiagnosticOutcome.Incomplete, [.. checks, checks[0]]);

        DiagnosticCheckStatus[] unverifiedStatuses =
        [
            DiagnosticCheckStatus.Error,
            DiagnosticCheckStatus.Skipped,
            DiagnosticCheckStatus.Warning
        ];

        foreach (var status in unverifiedStatuses)
        {
            RequireOutcome(
                DiagnosticOutcome.Incomplete,
                [
                    checks[0],
                    checks[1],
                    checks[2] with { Status = status }
                ]);
        }
    }

    /// <summary>Warns about supporting gaps while allowing deliberate omissions.</summary>
    private static void VerifySupportingCoverage()
    {
        var checks = CreateRequiredChecks();

        DiagnosticCheckStatus[] warningStatuses =
        [
            DiagnosticCheckStatus.Warning,
            DiagnosticCheckStatus.Failed,
            DiagnosticCheckStatus.Error,
            DiagnosticCheckStatus.Skipped
        ];

        foreach (var status in warningStatuses)
        {
            RequireOutcome(
                DiagnosticOutcome.PassedWithWarnings,
                [
                    .. checks,
                    CreateCheck(
                        "service.health",
                        DiagnosticCheckRole.Supporting,
                        status,
                        "coverage_unavailable")
                ]);
        }

        RequireOutcome(
            DiagnosticOutcome.Passed,
            [
                .. checks,
                CreateCheck(
                    "dns.resolve",
                    DiagnosticCheckRole.Supporting,
                    DiagnosticCheckStatus.Skipped,
                    "ip_literal")
            ]);

        RequireOutcome(
            DiagnosticOutcome.Passed,
            [
                CreateCheck("service.identity"),
                CreateCheck("service.readiness"),
                CreateCheck(
                    "speech.request",
                    DiagnosticCheckRole.Supporting,
                    DiagnosticCheckStatus.Skipped,
                    "not_requested"),
                CreateCheck(
                    "audio.container",
                    DiagnosticCheckRole.Supporting,
                    DiagnosticCheckStatus.Skipped,
                    "not_requested"),
                CreateCheck(
                    "audio.signal",
                    DiagnosticCheckRole.Supporting,
                    DiagnosticCheckStatus.Skipped,
                    "not_requested")
            ],
            mode: DiagnosticMode.Inspect);
    }

    /// <summary>Keeps diagnostic identity stable across presentation-only changes.</summary>
    private static void VerifyPresentationChanges()
    {
        var profile = CreateProfile();
        var before = CreateSnapshots(profile);

        var after = CreateSnapshots(profile with
        {
            Name = "Renamed fixture",
            Target = profile.Target with
            {
                BaseUrl = "http://127.0.0.1:5045"
            },
            Output = profile.Output with
            {
                RetainAudio = false
            }
        });

        Require(
            before.Profile.Fingerprint == after.Profile.Fingerprint &&
            before.Request.Fingerprint == after.Request.Fingerprint,
            "Display, retention, or equivalent URL changes altered diagnostic identity.");
    }

    /// <summary>Distinguishes target configuration from the speech payload.</summary>
    private static void VerifyTargetChanges()
    {
        var profile = CreateProfile();
        var before = CreateSnapshots(profile);

        TargetProfile[] alternatives =
        [
            profile with
            {
                Target = profile.Target with
                {
                    BaseUrl = "https://tts.example.test/proxy/"
                }
            },
            profile with
            {
                Target = profile.Target with { Id = "another-fixture" }
            },
            profile with
            {
                Routes = profile.Routes with { Health = "ready" }
            },
            profile with
            {
                TimeoutsMs = profile.TimeoutsMs with { Speech = 61000 }
            },
            profile with
            {
                Limits = profile.Limits with { MaxAudioBytes = 10485761 }
            },
            profile with
            {
                Authentication = new ProfileAuthentication
                {
                    Type = "bearer",
                    TokenEnvironmentVariable = "TSUBAKI_CHECK_FIXTURE_TOKEN"
                }
            }
        ];

        foreach (var alternative in alternatives)
        {
            var after = CreateSnapshots(alternative);

            Require(
                before.Profile.Fingerprint != after.Profile.Fingerprint,
                "Changed diagnostic settings did not alter the profile fingerprint.");

            Require(
                before.Request.Fingerprint == after.Request.Fingerprint,
                "Target settings incorrectly altered the speech request fingerprint.");
        }
    }

    /// <summary>Tracks effective speech changes in both diagnostic fingerprints.</summary>
    private static void VerifySpeechChanges()
    {
        var profile = CreateProfile();
        var before = CreateSnapshots(profile);

        TargetProfile[] alternatives =
        [
            profile with
            {
                Routes = profile.Routes with { Speech = "speech" }
            },
            profile with
            {
                Synthesis = profile.Synthesis with { Model = "fixture-model" }
            },
            profile with
            {
                Synthesis = profile.Synthesis with { Voice = "fixture-voice" }
            },
            profile with
            {
                Synthesis = profile.Synthesis with { Speed = 1.25 }
            },
            profile with
            {
                Synthesis = profile.Synthesis with
                {
                    Input = "Another diagnostic fixture sentence."
                }
            }
        ];

        foreach (var alternative in alternatives)
        {
            var after = CreateSnapshots(alternative);

            Require(
                before.Request.Fingerprint != after.Request.Fingerprint &&
                before.Profile.Fingerprint != after.Profile.Fingerprint,
                "Changed speech settings did not alter both fingerprints.");
        }
    }

    /// <summary>Exports sanitized labels and input identity without original text.</summary>
    private static void VerifySafeSnapshots()
    {
        const string secret = "FixtureSecret_84Q";
        const string input = "Private fixture speech input.";

        var profile = CreateProfile() with
        {
            Name = $"Fixture {secret}",
            Target = new ProfileTarget
            {
                Id = $"fixture-{secret}",
                BaseUrl = $"http://127.0.0.1:5045/{secret}/"
            },
            Routes = new ProfileRoutes
            {
                Health = $"health/{secret}",
                HealthFallback = $"v1/health/{secret}",
                Status = $"tsbk/status/{secret}",
                Voices = $"tsbk/voices/{secret}",
                Speech = $"v1/speech/{secret}"
            },
            Synthesis = new ProfileSynthesis
            {
                Model = $"model-{secret}",
                Voice = $"voice-{secret}",
                Input = input
            }
        };

        var original = CreateSnapshots(profile);

        var sanitized = DiagnosticSnapshotBuilder.Create(
            profile,
            value => value.Replace(secret, "***", StringComparison.Ordinal));

        var json = JsonSerializer.Serialize(sanitized);

        Require(
            !json.Contains(secret, StringComparison.Ordinal),
            "A configured secret remained in exported snapshot values.");

        Require(
            !json.Contains(input, StringComparison.Ordinal),
            "Original speech input remained in exported snapshots.");

        Require(
            sanitized.Request.InputLength == input.Length &&
            sanitized.Request.InputSha256.Length == 64,
            "Input identity was not preserved.");

        Require(
            original.Profile.Fingerprint == sanitized.Profile.Fingerprint &&
            original.Request.Fingerprint == sanitized.Request.Fingerprint,
            "Display redaction altered diagnostic fingerprints.");
    }

    /// <summary>Creates the required observations for an active check.</summary>
    private static DiagnosticCheckResult[] CreateRequiredChecks()
    {
        return
        [
            CreateCheck("speech.request"),
            CreateCheck("audio.container"),
            CreateCheck("audio.signal")
        ];
    }

    /// <summary>Creates one controlled diagnostic observation.</summary>
    private static DiagnosticCheckResult CreateCheck(
        string id,
        DiagnosticCheckRole role = DiagnosticCheckRole.Required,
        DiagnosticCheckStatus status = DiagnosticCheckStatus.Passed,
        string code = "fixture_observation")
    {
        return new DiagnosticCheckResult
        {
            Id = id,
            Role = role,
            Status = status,
            Code = code,
            Summary = "Fixture observation.",
            ObservedAtUtc = DateTimeOffset.UnixEpoch
        };
    }

    /// <summary>Creates a valid profile without reading files or credentials.</summary>
    private static TargetProfile CreateProfile()
    {
        return new TargetProfile
        {
            SchemaVersion = 1,
            Name = "Result fixture",
            Target = new ProfileTarget
            {
                Id = "fixture",
                BaseUrl = "http://127.0.0.1:5045/"
            }
        };
    }

    /// <summary>Creates snapshots with unchanged display values.</summary>
    private static DiagnosticSnapshots CreateSnapshots(TargetProfile profile)
    {
        return DiagnosticSnapshotBuilder.Create(profile, static value => value);
    }

    /// <summary>Requires the expected aggregate outcome.</summary>
    private static void RequireOutcome(
        DiagnosticOutcome expected,
        IReadOnlyList<DiagnosticCheckResult> checks,
        DiagnosticExecutionState executionState = DiagnosticExecutionState.Completed,
        DiagnosticMode mode = DiagnosticMode.Check)
    {
        var actual = DiagnosticOutcomeRules.Evaluate(
            mode,
            executionState,
            checks);

        Require(
            actual == expected,
            $"Expected outcome {expected}; received {actual}.");
    }

    /// <summary>Adapts a synchronous fixture to the regression harness.</summary>
    private static (string Name, Func<Task> Run) Check(string name, Action action)
    {
        return (name, () =>
        {
            action();

            return Task.CompletedTask;
        }
        );
    }

    /// <summary>Fails the current regression check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}