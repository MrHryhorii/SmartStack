# Practical walkthrough

This guide takes a target from its first profile to a saved speech check and
an offline before-and-after comparison. Additional scenarios cover profile
settings, incomplete metadata, audio retention, authentication, cancellation,
and access from another computer.

Use [README.md](readMe.md) for the command reference and defaults, and
[STATUS.md](STATUS.md) for verified platforms and outstanding work.

Sections 1 through 7 form the main workflow. The remaining sections are
additional scenarios; use the ones relevant to your deployment.

- [Build and create a profile](#1-build-and-identify-the-utility)
- [Inspect the service and verify speech](#3-inspect-connectivity-and-reported-readiness)
- [Record a failure and compare the correction](#5-record-an-unreachable-endpoint)
- [Try speech settings and retention](#8-change-speech-settings-in-a-separate-profile)
- [Explore metadata and invalid inputs](#10-observe-a-supporting-metadata-failure)
- [Use authentication and another computer](#12-use-bearer-authentication-when-required)
- [Configure bounds and cancellation](#14-configure-bounds-and-request-cancellation)
- [Run the regression checks](#15-run-the-regression-checks-without-an-inference-server)
- [Use Bash](#bash-command-route)
- [Interpret exit codes](#exit-codes-at-a-glance)

## Before starting

The main examples use PowerShell from the repository root and an existing
Tsubaki server at `http://127.0.0.1:5045/`. The server should have completed
startup and support the configured `piper_base` voice. Change the profile when
your deployment uses another address or voice.

Building requires the .NET 10 SDK. The development DLL requires an installed
.NET 10 runtime. The client needs no models or inference libraries.

Run one command block at a time. Read its result before proceeding. Keep the
same PowerShell session so that the path variables remain available. Check
`$LASTEXITCODE` immediately after a CLI invocation, before running another
native program. A Bash route is included at the end.

The guide creates a new working directory beneath `ServiceCheck/runs`.
Existing profiles and reports can remain where they are.

## 1. Build and identify the utility

```powershell
dotnet build ./ServiceCheck/Cli/Tsubaki.ServiceCheck.Cli.csproj --configuration Release
$LASTEXITCODE
```

Continue after a successful build and exit code `0`.

```powershell
$cliDll = (Resolve-Path -LiteralPath './ServiceCheck/Cli/bin/Release/net10.0/TsubakiCheck.dll').Path
dotnet $cliDll --help
```

The help lists `init`, `inspect`, `check`, and `compare`, their arguments, and
the process exit codes. `-h` also shows help.

```powershell
dotnet $cliDll --version
```

Record the version when collecting evidence for a deployment change. A
development version can include the source revision used to build the client.

## 2. Create a working directory and profile

```powershell
$walkthroughRoot = Join-Path './ServiceCheck/runs' ('walkthrough-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $walkthroughRoot -ErrorAction Stop | Out-Null
$profilePath = Join-Path $walkthroughRoot 'target.json'
$outputRoot = Join-Path $walkthroughRoot 'results'
Write-Output $walkthroughRoot
```

Keep the printed directory path if you want to revisit these files later.

```powershell
dotnet $cliDll init --output $profilePath
$LASTEXITCODE
```

Expected: `Profile created: ...` and exit code `0`.

```powershell
Get-Content -LiteralPath $profilePath
```

The generated JSON includes the target, routes, authentication, synthesis,
timeouts, response limits, and audio retention settings.

If needed, edit `target.baseUrl` in this file before continuing. It is the
service root, such as `http://127.0.0.1:5045/`, rather than a complete speech
endpoint. Keep `target.id` as the stable identifier of your logical target.

`init` requires an existing parent directory. Repeating it against the same
file preserves that file and returns `3`; there is no overwrite option.

## 3. Inspect connectivity and reported readiness

```powershell
dotnet $cliDll inspect --profile $profilePath --output $outputRoot --open
$LASTEXITCODE
```

For a ready Tsubaki deployment with accessible metadata, expect:

| Observation | Expected interpretation |
| --- | --- |
| `dns.resolve` is skipped with `ip_literal` | The URL already contains an IP address; a DNS lookup is unnecessary. |
| `tcp.connect` passes | A separate direct connection to the configured address and port succeeded. |
| Health, status, and voice checks pass | The corresponding metadata routes returned recognized evidence. |
| `service.identity` passes | Metadata reported the expected Tsubaki service identifier. |
| `service.readiness` passes | The server reported synthesis readiness. |
| Speech and audio checks are skipped with `not_requested` | Inspection sends no synthesis request. |

The required inspection checks are identity and reported readiness. When they
pass, the exit code is `0`, including when supporting observations have warnings.
If a required claim is unavailable or unsuccessful, read the finding before
proceeding; an inspection has not established that speech works.

The command prints paths to `report.json` and `report.html`. `--open` asks the
system viewer to open this newly published HTML file. If automatic opening is
unavailable, open the printed HTML path manually. This does not change the
diagnostic result.

In the HTML, examine the checks and reported server metadata. Identity,
readiness, advertised voices, and selected defaults retain their source routes.
Unavailable fields remain unknown. A reported `false`, `0`, or explicit null
can have a different meaning from a missing field.

No WAV is produced by inspection. A ready claim is a useful observation, not
a synthesis test.

## 4. Verify and listen to a speech response

```powershell
dotnet $cliDll check --profile $profilePath --output $outputRoot --open
$LASTEXITCODE
```

For a working endpoint, expect the required checks `speech.request`,
`audio.container`, and `audio.signal` to pass. The result is `passed` or
`passed_with_warnings`, with exit code `0`.

The command collects the service observations and sends at most one
non-streaming speech POST while the run remains active. Speech requests are
not retried. Supporting metadata failures do not, by themselves, block this
request.

With the default retention setting, the new run directory contains:

| File | Use |
| --- | --- |
| `report.json` | Read the canonical diagnostic data or use it as a comparison input. |
| `report.html` | Review the run offline and play the embedded supported WAV. |
| `sample.wav` | Open the retained response in an audio player. |

Listen to the response in the HTML report. The automated checks establish a
supported PCM16 WAV container with nonzero samples. Listening lets you assess
pronunciation and content, which the utility does not automatically verify.

The HTML includes its styles and retained audio, so it can be opened offline.
Wide tables scroll horizontally; their scrolling regions can receive keyboard
focus. Use the browser's print preview to print or save a PDF. Page breaks
depend on the browser and its print settings.

Each run gets its own directory. Repeating a command preserves earlier runs.

### Read the same evidence as JSON

At this prompt, paste only the full path from the speech check's `JSON:` line,
without the prefix or surrounding quotes:

```powershell
$baselineReportPath = Read-Host 'Paste the full JSON path from the successful speech check'
$baselineReport = Get-Content -LiteralPath $baselineReportPath -Raw | ConvertFrom-Json
$baselineReport | Select-Object runId, mode, executionState, outcome
```

```powershell
$baselineReport.checks | Select-Object id, role, status, code | Format-Table -AutoSize
```

```powershell
$baselineReport.network | ConvertTo-Json -Depth 6
```

```powershell
$baselineReport.audio.inspection | ConvertTo-Json -Depth 6
```

The audio evidence includes available format information, sample rate,
channels, duration, signal measurements, payload length, and SHA-256.
Use the observed values; payload bytes, timings, and hashes can vary between
successful requests.

Canonical JSON omits the original synthesis input and runtime audio bytes.
The local profile still contains the input, and retained audio still speaks
it. Disabling retention removes playback and WAV export, as shown below.

## 5. Record an unreachable endpoint

This scenario changes a separate profile to a local port with no listener.
The server used above can keep running.

The example assumes port `5046` is unused on this computer. Choose another
known unused local port if an application already listens there. If your
earlier target was remote, this scenario still deliberately uses local loopback.

```powershell
$failedProfilePath = Join-Path $walkthroughRoot 'unreachable.json'
$failedProfile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$failedProfile.name = 'Tsubaki unreachable endpoint'
$failedProfile.target.baseUrl = 'http://127.0.0.1:5046/'
$failedProfile | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $failedProfilePath -Encoding UTF8
```

```powershell
dotnet $cliDll check --profile $failedProfilePath --output $outputRoot --open
$LASTEXITCODE
```

When the connection is refused, expect a failed required `speech.request`
with `connection_refused`, skipped dependent audio checks, and exit code `1`.
The report records the unsuccessful request rather than claiming a synthesis
result.

A deadline or cancellation can instead produce an interrupted run and a
different exit code. Read what actually happened. If the request succeeds,
the selected port was not an unreachable endpoint.

```powershell
$beforeReportPath = Read-Host 'Paste the full JSON path from the unsuccessful endpoint check'
```

Keep the target ID, synthesis text, voice, model, speed, and speech route
unchanged. This makes the intended routing correction clear in the comparison.

## 6. Check the working endpoint again

The original profile still contains the working URL:

```powershell
dotnet $cliDll check --profile $profilePath --output $outputRoot --open
$LASTEXITCODE
```

After a successful speech check and exit code `0`:

```powershell
$afterReportPath = Read-Host 'Paste the full JSON path from the new successful speech check'
```

You now have an unsuccessful endpoint observation followed by a successful
observation under the same logical target and speech request settings.

## 7. Compare the saved runs offline

```powershell
dotnet $cliDll compare --before $beforeReportPath --after $afterReportPath --output $outputRoot --open
$LASTEXITCODE
```

Expected: `Offline comparison published.`, paths to `comparison.json` and
`comparison.html`, and exit code `0`. Successful publication returns `0` even
when a source diagnostic failed.

The comparison makes no server requests and loads no referenced WAV files.
The server can be unavailable during this operation.

In the HTML report, review:

- The recorded source outcomes and execution states.
- The configured target, URL, mode, profile, and speech request context.
- Transitions such as a failed speech request becoming successful.
- Supporting coverage changes and unavailable observations.
- Selected server defaults, advertised voice lists, timings, and audio evidence.

A changed URL is explicitly identified. A shared target ID describes the
configured logical target; it does not prove that two URLs reach the same
physical server. Profile settings can differ while the speech request
fingerprint remains the same.

`--before` and `--after` determine the order. Timestamps do not reorder inputs.
Different modes or speech settings can limit comparison, and the report says
so. Missing historical evidence is not treated as a failed check or filled in
from current defaults.

A different audio hash does not establish a quality regression. Individual
run durations include client work, the access path, server processing, and
transfer. They are descriptive observations rather than statistical benchmarks
or isolated model inference timings.

Comparison produces no new WAV and does not modify its source reports.

## 8. Change speech settings in a separate profile

```powershell
$customProfilePath = Join-Path $walkthroughRoot 'custom-speech.json'
$customProfile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$customProfile.name = 'Tsubaki custom speech'
$customProfile.synthesis.input = 'The service returned a complete audio response.'
$customProfile.synthesis.speed = 1.1
$customProfile | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $customProfilePath -Encoding UTF8
```

```powershell
dotnet $cliDll check --profile $customProfilePath --output $outputRoot --open
$LASTEXITCODE
```

Choose a different `synthesis.voice` only when the deployment supports it.
The advertised voice list is useful evidence; an incomplete list does not
prove that an absent ID is unavailable. `synthesis.model` is the request's
model label, not a local model path.

The current check supports `responseFormat: "wav"`, `stream: false`, and a
finite speed from `0.25` through `4.0`. Other formats and streaming are rejected
as profile settings rather than silently changing this diagnostic contract.

Comparing this run against the earlier one will identify the changed speech
request. Such results are side-by-side observations, not an equivalent-input
performance comparison.

## 9. Keep metrics without retaining audio

```powershell
$metricsProfilePath = Join-Path $walkthroughRoot 'metrics-only.json'
$metricsProfile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$metricsProfile.name = 'Tsubaki metrics only'
$metricsProfile.output.retainAudio = $false
$metricsProfile | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $metricsProfilePath -Encoding UTF8
```

```powershell
dotnet $cliDll check --profile $metricsProfilePath --output $outputRoot --open
$LASTEXITCODE
```

The utility still receives and inspects the response. A successful run keeps
the audio metrics and payload hash in its JSON and HTML, but exports no
`sample.wav` and embeds no audio player. This setting does not remove files
from earlier run directories.

## 10. Observe a supporting metadata failure

Use a separate profile with a deliberately missing status route. The example
assumes the deployment returns HTTP 404 at this unused route.

```powershell
$metadataProfilePath = Join-Path $walkthroughRoot 'missing-status.json'
$metadataProfile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$metadataProfile.name = 'Tsubaki missing status route'
$metadataProfile.routes.status = 'servicecheck/intentional-missing-status'
$metadataProfile | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $metadataProfilePath -Encoding UTF8
```

```powershell
dotnet $cliDll check --profile $metadataProfilePath --output $outputRoot --open
$LASTEXITCODE
```

If speech verification succeeds, expect `service.status` to report
`http_not_found`, an aggregate outcome of `passed_with_warnings`, and exit
code `0`. The status failure remains visible; it does not become a required
speech failure.

The health fallback is separate behavior: the client requests
`routes.healthFallback` only after the primary health route returns HTTP 404
or 405. It does not use the fallback to conceal every kind of health failure.

## 11. Recognize invalid inputs before network work

Create an input-cap violation in another profile:

```powershell
$invalidProfilePath = Join-Path $walkthroughRoot 'invalid-input.json'
$invalidProfile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$invalidProfile.limits.maxInputCharacters = 1
$invalidProfile | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $invalidProfilePath -Encoding UTF8
```

```powershell
dotnet $cliDll check --profile $invalidProfilePath --output $outputRoot
$LASTEXITCODE
```

Expected: profile validation fails with exit code `2`, before diagnostics
start. No diagnostic run is published. Restore the cap in this copy before
using it for a real request.

Profiles also reject malformed JSON, unknown or duplicate properties,
unsupported schema versions, invalid URLs/routes, and files above 64 KiB.
Input length is measured in UTF-16 code units.

The comparison command accepts diagnostic reports, not target profiles:

```powershell
dotnet $cliDll compare --before $profilePath --after $afterReportPath --output $outputRoot
$LASTEXITCODE
```

Expected: the before input is rejected with exit code `2`. Existing files
remain available. Each imported diagnostic report must use the supported
version 1 schema and fit the 8 MiB input limit.

## 12. Use bearer authentication when required

This scenario needs a deployment that accepts a bearer token. An ordinary
unauthenticated local server does not demonstrate authentication enforcement.

Create a copy with an environment-variable reference. Set its target URL and
routes to the authenticated deployment when they differ from the original.

```powershell
$bearerProfilePath = Join-Path $walkthroughRoot 'bearer.json'
$bearerProfile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$bearerProfile.name = 'Tsubaki authenticated target'
$bearerProfile.authentication = [pscustomobject]@{
    type = 'bearer'
    tokenEnvironmentVariable = 'TSUBAKI_CHECK_TOKEN'
}
$bearerProfile | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $bearerProfilePath -Encoding UTF8
```

If the variable is already configured in this shell, reuse it. Otherwise,
enter the deployment token without placing its value in the command text:

```powershell
$secureToken = Read-Host 'Bearer token' -AsSecureString
$env:TSUBAKI_CHECK_TOKEN = [System.Net.NetworkCredential]::new('', $secureToken).Password
$secureToken.Dispose()
```

```powershell
dotnet $cliDll check --profile $bearerProfilePath --output $outputRoot --open
$LASTEXITCODE
```

The process reads the named environment variable and adds the bearer header.
A missing, empty, or invalid token configuration returns `2` before network
inspection. A credential that the server rejects is different: an HTTP 401
or 403 is recorded as an access failure in the diagnostic evidence.

Resolved token values, original input text, and authentication/cookie header
values are excluded or redacted from exports. The profile stores the variable
name rather than the token.

If you created the shell variable solely for this scenario, remove it when
finished:

```powershell
Remove-Item -LiteralPath Env:TSUBAKI_CHECK_TOKEN
```

## 13. Inspect a hostname or another computer

To include DNS observations for the local server, create another profile
copy and use `http://localhost:5045/` as its base URL, then run `inspect` with
that copy. This assumes the same local server and port as the main example.

A hostname is resolved before the direct TCP probe. Depending on the local
resolver and server binding, the evidence can include IPv4 and IPv6 addresses
and unsuccessful connection attempts before a successful one. The HTTP
request uses its own connection; a TCP probe is supporting evidence.

For a second computer on the same network, replace loopback with the server's
actual reachable address, for example `http://192.168.1.25:5045/`. The example
address must be replaced with your server's address. Run `inspect` first and
then `check` with that profile.

`127.0.0.1` always refers to the computer running the client. The server must
listen on a reachable interface, and the existing network configuration must
allow access. The utility does not change server bindings or firewall rules.

For an HTTPS reverse proxy, a root such as
`https://tts.example.test/tsubaki/` keeps its path prefix. With the default
speech route, the request goes to
`https://tts.example.test/tsubaki/v1/audio/speech`.
Use your deployment's actual URL. Routes stay relative, without a leading `/`.

HTTP access is direct, with normal certificate validation. Automatic redirects,
cookies, and system HTTP-proxy handling are disabled. There is no certificate
validation bypass option.

Native Linux and cross-machine verification are pending; see
[STATUS.md](STATUS.md) for current evidence. The client and server can be on
different operating systems.

## 14. Configure bounds and request cancellation

Edit these groups in a profile copy when adapting the check to a deployment:

| Group | Effect |
| --- | --- |
| `timeoutsMs.dns` and `timeoutsMs.tcp` | Bound the corresponding network inspection stages. |
| `timeoutsMs.metadata` | Bounds a metadata operation, including body receipt. |
| `timeoutsMs.speech` | Bounds the speech request, including body receipt. |
| `timeoutsMs.run` | Bounds overall diagnostic execution across stages. |
| `limits.maxMetadataBytes` | Bounds a metadata response body. |
| `limits.maxAudioBytes` | Bounds the received audio payload. |
| `limits.maxInputCharacters` | Rejects an oversized synthesis input before network work. |

The run deadline can be shorter than individual operation deadlines. Required
evidence that could not be collected remains incomplete. A response exceeding
a body limit is rejected rather than saved as a verified sample.

To request cancellation, start a normal check and press Ctrl+C while it is
still running. Expected cancellation exit code: `130`. A fast local request
may finish before you press the key; that completed run is not a cancellation
test.

When a diagnostic result is available, publication uses a separate writing
budget so that partial evidence can be saved after cancellation or a deadline.
If cancellation occurs before a result exists, no diagnostic report may be
available. Publication errors can also prevent a complete artifact set.
Comparison cancellation is observed during loading, comparison, and publication.

Interactive Ctrl+C verification remains outstanding. Automated cancellation
and deadline coverage is recorded in [STATUS.md](STATUS.md).

## 15. Run the regression checks without an inference server

```powershell
dotnet build ./ServiceCheck/Tests/Tsubaki.ServiceCheck.Regression.csproj --configuration Release
$LASTEXITCODE
```

After a successful build:

```powershell
dotnet ./ServiceCheck/Tests/bin/Release/net10.0/Tsubaki.ServiceCheck.Regression.dll
$LASTEXITCODE
```

Expected: all checks pass, the summary reports zero failures, and the process
returns `0`. The suite uses controlled HTTP/network fixtures and native
loopback connections. It needs no running inference server or model files.
See [STATUS.md](STATUS.md) for the latest verified count.

## Bash command route

The main walkthrough uses PowerShell variables and JSON editing commands.
Do not paste those commands into Bash. The following route uses the same
CLI arguments, with Bash syntax and manual profile editing.

With the .NET 10 SDK installed, build from the repository root:

```bash
dotnet build ./ServiceCheck/Cli/Tsubaki.ServiceCheck.Cli.csproj --configuration Release
echo "$?"
```

After a successful build, prepare a new workspace:

```bash
cli_dll='./ServiceCheck/Cli/bin/Release/net10.0/TsubakiCheck.dll'
mkdir -p './ServiceCheck/runs'
walkthrough_root=$(mktemp -d './ServiceCheck/runs/walkthrough.XXXXXXXX')
profile_path="$walkthrough_root/target.json"
output_root="$walkthrough_root/results"
printf '%s\n' "$walkthrough_root"
```

```bash
dotnet "$cli_dll" --help
```

```bash
dotnet "$cli_dll" --version
```

```bash
dotnet "$cli_dll" init --output "$profile_path"
echo "$?"
```

Edit the JSON with your text editor. If the server is on another computer,
use its reachable address rather than `127.0.0.1`.

```bash
dotnet "$cli_dll" inspect --profile "$profile_path" --output "$output_root" --open
echo "$?"
```

```bash
dotnet "$cli_dll" check --profile "$profile_path" --output "$output_root" --open
echo "$?"
```

To try the other scenarios, copy the generated profile to another filename,
edit the setting described in that scenario, and pass its path to the CLI.
For a comparison, read the two full diagnostic JSON paths printed by those
runs:

```bash
read -r -p 'Before report JSON path: ' before_report_path
read -r -p 'After report JSON path: ' after_report_path
```

```bash
dotnet "$cli_dll" compare --before "$before_report_path" --after "$after_report_path" --output "$output_root" --open
echo "$?"
```

For a bearer profile referencing `TSUBAKI_CHECK_TOKEN`, set the variable in
the shell that launches the command. This prompt avoids putting the value
in the command text:

```bash
read -r -s -p 'Bearer token: ' TSUBAKI_CHECK_TOKEN
printf '\n'
export TSUBAKI_CHECK_TOKEN
```

After the authenticated scenario, remove the variable if you set it solely
for that scenario:

```bash
unset TSUBAKI_CHECK_TOKEN
```

Keep `echo "$?"` immediately after the CLI command whose status you want to
read. The same exit-code meanings apply. Automatic HTML opening requires an
available system viewer; otherwise, use the printed HTML path.

## Exit codes at a glance

| Code | Meaning | Example in this guide |
| --- | --- | --- |
| `0` | Required diagnostic checks passed, or a comparison was published. Supporting warnings are allowed. | Successful speech, a supporting status-route warning, or a valid comparison. |
| `1` | A completed diagnostic run contains a failed required check. | A refused speech connection. |
| `2` | Invalid arguments, profile, authentication configuration, or input report. | Input above the configured cap, or a profile supplied as a report. |
| `3` | Incomplete diagnostics, unsupported required coverage, internal error, or publication failure. | A run deadline, an unsupported audio encoding, or an unwritable output destination. |
| `130` | User cancellation. | Ctrl+C handled while a command is active. |

Start with the reported execution state, then the required checks and their
codes. A failed supporting check and a failed required check have different
effects on the aggregate result. Preserve the JSON with the HTML when recording
a deployment change; JSON is the input for future offline comparisons.
