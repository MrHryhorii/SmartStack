# Tsubaki Service Check status

Last verified: 2026-11-08 UTC.

Implementation status: Phase 1 complete.
Release status: version 0.1.0 remains in development.
Next implementation phase: Phase 2 remote diagnostics.

Tsubaki Service Check is an independent command-line utility for checking
configured local and remote Tsubaki HTTP services.

## Implemented

- .NET 10 Core, CLI, and executable regression projects.
- `init`, `check`, `--help`, and `--version` commands.
- Bounded JSON profile loading, validation, and URL normalization.
- Relative route resolution that preserves reverse-proxy prefixes.
- One bounded, non-streaming speech POST per active run.
- Direct HTTP transport, disabled automatic redirects, and normal TLS validation.
- Bearer authentication through a named environment variable.
- Request and run deadlines, cancellation, and partial HTTP evidence.
- RIFF/WAVE PCM16 validation and nonzero signal verification.
- Audio duration, format, frame count, level metrics, and payload SHA-256.
- Typed diagnostic results, required/supporting roles, and outcome aggregation.
- Deterministic profile and speech request fingerprints.
- Canonical JSON and self-contained HTML reports.
- Optional retained WAV and embedded offline playback.
- Temporary-file publication, collision protection, and surviving-file reporting.

The client is independent of the inference server and its model,
GPU, and native audio dependencies.

## Regression verification

Environment: Windows, .NET SDK 10.0.302.

Latest result: 185 checks passed, 0 failed.

The suite covers profiles, URL rules, HTTP behavior, response limits,
deadlines, cancellation, secret redaction, WAV parsing, signal metrics,
outcome rules, fingerprints, HTML encoding, and artifact publication.

Reference commands, run independently from the repository root:

```powershell
dotnet build .\ServiceCheck\Cli\Tsubaki.ServiceCheck.Cli.csproj --configuration Release
```

```powershell
dotnet build .\ServiceCheck\Tests\Tsubaki.ServiceCheck.Regression.csproj --configuration Release
```

```powershell
.\ServiceCheck\Tests\bin\Release\net10.0\Tsubaki.ServiceCheck.Regression.exe
```

## Live local verification

Verified CLI informational version:

`0.1.0-dev+910a2683ad98f09ad1219479d97c3d2db33e6048`

This is the version reported by the binary used for the following checks.

### Successful speech check

Profile: `ServiceCheck/tsubaki.local.json`.

| Observation | Result |
| --- | --- |
| Target URL | `http://127.0.0.1:5045/` |
| Run ID | `20261108T213507Z_889b24700d1a` |
| Speech HTTP status | 200 |
| Required checks | Speech request, WAV container, and audio signal passed |
| Execution state | `completed` |
| Outcome | `passed_with_warnings` |
| Process exit code | 0 |
| Client-observed total run duration | 415 ms |
| Payload size | 102362 bytes |
| Audio format | PCM16, mono, 22050 Hz |
| Frame count | 51159 |
| Audio duration | 2.320136 seconds |
| Artifacts | JSON, HTML, and WAV published |
| Embedded audio | SHA-256 matches the report; playback confirmed |

The warning outcome reflects skipped supporting diagnostics.
It does not indicate a failed speech or audio check.

### Unavailable endpoint check

Profile: `ServiceCheck/Tests/Fixtures/tsubaki.closed-port.json`.

A manual TCP probe returned `False` before the diagnostic run.

| Observation | Result |
| --- | --- |
| Target URL | `http://127.0.0.1:5046/` |
| Required speech check | Failed |
| Failure code | `connection_refused` |
| Client-observed speech request duration | 2062 ms |
| Execution state | `completed` |
| Outcome | `failed` |
| Process exit code | 1 |
| Artifacts | JSON and HTML published |
| Audio checks | Skipped because the speech request failed |

Port availability must be checked again before repeating this scenario.

Earlier manual API contract observations are recorded in
`Tests/LocalIntegration.md`.

## Artifact behavior

Each check publishes a separate directory beneath the selected output root:

- `report.json`: canonical result without audio bytes.
- `report.html`: offline report with encoded dynamic text.
- `sample.wav`: supported received WAV when retention is enabled.

With audio retention disabled, measurements and hashes remain available,
while WAV output and embedded playback are omitted.

Generated `ServiceCheck/runs/` directories are excluded by the repository
ignore rules.

Report publication uses an independent writing budget so a cancelled
diagnostic can still produce a report.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Required checks passed; supporting warnings are allowed |
| 1 | Completed run with a failed required check |
| 2 | Invalid arguments, profile, or configured authentication |
| 3 | Incomplete diagnostics, unsupported required coverage, internal error, or output failure |
| 130 | User cancellation |

## Current limits

DNS and direct TCP probes, health/status requests, voice discovery,
server identity, and reported readiness are not implemented in this phase.
Their skipped coverage remains visible in active results.

The CLI currently supports active checks only. Inspection and comparison
commands remain pending.

The supported audio verification format is little-endian RIFF/WAVE PCM16.
A nonzero waveform does not establish intelligibility, pronunciation quality,
language correctness, or speaker identity.

The current build requires an installed .NET 10 runtime.
Self-contained distribution is planned for Phase 3.

Live remote HTTP/HTTPS and bearer deployments remain unverified.
Interactive Ctrl+C verification and narrow-screen/print visual review
remain pending. Cancellation and redaction behavior have fixture coverage.

## Phase 2 priorities

1. Add bounded DNS and direct TCP observations with IPv4/IPv6 handling.
2. Retrieve health, selected status fields, and advertised voices.
3. Derive service identity and readiness from labeled server observations.
4. Add inspection mode without sending a speech request.
5. Preserve active synthesis when optional metadata is restricted or unavailable.
6. Expand remote authentication, TLS, cancellation, and redaction verification.

Offline comparison, the finished report layout, example profiles,
README, and self-contained packaging belong to Phase 3.