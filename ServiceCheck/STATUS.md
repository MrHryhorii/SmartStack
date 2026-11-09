# Tsubaki Service Check status

Last verified: 2026-11-09 UTC.

Implementation status: Phases 1 and 2 complete.
Phase 2 milestone: metadata collection and both diagnostic modes verified.
Release status: version 0.1.0 remains in development.
Next implementation phase: Phase 3 comparison, documentation, and distribution.

Tsubaki Service Check is an independent command-line utility for checking
configured local and remote Tsubaki HTTP services.

## Implemented

- .NET 10 Core, CLI, and executable regression projects.
- `init`, `inspect`, `check`, `--help`, and `--version` commands.
- Bounded JSON profile loading, validation, and URL normalization.
- Relative route resolution that preserves reverse-proxy prefixes.
- Bounded DNS resolution with IPv4/IPv6 address handling.
- Direct TCP inspection with bounded concurrency and connection cleanup.
- Network observations included in canonical diagnostic reports.
- Bounded health, status, and advertised voice metadata requests.
- Health alias fallback limited to HTTP 404/405.
- Typed metadata with bounded JSON structure, fields, and voice lists.
- Sourced identity, readiness, capabilities, and defaults.
- Explicit warnings for conflicting claims and partial discovery.
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

## Operation semantics

`inspect` collects network and metadata observations. Its required checks are
service identity and server-reported synthesis readiness. It sends no speech
POST, exports a null request and audio result, and marks speech and audio
checks as `not_requested`.

`check` collects the same observations and then requests speech while the run
remains active. Its required checks are the speech request, supported WAV
container, and nonzero audio signal.

Metadata restrictions and an unready claim do not suppress an active request.
A successful waveform following an unready claim is explicitly labeled.
The normal metadata sequence uses three GETs; a missing primary health route
can add one alias GET for HTTP 404/405.

Readiness uses the most recent valid boolean claim by client observation
time. Earlier claims, source routes, server timestamps, and disagreements
remain available. Retained voice lists are limited to 512 distinct IDs.
Incomplete lists are labeled and cannot establish that a configured voice
is absent.

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

Latest result: 250 checks passed, 0 failed.

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

Metadata checks cover parsing, UTF-8, duplicate properties, field bounds,
source selection, fallback, conflicting claims, and redaction.
Complete pipeline checks cover both modes, restricted metadata, unready
claims followed by valid audio, interruption, canonical inspection output,
and remote metadata encoding in HTML.

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

## Phase 2 live local verification

Verified on 2026-11-09 UTC against `http://127.0.0.1:5045/`.
The server reported `Tsubaki TTS Engine`, version `1.0.10`.

CLI informational version reported by the tested binary:

`0.1.0-dev+e8bb01df796551c15133ab3f32837a3dfa65a082`

The `inspect --help` command was also verified.

| Observation | Inspection | Active check | Missing-status control |
| --- | --- | --- | --- |
| Mode | `inspect` | `check` | `check` |
| Run ID | `20261109T153757Z_863c60eea98b` | `20261109T154055Z_6d7cf935cab7` | `20261109T154755Z_32ad967ab85a` |
| TCP check | Passed | Passed | Passed |
| Health check | Passed | Passed | Passed |
| Status check | Passed | Passed | Failed: HTTP 404 |
| Advertised voices | 16 | 16 | 16 |
| Identity and reported readiness | Passed | Passed | Passed |
| Speech and audio checks | `not_requested` | Passed | Passed |
| Execution state | `completed` | `completed` | `completed` |
| Outcome | `passed` | `passed` | `passed_with_warnings` |
| Process exit code | 0 | 0 | 0 |
| Client-observed total duration | 178 ms | 511 ms | 409 ms |
| Published artifacts | JSON, HTML | JSON, HTML, WAV | JSON, HTML, WAV |

Inspection and the ordinary active check used
`ServiceCheck/tsubaki.local.json`.
The inspection terminal summary explicitly stated that speech generation
was not tested.

The missing-status control used
`ServiceCheck/Tests/Fixtures/tsubaki.missing-status.json`,
which deliberately sets the status route to
`servicecheck/intentional-missing-status`.
The supporting `http_not_found` result did not suppress synthesis.

| Audio observation | Active check | Missing-status control |
| --- | --- | --- |
| Payload size | 100314 bytes | 103898 bytes |
| Format | PCM16, mono, 22050 Hz | PCM16, mono, 22050 Hz |
| Duration | 2.273696 seconds | 2.354966 seconds |

## Earlier network integration verification


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

Each run publishes a separate directory beneath the selected output root:

- `report.json`: canonical result without audio bytes.
- `report.html`: offline report with encoded dynamic text.
- `sample.wav`: supported received WAV when retention is enabled.

Inspection does not publish a WAV artifact.

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

Offline comparison remains pending.

Identity and readiness are server-reported claims. A successful inspection
does not establish that synthesis works.

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

## Phase 3 priorities

1. Add bounded offline import and comparison of canonical JSON reports.
2. Complete the report layout and comparison report.
3. Add sanitized example profiles and a README.
4. Publish a self-contained Windows build.
5. Verify a genuine remote deployment and finish report visual review.
