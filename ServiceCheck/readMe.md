# Tsubaki Service Check

A standalone .NET diagnostic utility for local and remote Tsubaki TTS services.
Inspect connectivity and reported readiness, verify a speech response, and
compare saved runs after a configuration change or server update.

The client needs no inference engine, model files, GPU libraries, or database.
It connects to an existing Tsubaki HTTP or HTTPS endpoint and records what
this client observed.

Version 0.1.0 is in development. Windows development builds and local server
checks are verified. Windows x64 and Linux x64 are the intended distribution
targets; self-contained packages and native Linux verification are pending.
See [STATUS.md](STATUS.md) for the current verification results and remaining work.

## Build and run

Building from source requires the .NET 10 SDK. Run these commands from the
repository root:

```sh
dotnet build ./ServiceCheck/Cli/Tsubaki.ServiceCheck.Cli.csproj --configuration Release
```

```sh
dotnet ./ServiceCheck/Cli/bin/Release/net10.0/TsubakiCheck.dll --help
```

The DLL launcher uses the same command arguments on Windows and Linux.
On Windows, the build also creates `TsubakiCheck.exe` in the same output directory.
The current development output requires an installed .NET 10 runtime.
Native Linux execution remains to be verified; compatible operating-system
libraries will also be required by a future self-contained Linux package.

## Quick start

### Create a target profile

```sh
dotnet ./ServiceCheck/Cli/bin/Release/net10.0/TsubakiCheck.dll init --output "./ServiceCheck/my-target.json"
```

`init` creates a new file in an existing parent directory. It preserves an
existing file at the selected path.

The generated profile targets `http://127.0.0.1:5045/` with the default Tsubaki
routes, voice `piper_base`, compatibility model label `tts-1`, WAV output,
and a short English input sentence. Edit the target URL and speech settings
before running against another deployment.

### Inspect the service

```sh
dotnet ./ServiceCheck/Cli/bin/Release/net10.0/TsubakiCheck.dll inspect --profile "./ServiceCheck/my-target.json" --output "./ServiceCheck/runs" --open
```

Inspection collects DNS/TCP observations and health, status, and voice metadata.
Its required checks are the expected service identity and server-reported
synthesis readiness. The report explicitly states that speech generation
was not tested.

### Verify speech generation

```sh
dotnet ./ServiceCheck/Cli/bin/Release/net10.0/TsubakiCheck.dll check --profile "./ServiceCheck/my-target.json" --output "./ServiceCheck/runs" --open
```

The active check collects the same service observations and sends at most one
non-streaming speech POST. Its required checks are a successful speech response,
a supported PCM16 WAV container, and nonzero samples.

Restricted metadata and an unready server claim remain visible as findings.
The active speech request is still attempted while the run remains active.

### Compare saved runs

```sh
dotnet ./ServiceCheck/Cli/bin/Release/net10.0/TsubakiCheck.dll compare --before "./ServiceCheck/runs/BEFORE_RUN_ID/report.json" --after "./ServiceCheck/runs/AFTER_RUN_ID/report.json" --output "./ServiceCheck/runs" --open
```

Replace the two run-directory placeholders with paths printed by earlier runs.
The inputs must be diagnostic `report.json` files. Their explicit before/after
order is preserved, including when their timestamps are reversed.

Comparison runs offline, opens the input files read-only, and loads no server
URLs or referenced audio files. It exports the source outcomes, check transitions,
selected field differences, and limits on what can be compared.

All diagnostic and comparison commands require an output directory argument.
The directory is created when needed. Each publication receives a unique
subdirectory; existing output is preserved. `--open` launches the newly published
HTML using the system viewer. A viewer-launch failure does not change the
result of the completed diagnostic or comparison.

## Configure a profile

Profiles are UTF-8 JSON with schema version 1. Unknown or duplicate properties,
unsupported schemas, invalid settings, and files larger than 64 KiB are rejected.
The generated profile includes all editable settings. A minimal profile is:

```json
{
  "schemaVersion": 1,
  "name": "Tsubaki local",
  "target": {
    "id": "tsubaki-local",
    "baseUrl": "http://127.0.0.1:5045/"
  }
}
```

Omitted optional settings receive the client defaults. Keep `target.id` stable
when comparing corrected configurations of the same logical target. The name
is a display label; it is independent of the speech request fingerprint.

`target.baseUrl` is the service root. For a reverse proxy, include its path
prefix, such as `https://tts.example.test/tsubaki/`. Configured routes are
relative to this root and preserve the prefix. A route must not begin with `/`
or contain an absolute URL, traversal, query, or fragment.

### Default routes

| Profile property under `routes` | Default value |
| --- | --- |
| `health` | `health` |
| `healthFallback` | `v1/health` |
| `status` | `tsbk/server/status` |
| `voices` | `tsbk/audio/voices` |
| `speech` | `v1/audio/speech` |

The health fallback is attempted only when the primary health route returns
HTTP 404 or 405. Optional status or voice-route failures do not prevent the
active speech check.

### Speech, deadlines, and retention

