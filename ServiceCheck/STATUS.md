# Tsubaki Service Check status

Last verified: 2026-11-12 UTC.

Tsubaki Service Check is a standalone .NET command-line utility for diagnosing
local and remote Tsubaki TTS services. It checks connectivity, inspects reported
server state, verifies a speech response, and compares saved diagnostic runs.
The client needs no inference engine, model files, or GPU dependencies.

Version 0.1.0 is in development. The diagnostic commands, offline comparison,
and report generation are implemented and verified on Windows. Work remaining
before the first release is documentation, distribution packaging, and native
Linux and cross-machine verification.

## Available functionality

| Area | Current functionality |
| --- | --- |
| Profile creation | `init` creates an editable JSON target profile. |
| Profile validation | Bounded loading, required settings, strict schema checks, and URL/route validation. |
| Connectivity | DNS and direct TCP observations, IPv4/IPv6 address handling, connection deadlines, and cleanup. |
| Service inspection | `inspect` collects health, status, advertised voices, identity, and reported synthesis readiness. It sends no speech request. |
| Speech verification | `check` collects service observations, sends at most one non-streaming speech POST, and verifies a supported nonzero PCM16 WAV response. |
| Server metadata | Selected capabilities, configured defaults, source routes, and advertised voices are available in JSON and readable HTML. |
| Diagnostic reports | Canonical JSON, self-contained HTML, and optional retained WAV with embedded offline playback. |
| Offline comparison | `compare` loads two saved diagnostic reports in explicit before/after order and exports JSON and HTML comparison reports. |
| Authentication | Bearer authentication through a named environment variable, with known token values and original request text redacted from diagnostic output. |
| Bounded execution | Configurable operation and run deadlines, response-size limits, cancellation handling, and partial diagnostic evidence. |
| Safe export | Encoded report text, restrictive HTML document policies, controlled filenames, temporary-file publication, and collision protection. |

The core, CLI, and regression projects are independent of the Tsubaki server
project. The client uses the .NET base class library.

## How results should be interpreted

`inspect` requires the expected service identity and server-reported synthesis
readiness. A successful inspection records those claims; speech generation
has not been tested.

`check` requires a successful speech response, a supported WAV container, and
nonzero PCM16 samples. Optional metadata failures remain visible as supporting
findings and do not prevent an active speech request while the run remains active.

Reports distinguish required failures, supporting warnings, missing evidence,
and intentionally omitted checks. Unknown metadata remains distinct from a
reported false or zero value. Incomplete voice discovery cannot establish that
a missing voice is unavailable.

Comparison preserves both source outcomes and their context. Changed target
IDs, URLs, modes, request fingerprints, and coverage are identified explicitly.
Different audio hashes and individual timing changes are descriptive evidence,
not automatic quality or performance regressions.

## Reports and offline behavior

Each diagnostic run publishes a new directory beneath the selected output root:

- `report.json`: canonical diagnostic data without runtime audio bytes or original speech input.
- `report.html`: offline report with encoded text and optional embedded WAV playback.
- `sample.wav`: supported received audio when retention is enabled.

Inspection creates JSON and HTML. With audio retention disabled, audio metrics
and hashes remain available while WAV output and embedded playback are omitted.

Each comparison publishes `comparison.json` and `comparison.html` in a new
directory. Comparison opens the source JSON files read-only, makes no server
requests, and loads no referenced WAV files. Historical retention flags and
sample references remain source evidence.

Report import is bounded to 8 MiB per input, with a JSON depth limit of 32 and
additional string and collection limits. Unsupported schemas, duplicate or
unknown members, missing required settings, and inconsistent evidence are rejected.
Missing historical settings are not silently replaced with current defaults.

HTML reports include their styles and require no JavaScript or external
resources. Wide tables support horizontal scrolling and keyboard focus.
Desktop, narrow-viewport, and A4 portrait preview readability have been manually
reviewed. Printed page breaks and section distribution remain browser-dependent
and are accepted as a cosmetic limitation.

Publication uses a separate 10-second writing budget and preserves available
working paths when output fails. Existing output is not overwritten. Diagnostic
publication can preserve a cancelled run; comparison publication also observes
the command's cancellation token.

## Verification completed

Latest complete regression result: **337 passed, 0 failed**.

Core, CLI, and regression Release builds succeeded on Windows. The SDK reported
during setup was .NET 10.0.302.

The executable suite covers:

