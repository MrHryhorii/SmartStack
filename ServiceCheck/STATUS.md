# Tsubaki Service Check status

Last verified: 2026-11-13 UTC.

Tsubaki Service Check is a standalone .NET command-line utility for diagnosing
local and remote Tsubaki TTS services. It checks connectivity, reads reported
server state, verifies a speech response, and compares saved diagnostic runs.
The client needs no inference engine, model files, GPU libraries, or database.

The diagnostic commands, offline comparison, report generation, and portable
executables have been exercised on Windows x64 and Linux x64. The executable
regression suite passed all 337 checks on each platform. Live integration used
Tsubaki TTS Engine 1.0.10, including a Linux client reaching a Windows server
over a LAN.

See [README.md](README.md) for installation, configuration, commands, and portable
build instructions. [WALKTHROUGH.md](WALKTHROUGH.md) covers a complete practical
workflow, including additional diagnostic scenarios.

## Available functionality

| Area | Implemented behavior |
| --- | --- |
| Profile creation | `init` creates an editable JSON target profile and preserves an existing file. |
| Example profiles | Complete local, LAN, and HTTPS/bearer configurations under `Examples`. |
| Profile validation | Bounded loading, required settings, strict schema checks, and URL/route validation. |
| Connectivity | DNS and direct TCP observations, IPv4/IPv6 address handling, connection deadlines, and cleanup. |
| Service inspection | `inspect` collects health, status, advertised voices, identity, and reported synthesis readiness without requesting speech. |
| Speech verification | `check` collects service observations, sends at most one non-streaming speech POST, and verifies a supported nonzero PCM16 WAV response. |
| Server metadata | Selected capabilities, configured defaults, source routes, and advertised voices appear in JSON and readable HTML. |
| Diagnostic reports | Canonical JSON, offline HTML, and optional retained WAV with embedded playback. |
| Offline comparison | `compare` loads two saved diagnostic reports in explicit before/after order and exports JSON and HTML comparison reports. |
| Authentication | Bearer authentication through a named environment variable; resolved token values and original input text are redacted from diagnostic text. |
| Bounded execution | Configurable operation and run deadlines, response-size limits, cancellation handling, and partial evidence. |
| Publication | Encoded report text, controlled filenames, staging directories, and collision protection. |
| Portable execution | Self-contained Windows x64 and Linux x64 executables with external profile, input-report, and output paths. |

Core, CLI, and regression projects are independent of the Tsubaki server project.
The client uses the .NET base class library.

## How results should be interpreted

`inspect` requires the expected service identity and server-reported synthesis
readiness. A successful inspection records those claims. Speech generation has
not been tested by this command.

`check` requires a successful speech response, a supported WAV container, and
nonzero PCM16 samples. Metadata failures remain visible as supporting findings
and do not prevent a speech request while the run remains active.

Reports distinguish required failures, supporting warnings, unavailable evidence,
and deliberately omitted checks. Unknown metadata remains distinct from a
reported false or zero value. Incomplete voice discovery cannot establish that
a missing voice is unavailable.

Comparison preserves source outcomes and identifies differences in target IDs,
URLs, modes, request fingerprints, settings, and coverage. A shared target ID
is a configured logical label. It does not establish physical server identity.
Audio hashes and individual timings are recorded observations; their changes
do not automatically establish quality or performance regressions.

## Verification baseline

The latest portable integration records used client build:

```text
TsubakiCheck 0.1.0-dev+7973b9f5c52b3d43988f12a51a83b528f85a19ec
```

Source revision: `7973b9f5c52b3d43988f12a51a83b528f85a19ec`.
The identifier pins these observations independently of later documentation
and example-profile updates.

| Environment | Recorded verification |
| --- | --- |
| Windows x64 | Core, CLI, and regression Release builds; 337 regression checks passed; portable executable launch, profile creation, local inspection, speech verification, and offline comparison. |
| Ubuntu 24.04.5 LTS x64, Lubuntu desktop | Native portable executable launch and profile creation; 337 regression checks passed with exit code 0; LAN inspection and speech against a Windows server; offline comparison, HTML playback, and SIGINT cancellation. |

Both portable executables were published on Windows using the .NET 10 SDK.
The SDK reported during setup was 10.0.302. Linux execution used the resulting
self-contained binary; a Linux SDK was not required for those checks.
The distribution version above comes from the Linux machine's `/etc/os-release`.

### Regression coverage

Latest complete results: **Windows 337 passed, 0 failed; Linux 337 passed, 0 failed**.

The executable suite covers:

- Profile loading, schema validation, URL normalization, proxy prefixes, and routes.
- HTTP contracts, access failures, redirects, response bounds, deadlines, and cancellation.
- Known-secret redaction and exclusion of original input text and sensitive headers from exports.
- PCM16 WAV parsing, malformed or unsupported audio, silence, signal metrics, and hashes.
- Required/supporting outcomes, missing evidence, fingerprints, and both diagnostic modes.
- DNS/TCP address handling, fallback, concurrency limits, and native loopback cleanup.
- Metadata parsing, conflicting claims, partial voice discovery, and source selection.
- Bounded report import, source-file preservation, comparison context, typed values, and publication collisions.
- HTML encoding, selected server settings, explicit-null limits, false/zero values, and inspection summaries.

Controlled HTTP/network fixtures make failure cases reproducible. Native loopback
checks exercise socket behavior. The suite requires no running inference server
or model files. The regression executable is separate from the product archive.

### Live integration evidence

Live checks used Tsubaki TTS Engine 1.0.10. Successful metadata discovery reported
the expected service identity, synthesis readiness, and 16 advertised voices.

