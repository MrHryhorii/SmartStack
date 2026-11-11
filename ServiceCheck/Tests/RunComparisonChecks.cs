using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Comparison;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Metadata;
using Tsubaki.ServiceCheck.Core.Profiles;
using Tsubaki.ServiceCheck.Core.Reports;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Verifies pure comparison semantics with independently configured run snapshots.</summary>
internal static class RunComparisonChecks
{
    private const string ComparisonId = "20261111T130000Z_123456abcdef";
    private const string ToolVersion = "0.1.0-comparison-fixture";
    private static readonly DateTimeOffset Epoch = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Returns comparison scope, coverage, measurement, and purity checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            Case("Matching runs retain unchanged diagnostic results", VerifyMatching),
            Case("Explicit before and after order preserved", VerifyOrder),
            Case("Repeated source run ID labeled", VerifyRepeatedRun),
            Case("Recorded failure to success transition compared", VerifyTransition),
            Case("Different target IDs limit outcome comparison", VerifyDifferentTargets),
            Case("Corrected URL remains a configured routing change", VerifyChangedUrl),
            Case("Changed speech payload limits dependent comparisons", VerifyChangedRequest),
            Case("Presentation and retention changes preserve test context", VerifyPresentation),
            Case("Inspection outcomes compare without speech requests", VerifyInspection),
            Case("Mode changes preserve shared supporting observations", VerifyModes),
            Case("Changed check roles remain distinct", VerifyRoles),
            Case("Added and missing check records retain coverage gaps", VerifyAddedAndMissing),
            Case("Missing required observations remain incomplete", VerifyMissingRequired),
            Case("Coverage absent from both inputs stays explicit", VerifyBothMissing),
            Case("Interrupted comparisons retain recorded observations", VerifyInterrupted),
            Case("Error wording does not change diagnostic status", VerifyWording),
            Case("Changed diagnostic codes remain visible", VerifyCodes),
            Case("HTTP status and completion differences remain visible", VerifyHttp),
            Case("Timing differences remain descriptive measurements", VerifyTimings),
            Case("Audio artifact changes remain informational", VerifyAudioArtifacts),
            Case("Voice and address ordering preserves comparison meaning", VerifyListOrder),
            Case("Partial voice discovery does not establish removal", VerifyPartialVoices),
            Case("Complete empty voice lists differ from unavailable discovery", VerifyEmptyVoices),
            Case("Unknown and explicit-null text limits stay distinct", VerifyUnknownTextLimit),
            Case("Reported defaults remain contextual observations", VerifyDefaults),
            Case("Server and source-tool version changes retained", VerifyVersions),
            Case("Digest letter casing preserves diagnostic identity", VerifyHashCasing),
            Case("Comparison values preserve numeric boolean and enum types", VerifyValueTypes),
            Case("Comparison output independent of current culture", VerifyCulture),
            Case("Fixed metadata produces deterministic comparison output", VerifyDeterminism),
            Case("Comparison preserves sources and detaches runtime audio", VerifySourcePreservation),
            Case("Redacted addresses and absent audio references remain offline", VerifyReferences),
            Case("Invalid run evidence rejected before comparison", VerifyInvalidEvidence),
            Case("Comparison cancellation honored", VerifyCancellation),
            Case("Invalid comparison arguments rejected", VerifyArguments),
            Case("Caller publication metadata normalized without changing sources", VerifyPublicationMetadata),
            Case("Earlier metadata coverage gaps remain available for comparison", VerifyEarlierCoverage)
        ];
    }

    /// <summary>Compares independent observations with equivalent settings and evidence.</summary>
    private static void VerifyMatching()
    {
        var result = Compare(CreateRun(), CreateRun(2));
        Require(result.SchemaVersion == 1 && result.Scope.SameTargetId
            && result.Scope.SameTargetUrl && result.Scope.SameMode
            && result.Scope.SameProfileFingerprint && result.Scope.SameRequestFingerprint == true
            && result.Scope.OutcomesComparable && result.Checks.Count == 10
            && result.Checks.All(check => check.Change == ComparisonChangeKind.Unchanged)
            && result.Fields.All(field => field.Change == ComparisonChangeKind.Unchanged)
            && result.Findings.Count == 0,
            "Equivalent observations produced artificial differences or lost their context.");
    }

    /// <summary>Retains caller order even when its timestamps run backward.</summary>
    private static void VerifyOrder()
    {
        var before = CreateRun(2);
        var after = CreateRun();
        var result = Compare(before, after);
        Require(result.Before.RunId == before.RunId && result.After.RunId == after.RunId
            && HasFinding(result, "input_timestamps_reversed"),
            "The comparer reordered its inputs or hid reversed timestamps.");
    }

    /// <summary>Labels duplicate source identity without fabricating a new observation.</summary>
    private static void VerifyRepeatedRun()
    {
        var source = CreateRun();
        var result = Compare(source, source);
        Require(HasFinding(result, "same_source_run_id")
            && result.Before.RunId == result.After.RunId,
            "A repeated source run was presented as an independent after observation.");
    }

    /// <summary>Records an actual required-status transition under matching request conditions.</summary>
    private static void VerifyTransition()
    {
        var result = Compare(CreateRun(speechFails: true), CreateRun(2));
        var speech = Check(result, "speech.request");
        Require(result.Scope.OutcomesComparable && speech.Change == ComparisonChangeKind.Changed
            && speech.Before?.Status == DiagnosticCheckStatus.Failed
            && speech.After?.Status == DiagnosticCheckStatus.Passed
            && result.Before.Outcome == DiagnosticOutcome.Failed
            && result.After.Outcome == DiagnosticOutcome.Passed
            && HasFinding(result, "recorded_outcome_changed")
            && Field(result, "audio.inspection.durationSeconds").Change == ComparisonChangeKind.BeforeMissing,
            "A failed-to-passed transition lost its original evidence or coverage.");
    }

    /// <summary>Limits functional interpretation across different configured target IDs.</summary>
    private static void VerifyDifferentTargets()
    {
        var profile = CreateProfile();
        var other = profile with { Target = profile.Target with { Id = "another-target" } };
        var result = Compare(CreateRun(speechFails: true), CreateRun(2, other));
        Require(!result.Scope.SameTargetId && !result.Scope.OutcomesComparable
            && Check(result, "speech.request").Change == ComparisonChangeKind.NotComparable
            && Field(result, "outcome").Change == ComparisonChangeKind.NotComparable
            && HasFinding(result, "different_configured_targets")
            && !HasFinding(result, "recorded_outcome_changed"),
            "Different configured targets were interpreted as the same target recovering.");
    }

    /// <summary>Shows corrected routing without equating it with physical server identity.</summary>
    private static void VerifyChangedUrl()
    {
        var profile = CreateProfile();
        var wrong = profile with
        {
            Target = profile.Target with { BaseUrl = "http://fixture.example.test:5046/proxy/" }
        };
        var result = Compare(CreateRun(profile: wrong, speechFails: true), CreateRun(2, profile));
        Require(result.Scope.SameTargetId && !result.Scope.SameTargetUrl
            && result.Scope.SameRequestFingerprint == true && result.Scope.OutcomesComparable
            && Field(result, "target.baseUrl").Change == ComparisonChangeKind.Changed
            && Field(result, "network.port").Change == ComparisonChangeKind.Changed
            && Check(result, "speech.request").Change == ComparisonChangeKind.Changed
            && Finding(result, "target_url_changed").Severity == ComparisonFindingSeverity.Warning,
            "A routing correction disappeared or incorrectly suppressed comparable request evidence.");
    }

    /// <summary>Restricts speech-derived observations while preserving shared metadata comparisons.</summary>
    private static void VerifyChangedRequest()
    {
        var profile = CreateProfile();
        var other = profile with { Synthesis = profile.Synthesis with { Voice = "alloy" } };
        var before = WithUnreadyClaim(CreateRun(profile: profile));
        var after = WithUnreadyClaim(CreateRun(2, other));
        var result = Compare(before, after);
        Require(result.Scope.SameRequestFingerprint == false && !result.Scope.OutcomesComparable
            && Field(result, "request.voice").Change == ComparisonChangeKind.Changed
            && Check(result, "speech.request").Change == ComparisonChangeKind.NotComparable
            && Check(result, "audio.signal").Change == ComparisonChangeKind.NotComparable
            && Check(result, "service.readiness.speech_discrepancy").Change == ComparisonChangeKind.NotComparable
            && Check(result, "service.health").Change == ComparisonChangeKind.Unchanged
            && HasFinding(result, "speech_request_changed"),
            "A changed payload was treated as the same functional speech test.");
    }

    /// <summary>Keeps display names and retention preferences outside request identity.</summary>
    private static void VerifyPresentation()
    {
        var profile = CreateProfile();
        var other = profile with
        {
            Name = "Renamed comparison fixture",
            Output = profile.Output with { RetainAudio = false }
        };
        var result = Compare(CreateRun(profile: profile), CreateRun(2, other));
        Require(result.Scope.SameProfileFingerprint && result.Scope.SameRequestFingerprint == true
            && result.Scope.OutcomesComparable
            && Field(result, "profile.name").Change == ComparisonChangeKind.Changed
            && Field(result, "audio.retained").Change == ComparisonChangeKind.Changed
            && !HasFinding(result, "profile_settings_changed"),
            "Presentation-only changes altered the functional comparison context.");
    }

    /// <summary>Compares inspections while retaining explicitly untested speech checks.</summary>
    private static void VerifyInspection()
    {
        var result = Compare(CreateRun(mode: DiagnosticMode.Inspect),
            CreateRun(2, mode: DiagnosticMode.Inspect));
        Require(result.Scope.SameRequestFingerprint is null && result.Scope.OutcomesComparable
            && Check(result, "service.identity").Change == ComparisonChangeKind.Unchanged
            && Check(result, "speech.request").Change == ComparisonChangeKind.NotComparable
            && result.Before.Request is null && result.After.Request is null
            && result.Fields.All(field => !field.Path.StartsWith("audio.inspection.", StringComparison.Ordinal)),
            "Inspection comparison invented an active request or verified waveform.");
    }

    /// <summary>Keeps common supporting observations useful across operation modes.</summary>
    private static void VerifyModes()
    {
        var result = Compare(CreateRun(mode: DiagnosticMode.Inspect), CreateRun(2));
        Require(!result.Scope.SameMode && !result.Scope.OutcomesComparable
            && Check(result, "service.identity").Change == ComparisonChangeKind.NotComparable
            && Check(result, "tcp.connect").Change == ComparisonChangeKind.Unchanged
            && Field(result, "request.fingerprint").Change == ComparisonChangeKind.BeforeMissing
            && HasFinding(result, "mode_changed") && HasFinding(result, "check_roles_changed"),
            "Mode changes erased shared coverage or conflated required check roles.");
    }

    /// <summary>Retains role changes for extensible check identifiers.</summary>
    private static void VerifyRoles()
    {
        var before = AddCheck(CreateRun(), NewCheck("fixture.custom"));
        var after = AddCheck(CreateRun(2), NewCheck("fixture.custom") with
        {
            Role = DiagnosticCheckRole.Required
        });
        var result = Compare(before, after);
        Require(Check(result, "fixture.custom").Change == ComparisonChangeKind.NotComparable
            && HasFinding(result, "check_roles_changed"),
            "A required observation was treated as equivalent to supporting coverage.");
    }

    /// <summary>Preserves one-sided observations and deterministic union ordering.</summary>
    private static void VerifyAddedAndMissing()
    {
        var before = AddCheck(CreateRun(), NewCheck("fixture.before_only"));
        var after = AddCheck(CreateRun(2), NewCheck("fixture.after_only"));
        var result = Compare(before, after);
        var missing = Check(result, "fixture.before_only");
        var added = Check(result, "fixture.after_only");
        Require(missing.Change == ComparisonChangeKind.AfterMissing
            && missing.Before is not null && missing.After is null
            && added.Change == ComparisonChangeKind.BeforeMissing
            && added.Before is null && added.After is not null
            && result.Checks.Take(before.Checks.Count).Select(check => check.Id)
                .SequenceEqual(before.Checks.Select(check => check.Id))
            && result.Checks[^1].Id == "fixture.after_only"
            && HasFinding(result, "check_coverage_changed"),
            "The comparer fabricated a missing check or changed source order.");
    }

    /// <summary>Distinguishes a new observation from a formerly passing required check.</summary>
    private static void VerifyMissingRequired()
    {
        var before = RemoveCheck(CreateRun(), "audio.signal");
        var result = Compare(before, CreateRun(2));
        var signal = Check(result, "audio.signal");
        Require(result.Before.Outcome == DiagnosticOutcome.Incomplete
            && signal.Change == ComparisonChangeKind.BeforeMissing && signal.Before is null
            && signal.After?.Status == DiagnosticCheckStatus.Passed
            && HasFinding(result, "check_coverage_incomplete"),
            "Absent required evidence became an invented before pass or failure.");
    }

    /// <summary>Labels known coverage missing from both inputs without creating observations.</summary>
    private static void VerifyBothMissing()
    {
        var result = Compare(RemoveCheck(CreateRun(), "audio.signal"),
            RemoveCheck(CreateRun(2), "audio.signal"));
        Require(result.Before.Outcome == DiagnosticOutcome.Incomplete
            && result.After.Outcome == DiagnosticOutcome.Incomplete
            && result.Checks.All(check => check.Id != "audio.signal")
            && HasFinding(result, "check_coverage_incomplete"),
            "Coverage missing on both sides became a fabricated unchanged success.");
    }

    /// <summary>Preserves completed observations in a run whose final execution was interrupted.</summary>
    private static void VerifyInterrupted()
    {
        foreach (var state in new[]
            { DiagnosticExecutionState.Cancelled, DiagnosticExecutionState.TimedOut, DiagnosticExecutionState.Error })
        {
            var before = CreateRun() with { ExecutionState = state, Outcome = DiagnosticOutcome.Incomplete };
            var result = Compare(before, CreateRun(2));
            Require(result.Before.ExecutionState == state
                && Check(result, "speech.request").Before?.Status == DiagnosticCheckStatus.Passed
                && Finding(result, "interrupted_execution").Severity == ComparisonFindingSeverity.Warning,
                "Interrupted comparison discarded recorded observations or hid incomplete execution.");
        }
    }

    /// <summary>Uses diagnostic codes rather than summaries or error excerpts as status keys.</summary>
    private static void VerifyWording()
    {
        var before = CreateRun();
        var after = UpdateCheck(CreateRun(2), "service.status", check => check with
        {
            Summary = "A revised diagnostic explanation.",
            Http = check.Http! with { ErrorExcerpt = "A different bounded explanation." }
        });
        var result = Compare(before, after);
        Require(Check(result, "service.status").Change == ComparisonChangeKind.Unchanged,
            "A prose change became a diagnostic status transition.");
    }

    /// <summary>Exposes a changed machine-readable code even when status text stays the same.</summary>
    private static void VerifyCodes()
    {
        var after = UpdateCheck(CreateRun(2), "service.health",
            check => check with { Code = "metadata_accepted" });
        Require(Check(Compare(CreateRun(), after), "service.health").Change == ComparisonChangeKind.Changed,
            "A machine-readable result-code change was hidden.");
    }

    /// <summary>Compares HTTP evidence independently of a shared diagnostic error code.</summary>
    private static void VerifyHttp()
    {
        var before = WithStatusFailure(CreateRun(), 500, true);
        var after = WithStatusFailure(CreateRun(2), 503, true);
        var result = Compare(before, after);
        Require(Check(result, "service.status").Change == ComparisonChangeKind.Changed
            && Field(result, "checks[\"service.status\"].http.statusCode").Change == ComparisonChangeKind.Changed,
            "A changed HTTP status disappeared behind the same error category.");

        before = WithStatusFailure(CreateRun(), 503, true);
        after = WithStatusFailure(CreateRun(2), 503, false);
        result = Compare(before, after);
        Require(Check(result, "service.status").Change == ComparisonChangeKind.Changed
            && Field(result, "checks[\"service.status\"].http.bodyComplete").Change == ComparisonChangeKind.Changed,
            "HTTP completion evidence was omitted from the comparison.");
    }

    /// <summary>Shows numeric durations without converting one sample into a performance verdict.</summary>
    private static void VerifyTimings()
    {
        var after = CreateRun(2) with { Timings = new DiagnosticTimings(500, 300, 200, 500) };
        after = UpdateCheck(after, "speech.request", check => check with { DurationMs = 500 });
        var result = Compare(CreateRun(), after);
        var field = Field(result, "timings.speechTotalMs");
        Require(field.Change == ComparisonChangeKind.Changed && field.Unit == "ms"
            && Value(field.Before).GetInt64() == 60 && Value(field.After).GetInt64() == 500
            && Check(result, "speech.request").Change == ComparisonChangeKind.Unchanged
            && Finding(result, "single_run_timing").Severity == ComparisonFindingSeverity.Information,
            "A duration difference became an unsupported diagnostic or benchmark verdict.");
    }

    /// <summary>Reports changed payload identity while keeping successful signal checks intact.</summary>
    private static void VerifyAudioArtifacts()
    {
        var result = Compare(CreateRun(), CreateRun(2, amplitude: 4096));
        Require(Field(result, "audio.inspection.payloadSha256").Change == ComparisonChangeKind.Changed
            && Field(result, "audio.inspection.rms").Change == ComparisonChangeKind.Changed
            && Check(result, "audio.signal").Change == ComparisonChangeKind.Unchanged
            && result.Before.Outcome == result.After.Outcome
            && Finding(result, "audio_artifact_changed").Severity == ComparisonFindingSeverity.Information,
            "Different valid artifacts were treated as a functional or quality regression.");
    }

    /// <summary>Normalizes unordered evidence lists without mutating their source order.</summary>
    private static void VerifyListOrder()
    {
        var before = CreateRun();
        var after = CreateRun(2);
        var reversedVoices = after.Server!.AdvertisedVoices.Reverse().ToArray();
        var reversedAddresses = after.Network!.ResolvedAddresses.Reverse().ToArray();
        after = after with
        {
            Server = after.Server! with { AdvertisedVoices = reversedVoices },
            Network = after.Network! with { ResolvedAddresses = reversedAddresses }
        };
        var result = Compare(before, after);
        Require(Field(result, "server.advertisedVoices").Change == ComparisonChangeKind.Unchanged
            && Field(result, "network.resolvedAddresses").Change == ComparisonChangeKind.Unchanged
            && after.Server!.AdvertisedVoices.SequenceEqual(reversedVoices)
            && after.Network!.ResolvedAddresses.SequenceEqual(reversedAddresses),
            "List ordering became a difference or source evidence was sorted in place.");
    }

    /// <summary>Retains partial lists without claiming that an absent retained ID was removed.</summary>
    private static void VerifyPartialVoices()
    {
        var after = CreateRun(2);
        after = after with
        {
            Server = after.Server! with { AdvertisedVoices = ["piper_base"], AdvertisedVoicesComplete = false }
        };
        after = UpdateCheck(after, "service.voices", check => check with
        {
            Status = DiagnosticCheckStatus.Warning,
            Code = "voices_incomplete"
        });
        var result = Compare(CreateRun(), after);
        Require(Field(result, "server.advertisedVoices").Change == ComparisonChangeKind.NotComparable
            && Finding(result, "voice_coverage_limited").Severity == ComparisonFindingSeverity.Warning,
            "A partial retained list was interpreted as proof of voice removal.");
    }

    /// <summary>Separates a complete empty claim from missing discovery evidence.</summary>
    private static void VerifyEmptyVoices()
    {
        var before = CreateRun();
        before = before with { Server = before.Server! with { AdvertisedVoices = [] } };
        var after = CreateRun(2);
        after = after with
        {
            Server = after.Server! with
            {
                AdvertisedVoices = [],
                AdvertisedVoicesComplete = false,
                VoicesSourceRoute = null
            }
        };
        var field = Field(Compare(before, after), "server.advertisedVoices");
        Require(field.Change == ComparisonChangeKind.AfterMissing
            && Value(field.Before).ValueKind == JsonValueKind.Array
            && Value(field.Before).GetArrayLength() == 0 && field.After is null,
            "An unavailable list was silently equated with a complete empty list.");
    }

    /// <summary>Preserves the availability distinction for an unlimited server text claim.</summary>
    private static void VerifyUnknownTextLimit()
    {
        var before = CreateRun();
        before = before with
        {
            Server = before.Server! with
            {
                StatusSnapshot = before.Server!.StatusSnapshot! with { TextLimitKnown = false }
            }
        };
        var after = CreateRun(2);
        var field = Field(Compare(before, after), "server.statusSnapshot.maxTextLength");
        Require(field.Change == ComparisonChangeKind.BeforeMissing && field.Before is null
            && Value(field.After).ValueKind == JsonValueKind.Null,
            "Unknown text coverage became a known unlimited claim.");

        field = Field(Compare(after, before), "server.statusSnapshot.maxTextLength");
        Require(field.Change == ComparisonChangeKind.AfterMissing
            && Value(field.Before).ValueKind == JsonValueKind.Null && field.After is null,
            "The explicit-null claim was lost when comparison order changed.");
    }

    /// <summary>Exposes changed defaults without failing otherwise successful speech observations.</summary>
    private static void VerifyDefaults()
    {
        var after = CreateRun(2);
        after = after with
        {
            Server = after.Server! with
            {
                StatusSnapshot = after.Server!.StatusSnapshot! with { MaxTextLength = 100, DefaultVolume = 0.5 }
            }
        };
        var result = Compare(CreateRun(), after);
        var field = Field(result, "server.statusSnapshot.maxTextLength");
        Require(field.Change == ComparisonChangeKind.Changed
            && Value(field.Before).ValueKind == JsonValueKind.Null && Value(field.After).GetInt32() == 100
            && result.Before.Outcome == DiagnosticOutcome.Passed && result.After.Outcome == DiagnosticOutcome.Passed
            && Finding(result, "reported_defaults_changed").Severity == ComparisonFindingSeverity.Information,
            "Reported settings changes became an automatic failure or lost explicit-null meaning.");
    }

    /// <summary>Retains reported server and source utility version differences as context.</summary>
    private static void VerifyVersions()
    {
        var after = CreateRun(2);
        after = after with
        {
            Server = after.Server! with { Version = "1.0.11" },
            Tool = after.Tool with { Version = "0.2.0-fixture" }
        };
        var result = Compare(CreateRun(), after);
        Require(Field(result, "server.version").Change == ComparisonChangeKind.Changed
            && Field(result, "tool.version").Change == ComparisonChangeKind.Changed
            && Finding(result, "reported_version_changed").Severity == ComparisonFindingSeverity.Information
            && Finding(result, "source_tool_changed").Severity == ComparisonFindingSeverity.Information,
            "Version differences disappeared or became automatic regressions.");
    }

    /// <summary>Compares equivalent hexadecimal digests without a casing-induced context change.</summary>
    private static void VerifyHashCasing()
    {
        var after = CreateRun(2);
        after = after with
        {
            Profile = after.Profile with { Fingerprint = after.Profile.Fingerprint.ToUpperInvariant() },
            Request = after.Request! with
            {
                Fingerprint = after.Request!.Fingerprint.ToUpperInvariant(),
                InputSha256 = after.Request!.InputSha256.ToUpperInvariant()
            },
            Audio = after.Audio! with
            {
                Inspection = after.Audio!.Inspection with
                {
                    PayloadSha256 = after.Audio!.Inspection.PayloadSha256.ToUpperInvariant()
                }
            }
        };
        var result = Compare(CreateRun(), after);
        Require(result.Scope.SameProfileFingerprint && result.Scope.SameRequestFingerprint == true
            && Field(result, "request.inputSha256").Change == ComparisonChangeKind.Unchanged
            && Field(result, "audio.inspection.payloadSha256").Change == ComparisonChangeKind.Unchanged
            && !HasFinding(result, "audio_artifact_changed"),
            "Digest casing changed the request context or artifact identity.");
    }

    /// <summary>Keeps machine-readable values typed rather than formatting all of them as strings.</summary>
    private static void VerifyValueTypes()
    {
        var result = Compare(CreateRun(), CreateRun(2));
        Require(Value(Field(result, "request.speed").Before).ValueKind == JsonValueKind.Number
            && Value(Field(result, "request.speed").Before).GetDouble() == 1
            && Value(Field(result, "request.stream").Before).ValueKind == JsonValueKind.False
            && Value(Field(result, "mode").Before).GetString() == "check"
            && Value(Field(result, "audio.inspection.payloadBytes").Before).GetInt64() > 44,
            "Comparison field values lost their numeric, boolean, or enum representation.");
    }

    /// <summary>Preserves ordinal identity and invariant numeric output across cultures.</summary>
    private static void VerifyCulture()
    {
        var before = CreateRun();
        var after = CreateRun(2, amplitude: 4096);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            var first = JsonSerializer.SerializeToUtf8Bytes(Compare(before, after));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            var second = JsonSerializer.SerializeToUtf8Bytes(Compare(before, after));
            Require(first.AsSpan().SequenceEqual(second), "Current culture changed the comparison output.");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    /// <summary>Produces the same result when inputs and caller metadata are unchanged.</summary>
    private static void VerifyDeterminism()
    {
        var before = CreateRun();
        var after = CreateRun(2);
        var first = JsonSerializer.SerializeToUtf8Bytes(Compare(before, after));
        var second = JsonSerializer.SerializeToUtf8Bytes(Compare(before, after));
        Require(first.AsSpan().SequenceEqual(second),
            "The pure comparer introduced clock, random, or unstable ordering data.");
    }

    /// <summary>Leaves input records and payload bytes intact while detaching runtime bytes from output.</summary>
    private static void VerifySourcePreservation()
    {
        var before = CreateRun();
        var after = CreateRun(2);
        var beforeJson = ReportJson.Serialize(before);
        var afterJson = ReportJson.Serialize(after);
        var payload = before.AudioPayload!;
        var originalBytes = payload.ToArray();
        var result = Compare(before, after);

        Require(ReportJson.Serialize(before).AsSpan().SequenceEqual(beforeJson)
            && ReportJson.Serialize(after).AsSpan().SequenceEqual(afterJson)
            && ReferenceEquals(before.AudioPayload, payload) && payload.AsSpan().SequenceEqual(originalBytes)
            && result.Before.AudioPayload is null && result.After.AudioPayload is null,
            "Comparison mutated a source or retained unnecessary runtime audio bytes.");
    }

    /// <summary>Accepts redacted configuration as report data without interpreting stored references.</summary>
    private static void VerifyReferences()
    {
        var before = RedactReferences(CreateRun());
        var after = RedactReferences(CreateRun(2));
        var result = Compare(before, after);
        Require(result.Before.Target.BaseUrl == "[REDACTED]"
            && result.After.Request?.Route == "[REDACTED]"
            && result.Before.Audio?.SamplePath == "sample.wav"
            && result.Before.AudioPayload is null && result.Scope.OutcomesComparable,
            "Stored references were resolved, normalized, or loaded as new audio evidence.");
    }

    /// <summary>Reuses report validation before comparing malformed or contradictory inputs.</summary>
    private static void VerifyInvalidEvidence()
    {
        var valid = CreateRun();
        ExpectException<JsonException>(() => Compare(valid with { SchemaVersion = 2 }, CreateRun(2)));
        ExpectException<JsonException>(() => Compare(valid, CreateRun(2) with { SchemaVersion = 2 }));
        ExpectException<JsonException>(() => Compare(valid with { Outcome = DiagnosticOutcome.Failed }, CreateRun(2)));
        ExpectException<JsonException>(() => Compare(valid with
        {
            Checks = [.. valid.Checks, valid.Checks[0]],
            Outcome = DiagnosticOutcome.Incomplete
        }, CreateRun(2)));
        ExpectException<JsonException>(() => Compare(valid with
        {
            Profile = valid.Profile with { Fingerprint = "invalid" }
        }, CreateRun(2)));
    }

    /// <summary>Stops a cancelled comparison before producing a result.</summary>
    private static void VerifyCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = CreateRun();
        var after = CreateRun(2);
        ExpectException<OperationCanceledException>(() =>
            RunComparer.Compare(before, after, ToolVersion, ComparisonId, Epoch, cancellation.Token));
    }

    /// <summary>Rejects absent sources and invalid caller-owned metadata.</summary>
    private static void VerifyArguments()
    {
        var before = CreateRun();
        var after = CreateRun(2);
        ExpectException<ArgumentNullException>(() => Compare(null!, after));
        ExpectException<ArgumentNullException>(() => Compare(before, null!));
        ExpectException<ArgumentException>(() =>
            RunComparer.Compare(before, after, "", ComparisonId, Epoch));
        ExpectException<ArgumentException>(() =>
            RunComparer.Compare(before, after, ToolVersion, "", Epoch));
        ExpectException<ArgumentException>(() =>
            RunComparer.Compare(before, after, "invalid\nversion", ComparisonId, Epoch));
        ExpectException<ArgumentException>(() =>
            RunComparer.Compare(before, after, ToolVersion, new string('x', 129), Epoch));
    }

    /// <summary>Uses supplied metadata and normalizes its offset without generating new identity.</summary>
    private static void VerifyPublicationMetadata()
    {
        var result = RunComparer.Compare(CreateRun(), CreateRun(2), ToolVersion, ComparisonId,
            Epoch.AddHours(1).ToOffset(TimeSpan.FromHours(2)));
        Require(result.ComparisonId == ComparisonId && result.Tool.Version == ToolVersion
            && result.CreatedAtUtc == Epoch.AddHours(1) && result.CreatedAtUtc.Offset == TimeSpan.Zero,
            "Caller-supplied publication metadata was replaced or kept a non-UTC offset.");
    }

    /// <summary>Compares a successful earlier run without inventing its unavailable metadata.</summary>
    private static void VerifyEarlierCoverage()
    {
        var before = CreateRun() with { Server = null, Network = null };
        before = WithChecks(before, before.Checks.Select(check => check.Role == DiagnosticCheckRole.Supporting
            ? check with
            {
                Status = DiagnosticCheckStatus.Skipped,
                Code = "coverage_unavailable",
                Http = null,
                DurationMs = null
            }
            : check).ToArray());
        var result = Compare(before, CreateRun(2));
        Require(result.Before.Server is null && result.Before.Network is null
            && Check(result, "speech.request").Change == ComparisonChangeKind.Unchanged
            && Field(result, "server.version").Change == ComparisonChangeKind.BeforeMissing
            && HasFinding(result, "metadata_coverage_limited"),
            "Earlier missing metadata became fabricated claims or suppressed verified speech.");
    }

    /// <summary>Creates a target with deterministic supported settings.</summary>
    private static TargetProfile CreateProfile()
    {
        return new TargetProfile
        {
            SchemaVersion = 1,
            Name = "Comparison fixture",
            Target = new ProfileTarget
            {
                Id = "comparison-target",
                BaseUrl = "http://fixture.example.test:5045/proxy/"
            }
        };
    }

    /// <summary>Builds canonical run data using actual configuration and audio snapshot producers.</summary>
    private static DiagnosticRunResult CreateRun(
        int number = 1,
        TargetProfile? profile = null,
        DiagnosticMode mode = DiagnosticMode.Check,
        bool speechFails = false,
        short amplitude = 8192)
    {
        var effective = profile ?? CreateProfile();
        var snapshots = DiagnosticSnapshotBuilder.Create(effective, value => value);
        var started = Epoch.AddSeconds(number);
        var active = mode == DiagnosticMode.Check;
        var hasAudio = active && !speechFails;
        var payload = hasAudio ? CreateWav(amplitude) : null;
        var inspection = payload is null ? null : WavInspector.Inspect(payload) with { InspectionDurationMs = 1 };
        var checks = new List<DiagnosticCheckResult>
        {
            NewCheck("dns.resolve", "dns_resolved"),
            NewCheck("tcp.connect", "tcp_connected"),
            NewCheck("service.health", "metadata_recognized", http: MetadataHttp(effective.Routes.Health)),
            NewCheck("service.status", "metadata_recognized", http: MetadataHttp(effective.Routes.Status)),
            NewCheck("service.voices", "voices_received", http: MetadataHttp(effective.Routes.Voices)),
            NewCheck("service.identity", "service_identity_reported", http: MetadataHttp(effective.Routes.Health)) with
            {
                Role = active ? DiagnosticCheckRole.Supporting : DiagnosticCheckRole.Required
            },
            NewCheck("service.readiness", "readiness_reported_ready", http: MetadataHttp(effective.Routes.Health)) with
            {
                Role = active ? DiagnosticCheckRole.Supporting : DiagnosticCheckRole.Required
            }
        };

        var speechHttp = active ? new DiagnosticHttpEvidence
        {
            Method = "POST",
            Route = effective.Routes.Speech,
            StatusCode = speechFails ? null : 200,
            MediaType = speechFails ? null : "audio/wav",
            BodyComplete = !speechFails,
            BodyBytesRead = payload?.LongLength,
            DeclaredContentLength = payload?.LongLength,
            ExceptionType = speechFails ? "HttpRequestException" : null
        } : null;

        checks.Add(NewCheck("speech.request",
            active ? speechFails ? "connection_refused" : "speech_response_received" : "not_requested",
            active ? speechFails ? DiagnosticCheckStatus.Failed : DiagnosticCheckStatus.Passed : DiagnosticCheckStatus.Skipped,
            speechHttp) with
        {
            Role = active ? DiagnosticCheckRole.Required : DiagnosticCheckRole.Supporting,
            DurationMs = active ? 60 : null
        });
        checks.Add(NewCheck("audio.container", active ? hasAudio ? "audio_container_verified" : "dependency_failed" : "not_requested",
            hasAudio ? DiagnosticCheckStatus.Passed : DiagnosticCheckStatus.Skipped) with
        {
            Role = active ? DiagnosticCheckRole.Required : DiagnosticCheckRole.Supporting,
            DurationMs = hasAudio ? 1 : null
        });
        checks.Add(NewCheck("audio.signal", active ? hasAudio ? "audio_signal_verified" : "dependency_failed" : "not_requested",
            hasAudio ? inspection!.SignalState == AudioSignalState.Nonzero
                ? DiagnosticCheckStatus.Passed : DiagnosticCheckStatus.Failed : DiagnosticCheckStatus.Skipped) with
        {
            Role = active ? DiagnosticCheckRole.Required : DiagnosticCheckRole.Supporting,
            DurationMs = hasAudio ? 1 : null
        });

        return new DiagnosticRunResult
        {
            SchemaVersion = 1,
            RunId = started.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + $"_{number:x12}",
            Tool = new DiagnosticToolInfo("Tsubaki Service Check", "0.1.0-source-fixture"),
            Mode = mode,
            StartedAtUtc = started,
            FinishedAtUtc = started.AddMilliseconds(100),
            ExecutionState = DiagnosticExecutionState.Completed,
            Outcome = DiagnosticOutcomeRules.Evaluate(mode, DiagnosticExecutionState.Completed, checks),
            Target = snapshots.Target,
            Profile = snapshots.Profile,
            Request = active ? snapshots.Request : null,
            Network = new DiagnosticNetworkEvidence(new Uri(snapshots.Target.BaseUrl).Port,
                ["127.0.0.1", "::1"],
                [new DiagnosticConnectionEvidence("127.0.0.1", new Uri(snapshots.Target.BaseUrl).Port,
                    DiagnosticCheckStatus.Passed, "tcp_connected", 1)]),
            Server = new DiagnosticServerEvidence("Tsubaki TTS Engine", "1.0.10", DiagnosticReadiness.Ready,
                ["piper_base", "alloy"])
            {
                IdentitySourceRoute = effective.Routes.Health,
                VersionSourceRoute = effective.Routes.Health,
                ReadinessSourceRoute = effective.Routes.Health,
                ReadinessObservedAtUtc = started,
                VoicesSourceRoute = effective.Routes.Voices,
                AdvertisedVoicesComplete = true,
                StatusSourceRoute = effective.Routes.Status,
                StatusSnapshot = new MetadataStatusSnapshot
                {
                    SampleRateHz = 8000,
                    WavAvailable = true,
                    TextLimitKnown = true,
                    MaxTextLength = null,
                    DefaultVolume = 1
                }
            },
            Timings = new DiagnosticTimings(100,
                hasAudio ? 20 : null, hasAudio ? 40 : null, active ? 60 : null),
            Audio = inspection is null ? null : new DiagnosticAudioResult(inspection,
                effective.Output.RetainAudio, effective.Output.RetainAudio ? "sample.wav" : null),
            AudioPayload = effective.Output.RetainAudio ? payload : null,
            Checks = checks.ToArray()
        };
    }

    /// <summary>Creates one stable, typed diagnostic observation.</summary>
    private static DiagnosticCheckResult NewCheck(
        string id,
        string code = "fixture_passed",
        DiagnosticCheckStatus status = DiagnosticCheckStatus.Passed,
        DiagnosticHttpEvidence? http = null)
    {
        return new DiagnosticCheckResult
        {
            Id = id,
            Role = DiagnosticCheckRole.Supporting,
            Status = status,
            Code = code,
            Summary = "Fixture diagnostic observation.",
            ObservedAtUtc = Epoch,
            DurationMs = 1,
            Http = http
        };
    }

    /// <summary>Creates complete metadata transport evidence without making a request.</summary>
    private static DiagnosticHttpEvidence MetadataHttp(string route)
    {
        return new DiagnosticHttpEvidence
        {
            Method = "GET", Route = route, StatusCode = 200, MediaType = "application/json",
            DeclaredContentLength = 128, BodyBytesRead = 128, BodyComplete = true
        };
    }

    /// <summary>Applies a check list and derives its canonical aggregate outcome.</summary>
    private static DiagnosticRunResult WithChecks(DiagnosticRunResult source, IReadOnlyList<DiagnosticCheckResult> checks)
    {
        return source with
        {
            Checks = checks,
            Outcome = DiagnosticOutcomeRules.Evaluate(source.Mode, source.ExecutionState, checks)
        };
    }

    /// <summary>Changes one observation while keeping aggregate evidence consistent.</summary>
    private static DiagnosticRunResult UpdateCheck(
        DiagnosticRunResult source, string id, Func<DiagnosticCheckResult, DiagnosticCheckResult> update)
    {
        return WithChecks(source, source.Checks.Select(check => check.Id == id ? update(check) : check).ToArray());
    }

    /// <summary>Adds one observation without changing source collections.</summary>
    private static DiagnosticRunResult AddCheck(DiagnosticRunResult source, DiagnosticCheckResult check)
    {
        return WithChecks(source, [.. source.Checks, check]);
    }

    /// <summary>Removes one record while retaining an explicit incomplete outcome when required.</summary>
    private static DiagnosticRunResult RemoveCheck(DiagnosticRunResult source, string id)
    {
        return WithChecks(source, source.Checks.Where(check => check.Id != id).ToArray());
    }

    /// <summary>Creates a successful active run following a reported unready claim.</summary>
    private static DiagnosticRunResult WithUnreadyClaim(DiagnosticRunResult source)
    {
        source = source with { Server = source.Server! with { Readiness = DiagnosticReadiness.NotReady } };
        source = UpdateCheck(source, "service.readiness", check => check with
        {
            Status = DiagnosticCheckStatus.Failed,
            Code = "readiness_reported_not_ready"
        });
        return AddCheck(source, NewCheck("service.readiness.speech_discrepancy",
            "speech_after_reported_unready", DiagnosticCheckStatus.Warning));
    }

    /// <summary>Records a supporting HTTP failure with configurable response completion.</summary>
    private static DiagnosticRunResult WithStatusFailure(DiagnosticRunResult source, int status, bool complete)
    {
        return UpdateCheck(source, "service.status", check => check with
        {
            Status = DiagnosticCheckStatus.Failed,
            Code = "http_server_error",
            Http = check.Http! with { StatusCode = status, BodyComplete = complete }
        });
    }

    /// <summary>Replaces exported references with non-executable redacted text.</summary>
    private static DiagnosticRunResult RedactReferences(DiagnosticRunResult source)
    {
        return source with
        {
            Target = source.Target with { BaseUrl = "[REDACTED]" },
            Profile = source.Profile with { Routes = source.Profile.Routes with { Speech = "[REDACTED]" } },
            Request = source.Request! with { Route = "[REDACTED]" }
        };
    }

    /// <summary>Creates deterministic nonzero PCM16 bytes for the real WAV inspector.</summary>
    private static byte[] CreateWav(short amplitude)
    {
        const int dataBytes = 16;
        var bytes = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), dataBytes);

        for (var offset = 0; offset < dataBytes; offset += 2)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + offset),
                (short)(offset % 4 == 0 ? amplitude : -amplitude));

        return bytes;
    }

    /// <summary>Compares fixtures with fixed, caller-owned publication metadata.</summary>
    private static RunComparisonResult Compare(DiagnosticRunResult before, DiagnosticRunResult after)
    {
        return RunComparer.Compare(before, after, ToolVersion, ComparisonId, Epoch.AddHours(1));
    }

    /// <summary>Finds one check pair by its stable diagnostic ID.</summary>
    private static CheckComparisonResult Check(RunComparisonResult result, string id)
    {
        return result.Checks.Single(check => check.Id == id);
    }

    /// <summary>Finds one allowlisted value pair by its stable path.</summary>
    private static FieldComparisonResult Field(RunComparisonResult result, string path)
    {
        return result.Fields.Single(field => field.Path == path);
    }

    /// <summary>Finds one contextual finding by its stable code.</summary>
    private static ComparisonFinding Finding(RunComparisonResult result, string code)
    {
        return result.Findings.Single(finding => finding.Code == code);
    }

    /// <summary>Checks whether the comparison recorded a particular interpretation limit.</summary>
    private static bool HasFinding(RunComparisonResult result, string code)
    {
        return result.Findings.Any(finding => finding.Code == code);
    }

    /// <summary>Requires an available JSON value while retaining explicit JSON nulls.</summary>
    private static JsonElement Value(JsonElement? value)
    {
        return value ?? throw new InvalidOperationException("Expected an available comparison value.");
    }

    /// <summary>Adapts a synchronous pure-data check to the executable regression harness.</summary>
    private static (string Name, Func<Task> Run) Case(string name, Action verify)
    {
        return (name, () =>
        {
            verify();
            return Task.CompletedTask;
        });
    }

    /// <summary>Requires a specific error category without accepting unrelated fixture failures.</summary>
    private static void ExpectException<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    /// <summary>Fails a regression check with a focused diagnostic message.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