- Profiles, URL normalization, proxy-prefix preservation, and route validation.
- HTTP contracts, access restrictions, redirects, response bounds, deadlines, and cancellation.
- Known-secret redaction and exclusion of original input text, credentials, and cookies from exports.
- PCM16 WAV parsing, malformed or unsupported audio, silence, signal metrics, and hashes.
- Required/supporting outcomes, missing coverage, fingerprints, and both diagnostic modes.
- DNS/TCP address handling, fallback, concurrency limits, and native loopback connection cleanup.
- Metadata parsing, conflicting claims, partial voice discovery, source selection, and HTML encoding.
- Bounded report import, source-file preservation, comparison context, typed values, and export collisions.
- Readable server settings, explicit-null limits, reported false/zero values, and inspection summaries.

Controlled HTTP and network fixtures provide reproducible failure cases.
Native loopback tests exercise actual socket behavior. These checks require
no running inference server or model files.

### Recorded integration evidence

Local server checks used `http://127.0.0.1:5045/`, reporting
`Tsubaki TTS Engine`, version `1.0.10`.

| Scenario | Recorded result |
| --- | --- |
| Local speech and offline playback | A supported nonzero PCM16 WAV was received, retained, and played. |
| Known unused port | Port 5046 returned `connection_refused`; the run failed its required speech check and returned exit code 1. |
| Restricted metadata route | An intentional status-route HTTP 404 remained a supporting warning; speech and audio verification passed with exit code 0. |
| Fresh inspection | Run `20261112T134158Z_ee1c754f5a29` completed with outcome `passed`, 16 advertised voices, and no speech or audio request evidence. |
| Offline routing comparison | Saved runs against ports 5046 and 5045 showed `failed` to `passed` under the same configured target ID and speech request fingerprint. Successful comparison publication returned exit code 0. |
| Invalid comparison input | A profile supplied as the before report was rejected with exit code 2. |
| Report layout | Diagnostic and comparison reports were manually accepted at desktop and narrow widths and in A4 portrait print preview. |

The latest inspection took 223 ms as observed by the client. Its JSON and HTML
preserved selected settings, metadata sources, and the deliberately omitted
speech checks.

The latest reviewed comparison, `20261112T140301Z_9310ca7017df`, retained 10 paired
checks and 130 selected fields. It compared runs
`20261108T215027Z_2193953cf5f7` and `20261109T154055Z_6d7cf935cab7`.
Its diagnostic evidence matched the earlier verified comparison; publication
metadata and utility version differed.

The binary used for these latest report checks printed informational version
`0.1.0-dev+af5c632bbd4d711ea9a033832567d1690ecf946e`.
Earlier API contract observations are recorded in [Tests/LocalIntegration.md](Tests/LocalIntegration.md).

## Platform and distribution status

The first release targets Windows x64 and Linux x64 with shared Core and CLI code.

| Platform | Verified now | Still pending |
| --- | --- | --- |
| Windows x64 | Development builds, regression execution, local Tsubaki diagnostics, offline comparison, and report review. | Self-contained release packaging and package verification. |
| Linux x64 | No native verification yet; the client implementation is shared with Windows. | Published build, native launch and regression verification, and a real client/server run. |

The current development build requires an installed .NET 10 runtime.
Self-contained distributions are planned to include the runtime. A Linux
distribution will still need compatible operating-system libraries.

Planned cross-machine verification uses a Lubuntu x64 client against a Tsubaki
server on Windows over a LAN. The exact tested distribution version and runtime
environment will be recorded with the results.

## Remaining work before version 0.1.0

1. Add the installation and usage README, plus sanitized local and remote example profiles.
2. Produce and verify self-contained Windows x64 and Linux x64 distribution packages.
3. Run native Linux and cross-machine LAN checks and verify interactive Ctrl+C behavior.

## Current limitations and unverified deployments

- Audio verification supports little-endian RIFF/WAVE PCM16. A nonzero waveform
  does not establish intelligibility, pronunciation quality, or speaker identity.
- Observations describe this client's access path and the metadata exposed by
  the server. They do not establish remote CPU/GPU load or a specific firewall cause.
- Each active run sends one non-streaming speech request. No automatic speech retry is performed.
- The HTTP client connects directly, with normal TLS validation and automatic
  redirects disabled. It does not use the system HTTP proxy.
- Live DNS-name, IPv6, remote HTTP/HTTPS, and bearer deployments remain unverified;
  their handling has controlled regression coverage.
- Interactive Ctrl+C, native Linux operation, and distribution packages remain pending.
- Printed content fits the tested page width; page breaks are not optimized for publication layout.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Required diagnostic checks passed, or a comparison was published; supporting diagnostic warnings are allowed. |
| 1 | A completed diagnostic run contains a failed required check. |
| 2 | Invalid arguments, profile, authentication configuration, or input report. |
| 3 | Incomplete diagnostics, unsupported required coverage, internal error, or publication failure. |
| 130 | User cancellation. |