| Scenario | Recorded result |
| --- | --- |
| Windows portable executable outside the repository | A copied executable ran without copied application sidecar files. Profiles and reports were accessed through absolute and relative paths, including spaces, from working directories separate from the executable. |
| Windows local speech | Run `20261113T100638Z_e6cdd844bfc4` completed with outcome `passed`, exit 0, and JSON/HTML/WAV output. Audio was mono PCM16 at 22050 Hz, 2.320136 seconds, 102362 bytes. |
| Windows local inspection | Run `20261113T101216Z_7d3d167783f5` completed with outcome `passed` and exit 0. |
| Windows offline comparison | A speech report compared with itself published JSON/HTML, identified the shared run ID, and returned exit 0. |
| Linux LAN inspection | Run `20261113T130855Z_3f2868dd6b7c` completed with outcome `passed` and exit 0, with speech and audio deliberately not requested. |
| Linux LAN speech | Run `20261113T131359Z_2dd0cac894fd` completed with outcome `passed`, exit 0, and JSON/HTML/WAV output. Audio was mono PCM16 at 22050 Hz, 2.308526 seconds, 101850 bytes. Manual report playback was accepted. |
| Linux offline comparison | An incomplete inspection and a successful inspection were compared without server requests. A repeated invocation with immediate status capture returned exit 0. |
| Linux SIGINT cancellation | Run `20261113T135126Z_e42df732d57f` cancelled an active speech request, preserved partial JSON/HTML, and returned exit 130. Execution was `cancelled`; outcome was `incomplete`. |
| Known unused local port | A refused speech connection produced `connection_refused`, failed the required request check, and returned exit 1. |
| Missing status route | An intentional HTTP 404 remained a supporting finding while speech/audio verification passed with exit 0. |
| Invalid comparison input | A profile supplied as a diagnostic report was rejected with exit 2. |
| HTML report review | Diagnostic and comparison reports were accepted at desktop and narrow widths and in A4 portrait print preview. Page breaks remain browser-dependent. |

An initial Linux LAN inspection recorded connection timeouts/failures and
published an incomplete report. Repeating inspection succeeded; the cause of
the initial connection failure was not isolated.

The Linux cancellation record used SIGINT delivered to the client during an
active request. It establishes native signal handling and partial publication.
A separate manual keyboard Ctrl+C check on Windows is not recorded here.

On Linux, automatic HTML opening through Snap/Chromium emitted desktop/browser
messages after reports were published. Diagnostic results and exit codes were
verified separately. Manual HTML playback succeeded.

Earlier manual API contract observations are recorded in the
[source verification note](https://github.com/MrHryhorii/SmartStack/blob/main/ServiceCheck/Tests/LocalIntegration.md).

## Reports, paths, and distribution

Each diagnostic publishes a new directory beneath its selected output root:

- `report.json`: diagnostic evidence without original speech input or runtime audio bytes.
- `report.html`: offline report with encoded text and optional embedded WAV playback.
- `sample.wav`: supported received audio when retention is enabled.

Inspection creates JSON and HTML. Disabling retention keeps audio metrics and
hashes while omitting WAV output and embedded playback. The retained WAV still
contains the synthesized speech when retention is enabled.

Comparison publishes `comparison.json` and `comparison.html` in a new directory.
It opens source reports read-only, contacts no server, and loads no referenced
WAV files. Historical sample references remain part of the source evidence.
Import is bounded to 8 MiB per report, JSON depth 32, and additional string and
collection limits. Unsupported schemas, unknown or duplicate members, and
inconsistent required evidence are rejected.

HTML includes its styles and needs no JavaScript or external resources.
Wide tables provide horizontal scrolling and keyboard focus. Printed section
distribution varies with the viewer and print settings.

Publication uses a separate 10-second writing budget. Existing output is
preserved; output failures report available working paths. Diagnostic publication
can preserve a cancelled result. Comparison publication also observes command
cancellation.

Portable distributions include the matching runtime in a platform-specific
executable. Compatible operating-system libraries are still required. Bundled
native runtime libraries use the .NET host's per-user extraction cache.
The verification machines' independently installed runtime state was not audited.

Profiles and input reports remain external files. Relative arguments resolve
from the shell's working directory; absolute paths remain independent of it.
Example profiles and build instructions are linked from the README.

## Limitations and deployments not verified live

- Audio verification supports little-endian RIFF/WAVE PCM16. Intelligibility,
  pronunciation quality, language correctness, and speaker identity require
  separate assessment.
- Live integration covers IPv4 loopback and an IPv4 LAN path to Tsubaki 1.0.10.
  DNS-name, IPv6, HTTPS/reverse-proxy, and bearer deployments have controlled
  regression coverage but were not verified against live deployments here.
- The client records observable access and reported metadata. It does not inspect
  remote CPU/GPU load, model files, or a particular firewall rule.
- Each active run sends at most one non-streaming speech request. The utility
  provides no speech retries or statistical load-testing workflow.
- HTTP access is direct, with normal TLS validation, automatic redirects disabled,
  and no system HTTP-proxy handling. Reverse proxies use an explicit service URL.
- Desktop report opening depends on an available viewer. Printed page breaks
  are not optimized for publication layout.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Required diagnostic checks passed, or a comparison was published; supporting diagnostic warnings are allowed. |
| `1` | A completed diagnostic run contains a failed required check. |
| `2` | Invalid arguments, profile, authentication configuration, or input report. |
| `3` | Incomplete diagnostics, internal error, or report publication failure. |
| `130` | User cancellation. |