| Setting | Default |
| --- | --- |
| `synthesis.model` | `tts-1` |
| `synthesis.voice` | `piper_base` |
| `synthesis.responseFormat` | `wav` |
| `synthesis.stream` | `false` |
| `synthesis.speed` | `1.0` |
| `timeoutsMs.dns` | `3000` |
| `timeoutsMs.tcp` | `3000` |
| `timeoutsMs.metadata` | `5000` |
| `timeoutsMs.speech` | `60000` |
| `timeoutsMs.run` | `90000` |
| `limits.maxMetadataBytes` | `1048576` bytes |
| `limits.maxAudioBytes` | `10485760` bytes |
| `limits.maxInputCharacters` | `200` UTF-16 code units |
| `output.retainAudio` | `true` |

The active check currently requires non-streaming WAV. Speed must be finite
and between 0.25 and 4.0. Speech input must contain non-whitespace text and
fit the configured input cap. The run deadline bounds diagnostic execution
across the individual operations.

Set `output.retainAudio` to `false` to keep audio measurements and hashes
without exporting a WAV or embedding playback. Canonical JSON omits original
speech input and runtime audio bytes; it records input length and SHA-256 instead.

### Bearer authentication

In the profile, set `authentication` to:

```json
{
  "type": "bearer",
  "tokenEnvironmentVariable": "TSUBAKI_CHECK_TOKEN"
}
```

Provide the variable in the shell that launches the utility.

PowerShell:

```powershell
$env:TSUBAKI_CHECK_TOKEN = '<token>'
```

Bash:

```bash
export TSUBAKI_CHECK_TOKEN='<token>'
```

Replace `<token>` with the deployment credential. The profile stores the
variable name. Missing or invalid configured authentication is rejected before
network inspection. Resolved token values and original speech input are redacted
from recorded diagnostic text. Cookie and authentication header values are not exported.

For unauthenticated deployments, use `"authentication": { "type": "none" }`
and omit `tokenEnvironmentVariable`.

## Local and remote access

Use `127.0.0.1` when the client and server run on the same computer. From another
computer, configure the server's reachable address and port. The server must
listen on that access path, and the existing network configuration must permit it.
The client does not change server bindings or firewall rules.

HTTP requests connect directly, use normal TLS validation, and disable automatic
redirects and system HTTP-proxy handling. A reverse proxy is supported through
the configured service URL and relative route prefix. Speech requests are not retried.

An inspection can help distinguish name resolution, direct TCP, metadata access,
and server-reported readiness. These remain client observations. They do not
identify remote GPU load, model files, or a particular firewall rule.

## Read the output

| Artifact | Content |
| --- | --- |
| `report.json` | Canonical diagnostic result, evidence, settings, timings, and available audio metrics. |
| `report.html` | Offline diagnostic report with readable metadata, findings, checks, and optional playback. |
| `sample.wav` | Retained supported response when enabled. |
| `comparison.json` | Canonical comparison and both source diagnostic records. |
| `comparison.html` | Offline before/after view with context, findings, paired checks, and selected fields. |

Required failures determine a completed diagnostic failure. Supporting metadata
gaps can produce `passed_with_warnings` while speech verification succeeds.
Missing required evidence or an interrupted run can leave the outcome `incomplete`.
Deliberately omitted inspection audio checks are marked `not_requested`.

A successful speech check establishes a supported nonzero waveform. It does not
establish intelligibility, pronunciation quality, language correctness, or speaker identity.

Comparison identifies changed URLs, target IDs, modes, request fingerprints,
settings, versions, and coverage. A changed URL does not establish physical
server identity. A new audio hash is artifact evidence. Single-run durations
include the client, access path, server processing, and transfer; they are
descriptive measurements rather than isolated inference benchmarks.

HTML opens from disk with embedded styles and optional retained audio. Wide
tables have keyboard-focusable horizontal scrolling regions. A4 portrait print
preview has been reviewed; page breaks vary with the browser.

## Exit codes and cancellation

| Code | Meaning |
| --- | --- |
| `0` | Required diagnostic checks passed, or a comparison was published. Supporting diagnostic warnings are allowed. |
| `1` | A completed diagnostic run contains a failed required check. |
| `2` | Invalid arguments, profile, authentication configuration, or input report. |
| `3` | Incomplete diagnostics, unsupported required coverage, internal error, or publication failure. |
| `130` | User cancellation. |

A valid comparison returns 0 even if one of its source diagnostics failed.
Ctrl+C requests cancellation. When a diagnostic result is available, publication
uses a separate writing budget to preserve partial evidence. Comparison publication
also observes command cancellation. Interactive Ctrl+C verification remains pending.

## Regression checks

Build the executable regression project from the repository root:

```sh
dotnet build ./ServiceCheck/Tests/Tsubaki.ServiceCheck.Regression.csproj --configuration Release
```

```sh
dotnet ./ServiceCheck/Tests/bin/Release/net10.0/Tsubaki.ServiceCheck.Regression.dll
```

The checks use controlled HTTP/network fixtures and native loopback connections.
They require no running inference server or models. Current counts, recorded
integration evidence, platform verification, and known limitations are maintained
in [STATUS.md](STATUS.md).
