# Tsubaki Service Check status

Last verified: 2026-11-12 UTC.

Implementation status: Phases 1 and 2 complete; Phase 3 in progress.
Phase 2 milestone: metadata collection and both diagnostic modes verified.
Phase 3 milestone: report import, comparison CLI, and offline comparison reports verified.
Release status: version 0.1.0 remains in development.
Next implementation step: report review and release documentation.

Tsubaki Service Check is an independent command-line utility for checking
configured local and remote Tsubaki HTTP services.

## Implemented

- .NET 10 Core, CLI, and executable regression projects.
- `init`, `inspect`, `check`, `compare`, `--help`, and `--version` commands.
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
- Bounded offline import and validation of version 1 diagnostic JSON reports.
- Pure before-and-after comparison with explicit context and coverage limits.
- Canonical comparison JSON and self-contained comparison HTML.
- Comparison publication without copying or opening referenced audio files.

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

## Offline report import

`ReportLoader.LoadAsync` reads a saved canonical JSON report and returns a
validated `DiagnosticRunResult` for offline comparison.

- Maximum input: 8 MiB, including an optional UTF-8 BOM.
- Maximum JSON nesting depth: 32.
- Maximum decoded string or property name: 65536 UTF-16 code units.
- Maximum entries per JSON array or properties per object: 1024.
- Typed evidence collections retain their narrower limits, including
  256 checks, 32 network addresses or attempts, and 512 advertised voice IDs.
- Unsupported schemas, unknown or duplicate properties, invalid enum values,
  missing required data, and null collection entries are rejected.
- Exported routes, timeouts, and limits must be present; current defaults
  are not substituted for missing historical settings.
- Timestamps require an explicit offset and are normalized to UTC.
- Check IDs, roles, required evidence, audio measurements, and aggregate
  outcomes are checked for consistency.

Earlier active reports can omit network and server evidence when their
supporting checks explicitly record the unavailable coverage. Missing check
records remain missing rather than becoming fabricated observations.

Stored addresses and redacted routes remain report data. Import does not
request server URLs or load referenced WAV files. Runtime audio payloads
are excluded, and source files are opened read-only.

Verified on 2026-11-10 UTC: Core and regression Release builds succeeded;
the complete executable suite passed 282 checks with 0 failures.
The 32 import checks use controlled HTTP and network fixtures.

## Offline comparison core

`RunComparer.Compare` accepts validated before/after results plus caller-owned
tool version, comparison ID, creation timestamp, and cancellation token.
It returns a typed `RunComparisonResult` without file, network, clock, or
random-number access. Creation metadata is normalized to UTC.

- Explicit input order and source run IDs are preserved.
- Configured target IDs, URLs, modes, and fingerprints define the comparison
  context. Changed routing does not establish physical server identity.
- Active outcome comparison requires matching configured target ID, mode,
  and speech request fingerprint. Inspection remains distinct from synthesis.
- Checks are paired by stable IDs. Missing records stay missing, including
  known coverage absent from both inputs.
- Required/supporting role differences and request-dependent observations
  retain their interpretation limits.
- Diagnostic codes, HTTP status, and body-completion evidence are compared
  independently of human-readable error wording.
- Allowlisted fields retain numbers, booleans, enum labels, and units.
- Unknown values remain distinct from explicit-null server claims.
- Address and voice lists are compared without source-order noise. Partial
  voice discovery cannot establish that an absent retained ID was removed.
- Version/default changes, individual timings, and audio artifact hashes
  remain contextual observations rather than automatic regression verdicts.
- Source records and payload bytes remain unchanged. Runtime audio payloads
  are detached from the comparison result, and stored WAV paths are not read.
- Fixed inputs and publication metadata produce deterministic output across
  the verified culture settings.

Verified on 2026-11-11 UTC: Core and regression Release builds succeeded.
The complete suite passed 319 checks with 0 failures, including 37 new pure
comparison checks.

## Comparison command and artifacts

```text
TsubakiCheck compare --before <report.json> --after <report.json> --output <directory> [--open]
```

The command loads both reports through the bounded importer and preserves
their explicit before/after order. It makes no server requests, resolves no
stored URLs, and opens no referenced WAV files.

Each comparison publishes a new directory containing `comparison.json` and
`comparison.html`. JSON uses the shared UTC, textual enum, explicit-null,
and UTF-8 rules. HTML encodes recorded text, includes its styles, requires
no JavaScript, and blocks external resources through its document policy.

The HTML includes comparison context, source identity and outcomes,
findings, paired checks, and selected settings and measurements.
Missing values remain distinct from explicit-null claims. Check status
changes and descriptive field differences retain their comparison limits.

Publication uses controlled filenames, validated output identifiers, a
staging directory, no-overwrite file moves, and a 10-second writing budget.
Output failures retain available working paths. The comparison result
contains no runtime audio bytes, and source files remain unchanged.

