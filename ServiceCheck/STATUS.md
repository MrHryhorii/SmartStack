# Tsubaki Service Check status

Last verified: 2026-11-09 UTC.

Implementation status: Phase 1 complete; Phase 2 in progress.
Phase 2 milestone: DNS and direct TCP inspection integrated and verified.
Release status: version 0.1.0 remains in development.
Next implementation block: server metadata and inspection mode.

Tsubaki Service Check is an independent command-line utility for checking
configured local and remote Tsubaki HTTP services.

## Implemented

- .NET 10 Core, CLI, and executable regression projects.
- `init`, `check`, `--help`, and `--version` commands.
- Bounded JSON profile loading, validation, and URL normalization.
- Relative route resolution that preserves reverse-proxy prefixes.
- Bounded DNS resolution with IPv4/IPv6 address handling.
- Direct TCP inspection with bounded concurrency and connection cleanup.
- Network observations included in canonical diagnostic reports.
- At most one bounded, non-streaming speech POST per active run.
- Direct HTTP transport, disabled automatic redirects, and normal TLS validation.
- Bearer authentication through a named environment variable.
- Stage and run deadlines, cancellation, and partial diagnostic evidence.
- RIFF/WAVE PCM16 validation and nonzero signal verification.
- Audio duration, format, frame count, level metrics, and payload SHA-256.
- Typed diagnostic results, required/supporting roles, and outcome aggregation.
- Deterministic profile and speech request fingerprints.
- Canonical JSON and self-contained HTML reports.
- Optional retained WAV and embedded offline playback.
- Temporary-file publication, collision protection, and surviving-file reporting.

The client is independent of the inference server and its model,
GPU, and native audio dependencies.

## Network behavior

IP literals skip DNS resolution and remain available as address evidence.
DNS names are normalized before resolution. Retained addresses are
deduplicated, interleaved by address family, and limited to 32 entries.

Direct TCP inspection uses at most two concurrent connection attempts.
Connections are closed without sending application data. A successful
attempt cancels and joins superseded attempts before inspection returns.

DNS and TCP failures are supporting observations. They do not suppress
the speech request while the overall run remains active.

Caller cancellation or expiration of the overall run deadline prevents
a subsequent speech request and preserves available network evidence.

Direct TCP observations use separate connections from the HTTP request.
HTTP and TLS results are established by the HTTP operation itself.

## Regression verification

Environment: Windows, .NET SDK 10.0.302.

Latest result: 204 checks passed, 0 failed.

The suite covers profiles, URL rules, HTTP behavior, response limits,
deadlines, cancellation, secret redaction, WAV parsing, signal metrics,
outcome rules, fingerprints, HTML encoding, and artifact publication.

Network coverage includes address normalization and retention,
IPv4/IPv6 fallback, bounded concurrency, stage deadlines, run interruption,
connection cleanup, native loopback closure, and refused connections.

Integration checks verify that supporting network failures preserve
the single speech request, interrupted runs prevent speech,
missing credentials prevent network inspection, and exported network
evidence redacts known bearer values.

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

## Latest live local verification

Verified on 2026-11-09 UTC.

CLI informational version:

`0.1.0-dev+e07699e4d3f403e34cb3060d579d47c565db4363`

This is the version reported by the binary used for the following check.

Profile: `ServiceCheck/tsubaki.local.json`.

| Observation | Result |
| --- | --- |
| Target URL | `http://127.0.0.1:5045/` |
| Run ID | `20261109T124413Z_8586240425b6` |
| DNS check | Skipped with `ip_literal` |
| Network address evidence | `127.0.0.1` |
| TCP check | Passed with `tcp_connected` |
| TCP attempt duration | 15 ms |
| Required checks | Speech request, WAV container, and audio signal passed |
| Execution state | `completed` |
| Outcome | `passed_with_warnings` |
| Process exit code | 0 |
| Client-observed total run duration | 357 ms |
| Payload size | 103386 bytes |
| Audio format | PCM16, mono, 22050 Hz |
| Audio duration | 2.343356 seconds |
| Artifacts | JSON, HTML, and WAV published |

The exported JSON network section was inspected. It preserved port 5045,
address `127.0.0.1`, the successful connection status, its stable code,
and the observed TCP duration.

The warning outcome reflects unavailable server metadata coverage.
The required speech and audio checks passed.

## Historical Phase 1 verification

The following checks were verified on 2026-11-08 UTC.

### Successful speech and offline playback

CLI informational version:

`0.1.0-dev+910a2683ad98f09ad1219479d97c3d2db33e6048`

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
| Embedded audio | SHA-256 matched the report; playback confirmed |

### Unavailable endpoint

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

This live check predates the integrated DNS/TCP inspection.
Port availability must be checked again before repeating the scenario.

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

Health/status requests, voice discovery, server identity, and reported
readiness remain pending. Their skipped coverage is visible in active results.

The CLI currently supports active checks only. Inspection and comparison
commands remain pending.

Live DNS-name and IPv6 endpoint verification remains pending.
Address-family handling and fallback have controlled regression coverage.

The supported audio verification format is little-endian RIFF/WAVE PCM16.
A nonzero waveform does not establish intelligibility, pronunciation quality,
language correctness, or speaker identity.

The current build requires an installed .NET 10 runtime.
Self-contained distribution is planned for Phase 3.

Live remote HTTP/HTTPS and bearer deployments remain unverified.
Interactive Ctrl+C verification and narrow-screen/print visual review
remain pending. Cancellation and redaction behavior have fixture coverage.

## Remaining Phase 2 priorities

1. Retrieve health, selected status fields, and advertised voices.
2. Derive service identity and readiness from labeled server observations.
3. Add inspection mode without sending a speech request.
4. Preserve active synthesis when optional metadata is restricted or unavailable.
5. Expand live network, authentication, TLS, and cancellation verification.

Offline comparison, the finished report layout, example profiles,
README, and self-contained packaging belong to Phase 3.