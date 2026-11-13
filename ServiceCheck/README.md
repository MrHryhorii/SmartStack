# Tsubaki Service Check

A standalone command-line utility for checking local and remote
[Tsubaki TTS Engine](https://github.com/MrHryhorii/SmartStack) servers.
It checks connectivity, reads server metadata, verifies a generated WAV response,
and compares saved results after a configuration change or server update.

Use it to confirm that a deployment is reachable from a particular computer,
check whether synthesis works through the configured address, or save a report
for troubleshooting. The client connects to an existing HTTP or HTTPS service;
it needs no inference engine, model files, GPU libraries, or database.

- [Walkthrough](WALKTHROUGH.md): a complete workflow with practical examples.
- [Verification and limitations](STATUS.md): tested platforms, recorded results,
  and the scope of the checks.
- [Example profiles](#example-profiles): local, LAN, and HTTPS configurations.
- [Build from source](#build-from-source): development and portable builds.

## Choose a command

| Command | Purpose | Uses the server |
| --- | --- | --- |
| `init` | Create an editable JSON profile with local defaults. | No |
| `inspect` | Check connectivity and reported identity, readiness, and voices. | Yes; no speech request |
| `check` | Inspect the service, send one speech request, and verify its audio. | Yes |
| `compare` | Compare two saved diagnostic JSON reports and publish an offline comparison. | No |

Each diagnostic command prints a summary and saves JSON and HTML reports.
The HTML works offline and includes audio playback when a supported WAV is retained.

## Get started with a portable build

Download the matching archive from the
[Service Check release](https://github.com/MrHryhorii/SmartStack/releases/tag/tsubakicheck-v0.1.0)
and extract it into a writable directory.

| Platform | Executable |
| --- | --- |
| Windows x64 | `TsubakiCheck.exe` |
| Linux x64 | `TsubakiCheck` |

Portable builds include their .NET runtime. They require no separate .NET SDK
or runtime installation. Compatible operating-system libraries are still required.
Windows x64 and Ubuntu 24.04.5 LTS x64 have been exercised; see
[STATUS.md](STATUS.md) for the verified scope.

### Windows PowerShell

Open PowerShell in the extracted directory:

```powershell
.\TsubakiCheck.exe --version
```

Create a new profile:

```powershell
.\TsubakiCheck.exe init --output ".\target.json"
```

The default address is `http://127.0.0.1:5045/`. Start the server at that address,
or edit `target.baseUrl` in `target.json` to match your deployment. Then run:

```powershell
.\TsubakiCheck.exe inspect --profile ".\target.json" --output ".\runs"
```

```powershell
.\TsubakiCheck.exe check --profile ".\target.json" --output ".\runs" --open
```

### Linux Bash

Open a terminal in the extracted directory and restore execute permission:

```bash
chmod +x ./TsubakiCheck
```

```bash
./TsubakiCheck --version
```

Create a new profile:

```bash
./TsubakiCheck init --output "./target.json"
```

Edit `target.baseUrl` before continuing if the server runs on another computer.
Loopback addresses such as `127.0.0.1` refer to the computer running this client.

```bash
./TsubakiCheck inspect --profile "./target.json" --output "./runs"
```

```bash
./TsubakiCheck check --profile "./target.json" --output "./runs" --open
```

`init` requires an existing parent directory and preserves an existing profile.
Diagnostic output directories are created when needed. Every publication receives
a unique run directory, so earlier reports are preserved.

`--open` asks the system viewer to open the new HTML report. You can also open
the printed HTML path manually. Viewer-launch errors and browser messages should
be considered separately from the utility's diagnostic result and exit code.

## Example profiles

The `Examples` directory contains complete profiles with the default routes,
speech settings, deadlines, response limits, and audio retention enabled.
Copy a profile to your own working file and edit that copy before use.

| Profile | Intended use | Setting to review |
| --- | --- | --- |
| [tsubaki.local.json](Examples/tsubaki.local.json) | Server on the same computer, without authentication. | `http://127.0.0.1:5045/` |
| [tsubaki.lan.json](Examples/tsubaki.lan.json) | Server on another computer in the local network. | Replace `http://192.168.1.25:5045/` with its reachable address. |
| [tsubaki.bearer.json](Examples/tsubaki.bearer.json) | HTTPS service or reverse proxy requiring a bearer token. | Replace `https://tts.example.test/tsubaki/` and set `TSUBAKI_CHECK_TOKEN`. |

The LAN address and HTTPS hostname are examples. The bearer profile configures
the client; it does not enable authentication on the server or proxy.

The example target IDs describe separate configurations. When two profiles refer
to the same logical deployment, give them the same `target.id` before collecting
runs you want to compare. The display name can differ. A shared ID is a configured
label, not proof of physical server identity.

## Commands and saved comparisons

```text
TsubakiCheck --help
TsubakiCheck --version
TsubakiCheck init --output <profile.json>
TsubakiCheck inspect --profile <profile.json> --output <directory> [--open]
TsubakiCheck check --profile <profile.json> --output <directory> [--open]
TsubakiCheck compare --before <report.json> --after <report.json> --output <directory> [--open]
```

The command name above represents the executable for your platform. All file
arguments are explicit; there is no automatic profile selection.

To compare runs, use the `report.json` paths printed by two earlier diagnostics.
For example, in PowerShell:

```powershell
.\TsubakiCheck.exe compare --before ".\runs\BEFORE_RUN_ID\report.json" --after ".\runs\AFTER_RUN_ID\report.json" --output ".\comparisons" --open
```

Or in Bash:

```bash
./TsubakiCheck compare --before "./runs/BEFORE_RUN_ID/report.json" --after "./runs/AFTER_RUN_ID/report.json" --output "./comparisons" --open
```

Replace `BEFORE_RUN_ID` and `AFTER_RUN_ID` with the actual directories.
The selected order is preserved even when timestamps are reversed. Comparison
opens its inputs read-only, contacts no server, and loads no referenced WAV files.
Inputs must be schema version 1 diagnostic reports, each no larger than 8 MiB;
profiles and `comparison.json` files are not diagnostic inputs.

The result shows recorded outcomes, changes in individual checks, selected
settings and server claims, voice-list differences, audio measurements, and
available timings. Differences in target, mode, request, or coverage are reported
so that unrelated observations are not presented as directly comparable results.
Comparison exit code 0 means the comparison was published, even if a source run failed.

## File paths and working directories

Relative paths are resolved from the shell's current working directory.
Absolute paths work independently of that directory. Profiles, input reports,
and output directories can be stored separately from the executable and source tree.
Quote paths containing spaces.

| Argument | Selected location |
| --- | --- |
| `init --output` | A new profile file in an existing parent directory. |
| `inspect/check --profile` | An existing external profile file. |
| `compare --before`, `--after` | Existing diagnostic JSON files. |
| `inspect/check/compare --output` | The report output root, created when needed. |

For example, with the executable under `C:\Tools\TsubakiCheck` and an existing
profile in a separate work directory:

```powershell
& 'C:\Tools\TsubakiCheck\TsubakiCheck.exe' check --profile 'C:\Work\TsubakiChecks\target.json' --output 'C:\Work\TsubakiChecks\Run reports'
```

The corresponding Bash pattern is:

```bash
"$HOME/tools/tsubaki-check/TsubakiCheck" check --profile "$HOME/tsubaki-checks/target.json" --output "$HOME/tsubaki-checks/Run reports"
```

Moving the executable does not change where absolute arguments point. Relative
output paths are independent of the profile's directory and the executable's directory.

## Configure a profile

Profiles are UTF-8 JSON with schema version 1. `init` and the examples expose
all editable settings. A minimal valid profile is:

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

Omitted optional settings receive defaults. Unknown or duplicate properties,
unsupported schemas, invalid values, and files larger than 64 KiB are rejected.
Use valid JSON without comments.

`target.baseUrl` is the HTTP or HTTPS service root. For a reverse proxy, include
its prefix, for example `https://tts.example.test/tsubaki/`. Routes are relative
to this root: `v1/audio/speech` resolves beneath that prefix. Routes must not
start with `/` or contain an absolute URL, traversal, query, or fragment.

### Default routes

| Property under `routes` | Default value |
| --- | --- |
| `health` | `health` |
| `healthFallback` | `v1/health` |
| `status` | `tsbk/server/status` |
| `voices` | `tsbk/audio/voices` |
| `speech` | `v1/audio/speech` |

The health fallback is attempted only when the primary health route returns
HTTP 404 or 405. Status and voice-list failures remain visible and do not prevent
an active speech check while the run remains active.

### Speech, deadlines, and retention

| Setting | Default |
| --- | --- |
| `synthesis.model` | `tts-1` |
| `synthesis.voice` | `piper_base` |
| `synthesis.input` | `This is a Tsubaki service check.` |
| `synthesis.responseFormat` | `wav` |
| `synthesis.stream` | `false` |
| `synthesis.speed` | `1.0` |
| `timeoutsMs.dns` | `3000` ms |
| `timeoutsMs.tcp` | `3000` ms |
| `timeoutsMs.metadata` | `5000` ms |
| `timeoutsMs.speech` | `60000` ms |
| `timeoutsMs.run` | `90000` ms |
| `limits.maxMetadataBytes` | `1048576` bytes |
| `limits.maxAudioBytes` | `10485760` bytes |
| `limits.maxInputCharacters` | `200` UTF-16 code units |
| `output.retainAudio` | `true` |

Speech verification supports non-streaming PCM16 WAV. Speed must be finite and
between 0.25 and 4.0. Input must contain non-whitespace text and fit its configured cap.
Deadlines are independent; the run deadline bounds diagnostic execution across
the individual operations. Each configured timeout must be 1 to 900000 ms.
Maximum configurable limits are 4 MiB for metadata, 64 MiB for audio, and
10000 UTF-16 code units for speech input.

Set `output.retainAudio` to `false` to keep measurements and hashes without
exporting a WAV or embedding playback. Diagnostic JSON omits the original input
text and runtime audio bytes; it records input length and SHA-256 instead.
Retained audio contains the synthesized speech and can be played from the HTML report.

### Bearer authentication

Use the bearer example or set this profile section:

```json
{
  "type": "bearer",
  "tokenEnvironmentVariable": "TSUBAKI_CHECK_TOKEN"
}
```

Set the variable in the shell that launches the utility. In PowerShell:

```powershell
$env:TSUBAKI_CHECK_TOKEN = '<token>'
```

Or in Bash:

```bash
export TSUBAKI_CHECK_TOKEN='<token>'
```

Replace `<token>` with the deployment credential. The profile stores the variable
name, not its value. Missing or invalid configured authentication is rejected
before network inspection. Resolved token values and original speech input are
redacted from recorded diagnostic text. Cookie and authentication header values
are not exported.

For unauthenticated deployments, use `"authentication": { "type": "none" }`
and omit `tokenEnvironmentVariable`.

## Network behavior and result scope

For access from another computer, the server must listen on a reachable address
and port, and the network must permit the connection. The utility does not change
server bindings or firewall rules.

The client records DNS and direct TCP observations separately from HTTP results.
IP literals skip DNS lookup. A TCP connection establishes port reachability;
service identity and readiness come from recognized metadata. HTTP requests use
normal TLS validation, no automatic redirects, and no system HTTP-proxy handling.
An explicitly configured reverse proxy URL is supported. Speech requests are not retried.

In `inspect`, expected identity and server-reported readiness are required.
Speech generation is not tested. In `check`, a successful speech response,
a supported PCM16 WAV container, and nonzero samples are required. Metadata
failures can remain supporting warnings while the speech check succeeds.

A successful speech check establishes a supported nonzero waveform. It does not
establish intelligibility, pronunciation quality, language correctness, or speaker
identity. Server metadata records claims rather than independently verified
inference configuration. Durations include the client, network, server, and
transfer; individual runs are not statistical benchmarks or isolated inference timings.

## Reports and exit codes

| Artifact | Content |
| --- | --- |
| `report.json` | Diagnostic evidence, effective settings, timings, and available audio metrics. |
| `report.html` | Offline diagnostic report with metadata, findings, checks, and optional playback. |
| `sample.wav` | Optional retained supported WAV response. |
| `comparison.json` | Comparison result and both source diagnostic records. |
| `comparison.html` | Offline before/after view with context, findings, checks, and selected fields. |

Reports distinguish `passed`, `passed_with_warnings`, `failed`, and `incomplete`.
Required failures determine completed diagnostic failure; missing required
evidence or an interrupted run can leave diagnostics incomplete. Deliberately
omitted inspection audio checks are marked `not_requested`.

HTML uses embedded styles and optional audio. Wide tables have horizontal
scrolling regions. Browser print preview can be used for paper or PDF output;
page breaks vary with the browser.

| Exit code | Meaning |
| --- | --- |
| `0` | Required diagnostic checks passed, or a comparison was published. Supporting diagnostic warnings are allowed. |
| `1` | A completed diagnostic run contains a failed required check. |
| `2` | Invalid arguments, profile, authentication configuration, or input report. |
| `3` | Incomplete diagnostics, internal error, or report publication failure. |
| `130` | User cancellation. |

Read the process exit code immediately after the command: `$LASTEXITCODE` in
PowerShell or `$?` in Bash. Subsequent commands can replace it.

Ctrl+C requests cancellation. When a diagnostic result is available, publication
uses a separate writing budget to preserve partial evidence. Cancellation before
a result is available may produce no report. Comparison publication also observes
command cancellation.

## Build from source

Building requires the .NET 10 SDK. Run source commands from the repository root.

### Development build

```sh
dotnet build ./ServiceCheck/Cli/Tsubaki.ServiceCheck.Cli.csproj --configuration Release
```

```sh
dotnet ./ServiceCheck/Cli/bin/Release/net10.0/TsubakiCheck.dll --help
```

The DLL launcher accepts the same arguments on Windows and Linux and requires
an installed .NET 10 runtime. On Windows, the build also creates `TsubakiCheck.exe`
in the same output directory.

### Portable Windows x64

```sh
dotnet publish ./ServiceCheck/Cli/Tsubaki.ServiceCheck.Cli.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded --output ./ServiceCheck/Cli/bin/Release/portable/win-x64
```

Executable: `ServiceCheck/Cli/bin/Release/portable/win-x64/TsubakiCheck.exe`.

### Portable Linux x64

```sh
dotnet publish ./ServiceCheck/Cli/Tsubaki.ServiceCheck.Cli.csproj --configuration Release --runtime linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded --output ./ServiceCheck/Cli/bin/Release/portable/linux-x64
```

Executable: `ServiceCheck/Cli/bin/Release/portable/linux-x64/TsubakiCheck`.

These are platform-specific self-contained single-file builds. Trimming is
disabled for reflection-based JSON serialization; debug symbols are embedded.
Bundled native runtime libraries are extracted into the .NET host's per-user
cache at startup. Profiles and reports remain external files selected by arguments.
The target operating system still needs compatible native libraries.

To assemble a portable archive, place the matching executable, `README.md`,
`WALKTHROUGH.md`, `STATUS.md`, and the complete `Examples` directory together.
Source files, models, generated run reports, and the regression executable are
not needed in the product archive. Restore execute permission on Linux if the
archive format or transfer did not preserve it.

For SDK option details, see Microsoft's
[single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
and [`dotnet publish` reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish).

### Regression checks

```sh
dotnet build ./ServiceCheck/Tests/Tsubaki.ServiceCheck.Regression.csproj --configuration Release
```

```sh
dotnet ./ServiceCheck/Tests/bin/Release/net10.0/Tsubaki.ServiceCheck.Regression.dll
```

The executable checks use controlled HTTP/network fixtures, generated temporary
files, and native loopback connections. They require no running Tsubaki server
or model files. All 337 checks passed on Windows x64 and native Linux x64;
live service checks and their limits are documented in [STATUS.md](STATUS.md).