A successfully published comparison returns 0, including when a source
diagnostic run failed or was incomplete. Invalid arguments or input reports
return 2; internal or publication failures return 3; caller cancellation
returns 130. Opening the HTML is optional and does not change comparison
success if the system cannot launch a viewer.

## Regression verification

Environment: Windows, .NET SDK 10.0.302.

Latest result, 2026-11-12 UTC: 335 checks passed, 0 failed.
Core, CLI, and regression Release builds succeeded.

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

The 32 report import checks cover canonical round trips, earlier coverage
gaps, failed and interrupted outcomes, UTF-8 and BOM handling, byte and
structure bounds, malformed data, timestamp offsets, evidence consistency,
reference isolation, cancellation, and source-file preservation.

The 37 comparison checks cover target, URL, mode, request and role changes;
new or missing coverage; interrupted execution; typed values; partial voice
lists; explicit-null limits; descriptive timing/audio differences; ordinal
identity; culture independence; deterministic metadata; and source preservation.

The 16 comparison export checks cover the JSON contract and unsupported
schema, encoded hostile text, missing and explicit-null values, offline
references, runtime audio exclusion, source-file preservation, directory
and file collisions, unsafe identifiers, output errors, cancellation before
and during HTML writing, stream failures and ownership, culture independence,
and exclusion of credentials, original input text, and cookies.

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

## Phase 3 offline comparison verification

Verified on 2026-11-12 UTC using previously captured real diagnostic reports.
The comparison performed no new server diagnostics.

CLI informational version reported by the tested binary:

`0.1.0-dev+944ae919bdf6fbbdb8a74411f137013f27313151`

| Observation | Before | After |
| --- | --- | --- |
| Run ID | `20261108T215027Z_2193953cf5f7` | `20261109T154055Z_6d7cf935cab7` |
| Target ID | `tsubaki-local` | `tsubaki-local` |
| URL | `http://127.0.0.1:5046/` | `http://127.0.0.1:5045/` |
| Mode | `check` | `check` |
| Outcome | `failed` | `passed` |
| Speech request | `connection_refused` | `speech_response_received` |
| Audio container and signal | Skipped after request failure | Passed |

Comparison ID: `20261112T101718Z_95caa196f470`.

- Process exit code: 0.
- Both JSON and HTML were published and inspected.
- JSON retained 10 paired checks and 130 selected field comparisons.
- Configured target ID, mode, and speech request fingerprint matched.
- URL and effective profile fingerprints differed.
- Earlier metadata and network gaps stayed explicit.
- Different source utility versions were identified as coverage context.
- Timing changes remained individual client observations.
- Runtime audio payloads were absent from the exported JSON.
- The HTML opened in the system browser.
- A desktop screenshot confirmed the header, comparison context, and upper
  source-run table were readable and aligned.

The `compare --help` output was verified. Passing
`ServiceCheck/tsubaki.local.json` as the before report produced a clear
invalid-before-report message and process exit code 2.

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
Comparison publication uses its own writing budget linked to command
cancellation and publishes only JSON and HTML.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Required diagnostic checks passed, or a comparison was published; supporting diagnostic warnings are allowed |
| 1 | Completed diagnostic run with a failed required check |
| 2 | Invalid arguments, profile, authentication, or input report |
| 3 | Incomplete diagnostics, unsupported required coverage, internal error, or output failure |
| 130 | User cancellation |

## Current limits

The offline comparison block is complete: bounded import, pure comparison,
CLI execution, and JSON/HTML publication are implemented and verified.
Version 0.1.0 remains in development while the remaining Phase 3 work is completed.

Identity and readiness are server-reported claims. A successful inspection
does not establish that synthesis works.

Live DNS-name and IPv6 endpoint verification remains pending.
Address-family handling and fallback have controlled regression coverage.

The supported audio verification format is little-endian RIFF/WAVE PCM16.
A nonzero waveform does not establish intelligibility, pronunciation quality,
language correctness, or speaker identity.

The current build requires an installed .NET 10 runtime.
Self-contained Windows x64 and Linux x64 distributions are planned for Phase 3.
Linux launch and regression verification remain pending.

Live remote HTTP/HTTPS and bearer deployments remain unverified.
Interactive Ctrl+C verification, whole-report desktop review, and
narrow-screen/print visual review remain pending. Cancellation and
redaction behavior have fixture coverage.

## Target platforms and planned integration

The first release targets Windows x64 and Linux x64 with a shared Core and CLI.
Separate self-contained packages and native verification are required for each.

Planned LAN verification uses a Linux client on Lubuntu x64 against a Tsubaki
server on Windows. The exact distribution version and successful runtime
environment will be recorded during verification. No hosted server is required
for this independent-client integration path.

## Phase 3 priorities

1. Complete the report layout and desktop, narrow-screen, and print review.
2. Add sanitized example profiles and a README for both target platforms.
3. Publish self-contained Windows x64 and Linux x64 builds.
4. Verify Linux operation, a genuine LAN client/server run, and interactive cancellation.
