# Tsubaki Service Check walkthrough

Tsubaki Service Check diagnoses an existing Tsubaki TTS server from the computer
running the client. This guide covers creating a profile, inspecting the service,
verifying generated audio, reading reports, and comparing saved runs offline.
Additional scenarios cover the example profiles, authentication, response limits,
supporting failures, invalid inputs, and cancellation.

Use [README.md](README.md) for the command reference, defaults, and build commands.
Use [STATUS.md](STATUS.md) for verified platforms, recorded results, and limitations.

## Before starting

Download the Windows x64 or Linux x64 archive from the
[Service Check release](https://github.com/MrHryhorii/SmartStack/releases/tag/tsubakicheck-v0.1.0)
and extract it. Portable builds include the .NET runtime; the client needs no
models, inference libraries, or SDK. Building from source is described in the README.

The live examples need a running Tsubaki server. The default profile uses
`http://127.0.0.1:5045/` and voice `piper_base`. If the server is elsewhere,
configure its reachable address and a supported voice before running diagnostics.
Client and server can use different operating systems.

Sections 1 through 7 form the main workflow. In each section, use the block
for your shell and run one block at a time. Keep the same terminal session so
that its path variables remain available. Quoted paths support directory names
containing spaces.

Read exit codes immediately after the command. The Bash blocks group the CLI
invocation with its exit-code print so that subsequent shell input does not
replace the result. PowerShell uses `$LASTEXITCODE` for native processes.

## 1. Identify the portable executable

Open your terminal in the extracted archive directory.

PowerShell:

```powershell
$packageDirectory = (Get-Location).Path
$tsubakiExecutable = (Resolve-Path -LiteralPath '.\TsubakiCheck.exe').Path
& $tsubakiExecutable --help
$LASTEXITCODE
```

Bash:

```bash
package_directory="$(pwd -P)"
tsubaki_executable="$package_directory/TsubakiCheck"
chmod +x "$tsubaki_executable"
{
    "$tsubaki_executable" --help
    printf 'Exit code: %s\n' "$?"
}
```

The help lists `init`, `inspect`, `check`, and `compare`, their options, and
the exit codes. `-h` also shows help. Record the build version when collecting
evidence for a deployment change:

PowerShell:

```powershell
& $tsubakiExecutable --version
$LASTEXITCODE
```

Bash:

```bash
{
    "$tsubaki_executable" --version
    printf 'Exit code: %s\n' "$?"
}
```

## 2. Create a separate working directory

The executable can stay in its tools directory while profiles and reports live
elsewhere. The following commands create a new directory under your user directory
and make it the current working directory.

PowerShell:

```powershell
$walkthroughRoot = Join-Path $env:USERPROFILE ('Tsubaki checks\walkthrough-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $walkthroughRoot -Force -ErrorAction Stop | Out-Null
$profilePath = Join-Path $walkthroughRoot 'target.json'
$outputRoot = Join-Path $walkthroughRoot 'Run reports'
Set-Location -LiteralPath $walkthroughRoot
Write-Output $walkthroughRoot
```

Bash:

```bash
mkdir -p "$HOME/Tsubaki checks"
walkthrough_root="$(mktemp -d "$HOME/Tsubaki checks/walkthrough.XXXXXXXX")"
profile_path="$walkthrough_root/target.json"
output_root="$walkthrough_root/Run reports"
cd "$walkthrough_root"
printf '%s\n' "$walkthrough_root"
```

Keep the printed directory path. Reports remain there after you close the terminal.
The executable path captured in section 1 still works from this new location.
Absolute arguments select the same files from any working directory; relative
arguments are resolved from the shell's current directory.

## 3. Create and configure a profile

PowerShell:

```powershell
& $tsubakiExecutable init --output $profilePath
$LASTEXITCODE
```

Bash:

```bash
{
    "$tsubaki_executable" init --output "$profile_path"
    printf 'Exit code: %s\n' "$?"
}
```

Expected: `Profile created: ...` and exit code `0`. `init` requires an existing
parent directory. Repeating it against the same file preserves the file and
returns `3`; there is no overwrite option.

Open `target.json` in your text editor and review these fields:

| Field | What to configure |
| --- | --- |
| `name` | A readable label for this profile. |
| `target.id` | A stable identifier for the logical deployment. Keep it consistent between runs you want to compare. |
| `target.baseUrl` | The HTTP or HTTPS service root, including a reverse proxy prefix when used. |
| `synthesis.voice` | A voice supported by the server; the default is `piper_base`. |
| `authentication` | `none`, or a bearer environment-variable reference for an authenticated deployment. |

For a local server at the default address, the generated profile can be used
unchanged. For another computer, replace loopback with the server's reachable
address. `127.0.0.1` refers to the computer running the client.

The file includes routes, speech input, deadlines, size limits, and audio retention.
Keep valid JSON without comments. Settings use camelCase; for example, the profile
property is `responseFormat`, even though the HTTP request uses `response_format`.

## 4. Inspect connectivity and reported readiness

PowerShell:

```powershell
& $tsubakiExecutable inspect --profile $profilePath --output $outputRoot --open
$LASTEXITCODE
```

Bash:

```bash
{
    "$tsubaki_executable" inspect --profile "$profile_path" --output "$output_root" --open
    printf 'Exit code: %s\n' "$?"
}
```

For a ready Tsubaki deployment with accessible metadata, expect:

| Observation | Interpretation |
| --- | --- |
| `dns.resolve` skipped with `ip_literal` | The URL already contains an IP address. |
| `tcp.connect` passed | A separate direct connection to the configured port succeeded. |
| Health, status, and voice checks passed | The corresponding routes returned recognized evidence. |
| `service.identity` passed | Metadata reported the expected Tsubaki service identifier. |
| `service.readiness` passed | The server reported synthesis readiness. |
| Speech and audio checks skipped with `not_requested` | Inspection did not request synthesis. |

Expected identity and reported readiness are required for inspection. When those
checks pass, the exit code is `0`; supporting warnings are allowed. If a required
claim is missing or unsuccessful, review the finding and the aggregate result.

The command prints the JSON and HTML paths. `--open` asks the system viewer to
open that new HTML report. If opening fails, use the printed path manually.
Browser-launch messages are separate from the diagnostic evidence and exit code.

The HTML shows selected identity claims, readiness, advertised voices, and reported
defaults with their source routes. Missing information remains unknown; a reported
`false`, `0`, or explicit null can have a different meaning from an absent field.
Inspection produces no WAV and does not establish that speech generation works.

## 5. Verify a speech response and read its evidence

PowerShell:

```powershell
& $tsubakiExecutable check --profile $profilePath --output $outputRoot --open
$LASTEXITCODE
```

Bash:

```bash
{
    "$tsubaki_executable" check --profile "$profile_path" --output "$output_root" --open
    printf 'Exit code: %s\n' "$?"
}
```

For a working endpoint, the required checks `speech.request`, `audio.container`,
and `audio.signal` pass. The result is `passed` or `passed_with_warnings`, with
exit code `0`. The client sends at most one non-streaming speech POST while the
run remains active. Supporting metadata failures do not, by themselves, block it.
Speech requests are not retried.

With default retention and a supported WAV response, the new run directory contains:

| File | Use |
| --- | --- |
| `report.json` | Read diagnostic data or use it as a comparison input. |
| `report.html` | Review the run offline and play the embedded WAV. |
| `sample.wav` | Open the retained response in an audio player. |

Listen to the response. Automated checks establish a supported PCM16 WAV with
nonzero samples; listening lets you assess the spoken content and pronunciation.
The utility does not automatically score intelligibility or speaker identity.

The HTML includes its styles and retained audio. Wide tables scroll horizontally,
and their scrolling regions can receive keyboard focus. Browser print preview
can print the report or save a PDF; page breaks depend on the browser.

Save the full path from the speech check's `JSON:` line as the before input.
At the prompt below, paste only the path, without `JSON:` or surrounding quotes.

PowerShell:

```powershell
$beforeReportPath = Read-Host 'Paste the full diagnostic JSON path for the before input'
$baselineReport = Get-Content -LiteralPath $beforeReportPath -Raw | ConvertFrom-Json
$baselineReport | Select-Object runId, mode, executionState, outcome
```

Bash:

```bash
IFS= read -r -p 'Before report JSON path: ' before_report_path
```

In PowerShell, you can also inspect selected evidence:

```powershell
$baselineReport.checks | Select-Object id, role, status, code | Format-Table -AutoSize
```

```powershell
$baselineReport.network | ConvertTo-Json -Depth 6
```

```powershell
$baselineReport.audio.inspection | ConvertTo-Json -Depth 6
```

On either platform, the JSON can be read with a text editor. Available audio
evidence includes format, sample rate, channels, duration, signal measurements,
payload size, and SHA-256. Timings, hashes, and payload lengths can vary between
successful requests.

Diagnostic JSON omits original synthesis input and runtime audio bytes. The local
profile still contains that text, and retained audio contains the spoken response.

## 6. Collect an after observation

After a server update, configuration correction, or other deployment change,
run another check using the intended profile. To try the comparison workflow
without changing the server, repeat the check with the same profile.

PowerShell:

```powershell
& $tsubakiExecutable check --profile $profilePath --output $outputRoot --open
$LASTEXITCODE
```

```powershell
$afterReportPath = Read-Host 'Paste the full diagnostic JSON path for the after input'
```

Bash:

```bash
{
    "$tsubaki_executable" check --profile "$profile_path" --output "$output_root" --open
    printf 'Exit code: %s\n' "$?"
}
```

```bash
IFS= read -r -p 'After report JSON path: ' after_report_path
```

Each run receives a unique directory. Failed and incomplete diagnostic reports
can also be comparison inputs. A profile-loading failure produces no diagnostic
report to compare.

Keep `target.id` stable for the same logical deployment. Keep the speech input,
voice, model, speed, and speech route unchanged when you need an equivalent
request context. Changed settings remain visible and can limit comparison.

## 7. Compare the reports offline

PowerShell:

```powershell
& $tsubakiExecutable compare --before $beforeReportPath --after $afterReportPath --output $outputRoot --open
$LASTEXITCODE
```

Bash:

```bash
{
    "$tsubaki_executable" compare --before "$before_report_path" --after "$after_report_path" --output "$output_root" --open
    printf 'Exit code: %s\n' "$?"
}
```

Expected: `Offline comparison published.`, paths to `comparison.json` and
`comparison.html`, and exit code `0`. Successful publication returns `0` even
when a source diagnostic failed. A comparison is not a new server test.

The operation opens source JSON files read-only, makes no server requests,
loads no referenced audio files, and produces no new WAV. The server can be
unavailable while you compare saved reports.

In the comparison HTML, review:

- Recorded source outcomes and execution states.
- Configured target IDs, URLs, modes, and speech request context.
- Changes in individual diagnostic checks and their available evidence.
- Selected server defaults, advertised voices, timings, and audio measurements.
- Findings that limit what can be compared.

The explicit before/after order is preserved; timestamps do not reorder inputs.
Missing historical evidence is not filled in using current defaults. Comparing
the same report twice is allowed and identifies the shared run ID.

A shared target ID is a configured label, not proof that URLs reach the same
physical server. A different audio hash does not establish a quality regression.
Individual durations include the client, access path, server processing, and
transfer; they are observations rather than isolated inference benchmarks.

## 8. Use the example profiles

The portable package and source tree provide these complete JSON examples:

| Example | Purpose |
| --- | --- |
| [tsubaki.local.json](Examples/tsubaki.local.json) | Local loopback server without authentication. |
| [tsubaki.lan.json](Examples/tsubaki.lan.json) | Server on another computer, using an example LAN address. |
| [tsubaki.bearer.json](Examples/tsubaki.bearer.json) | HTTPS service or reverse proxy with bearer authentication. |

For example, copy the LAN profile to a separate working file.

PowerShell:

```powershell
$lanProfilePath = Join-Path $walkthroughRoot 'target.lan.json'
Copy-Item -LiteralPath (Join-Path $packageDirectory 'Examples\tsubaki.lan.json') -Destination $lanProfilePath -ErrorAction Stop
```

Bash:

```bash
lan_profile_path="$walkthrough_root/target.lan.json"
cp "$package_directory/Examples/tsubaki.lan.json" "$lan_profile_path"
```

Edit the copy and replace `http://192.168.1.25:5045/` with the server's actual
address. Then inspect it, followed by a speech check when you want to test synthesis.

PowerShell:

```powershell
& $tsubakiExecutable inspect --profile $lanProfilePath --output $outputRoot
$LASTEXITCODE
```

Bash:

```bash
{
    "$tsubaki_executable" inspect --profile "$lan_profile_path" --output "$output_root"
    printf 'Exit code: %s\n' "$?"
}
```

Use `check` in place of `inspect` to test speech. The server must listen on a
reachable address and port, and the network must permit access. The utility
does not configure server bindings or firewall rules.

The example target IDs describe separate configurations. If local, LAN, and
HTTPS profiles address the same logical deployment, give them the same
`target.id` before collecting results you want to compare.

To collect DNS observations, use a real hostname in a profile copy. For a local
server, `http://localhost:5045/` is an option when its binding supports that host.
Address evidence can contain IPv4 and IPv6 attempts. The direct TCP probe is
separate from the HTTP connection and records supporting reachability evidence.

## 9. Connect through HTTPS and bearer authentication

This scenario requires a deployment that enforces bearer authentication. Copy
the bearer example to another working file:

PowerShell:

```powershell
$bearerProfilePath = Join-Path $walkthroughRoot 'target.bearer.json'
Copy-Item -LiteralPath (Join-Path $packageDirectory 'Examples\tsubaki.bearer.json') -Destination $bearerProfilePath -ErrorAction Stop
```

Bash:

```bash
bearer_profile_path="$walkthrough_root/target.bearer.json"
cp "$package_directory/Examples/tsubaki.bearer.json" "$bearer_profile_path"
```

Replace `https://tts.example.test/tsubaki/` with the actual service root.
The prefix is preserved: default route `v1/audio/speech` would resolve to
`https://tts.example.test/tsubaki/v1/audio/speech` in that example. Routes remain
relative, without a leading `/`. Use the target ID appropriate to the deployment.

The profile names `TSUBAKI_CHECK_TOKEN`. If it is already set correctly in this
shell, reuse it. Otherwise, enter the deployment token without including its
value in command text.

PowerShell:

```powershell
$secureToken = Read-Host 'Bearer token' -AsSecureString
$env:TSUBAKI_CHECK_TOKEN = [System.Net.NetworkCredential]::new('', $secureToken).Password
$secureToken.Dispose()
```

Bash:

```bash
IFS= read -r -s -p 'Bearer token: ' TSUBAKI_CHECK_TOKEN
printf '\n'
export TSUBAKI_CHECK_TOKEN
```

PowerShell:

```powershell
& $tsubakiExecutable check --profile $bearerProfilePath --output $outputRoot
$LASTEXITCODE
```

Bash:

```bash
{
    "$tsubaki_executable" check --profile "$bearer_profile_path" --output "$output_root"
    printf 'Exit code: %s\n' "$?"
}
```

The profile stores the variable name. Missing, empty, or invalid token configuration
returns `2` before network inspection. A token rejected by the deployment is a
different result: an HTTP access failure is recorded in the diagnostic evidence.
Resolved token values and original input text are redacted from diagnostic text;
authentication and cookie header values are not exported.

HTTP requests use normal certificate validation, without automatic redirects,
cookies, or system HTTP-proxy handling. Configure the final service URL directly.
The bearer profile configures the client and does not enable server authentication.

If you set the variable solely for this scenario, remove it when finished.

PowerShell:

```powershell
Remove-Item -LiteralPath Env:TSUBAKI_CHECK_TOKEN
```

Bash:

```bash
unset TSUBAKI_CHECK_TOKEN
```

## 10. Try speech settings, retention, and failure scenarios

Keep the original profile and create a separate copy for a scenario.

PowerShell:

```powershell
$scenarioProfilePath = Join-Path $walkthroughRoot 'scenario.json'
Copy-Item -LiteralPath $profilePath -Destination $scenarioProfilePath -ErrorAction Stop
```

Bash:

```bash
scenario_profile_path="$walkthrough_root/scenario.json"
cp "$profile_path" "$scenario_profile_path"
```

Edit that copy in a text editor. Start from the original settings for each
independent scenario. In particular, restore an invalid setting before trying
another case. Change its `name` to make the resulting reports easier to identify.

| Scenario | Edit in the copied profile | Expected observation |
| --- | --- | --- |
| Custom speech | Set `synthesis.input` to `The service returned a complete audio response.` and `synthesis.speed` to `1.1`. | A successful check verifies audio; comparison identifies the changed request context. |
| Metrics without playback | Set `output.retainAudio` to `false`. | A successful check keeps audio metrics and hash but publishes no WAV or embedded player. |
| Missing status route | Set `routes.status` to `servicecheck/intentional-missing-status`. | If the route returns HTTP 404 and speech succeeds, the result is `passed_with_warnings`, exit `0`. |
| Unreachable endpoint | Set `target.baseUrl` to `http://127.0.0.1:5046/` only if that local port has no listener. | A refused speech connection fails the required request check, skips dependent audio checks, and returns `1`. |
| Input above its cap | Set `limits.maxInputCharacters` to `1` while keeping the normal input sentence. | Profile validation returns `2` before diagnostics start; no diagnostic report is published. |

Run the selected scenario:

PowerShell:

```powershell
& $tsubakiExecutable check --profile $scenarioProfilePath --output $outputRoot --open
$LASTEXITCODE
```

Bash:

```bash
{
    "$tsubaki_executable" check --profile "$scenario_profile_path" --output "$output_root" --open
    printf 'Exit code: %s\n' "$?"
}
```

Read the observed execution state and result code. An unused endpoint can time
out instead of refusing the connection; a deadline or cancellation can change
the aggregate result. If the request succeeds, the selected address was reachable.

The missing status case demonstrates a supporting failure. It is separate from
health fallback: the client tries `routes.healthFallback` only when the primary
health route returns HTTP 404 or 405.

Disabling retention affects the new run, not files already published by earlier
runs. Other voice IDs should be selected only when supported by the deployment.
An incomplete advertised list does not prove that an absent voice is unavailable.

The current speech check requires `responseFormat: "wav"`, `stream: false`, and
a finite speed from `0.25` to `4.0`. `synthesis.model` is a request model label,
not a path to a local model. Other formats and streaming are rejected as profile
settings; no audio-format conversion is performed by the client.

To compare a failed endpoint with a successful correction, set the before and
after path variables from those two `JSON:` lines and repeat section 7. Keep
the logical target ID and speech settings consistent for the intended comparison.

### Reject a profile supplied as a report

The comparison command accepts saved diagnostic JSON, not target profiles.
Using the valid after-report path from section 6:

PowerShell:

```powershell
& $tsubakiExecutable compare --before $profilePath --after $afterReportPath --output $outputRoot
$LASTEXITCODE
```

Bash:

```bash
{
    "$tsubaki_executable" compare --before "$profile_path" --after "$after_report_path" --output "$output_root"
    printf 'Exit code: %s\n' "$?"
}
```

Expected: the before input is rejected with exit code `2`. Comparison inputs
must be supported schema version 1 diagnostic reports, no larger than 8 MiB each.
Existing reports are preserved. Profiles have a separate 64 KiB limit and reject
unknown or duplicate fields, invalid URLs or routes, and unsupported settings.

## 11. Configure deadlines and request cancellation

Edit bounds in a profile copy when adapting the utility to a deployment:

| Setting | Effect |
| --- | --- |
| `timeoutsMs.dns`, `timeoutsMs.tcp` | Bound the respective network inspection stages. |
| `timeoutsMs.metadata` | Bounds a metadata operation, including body receipt. |
| `timeoutsMs.speech` | Bounds the speech request, including body receipt. |
| `timeoutsMs.run` | Bounds overall diagnostic execution across stages. |
| `limits.maxMetadataBytes` | Bounds a metadata response body. |
| `limits.maxAudioBytes` | Bounds the received audio payload. |
| `limits.maxInputCharacters` | Rejects oversized input before network work, measured in UTF-16 code units. |

The run deadline can be shorter than individual operation deadlines. Evidence
that could not be collected remains unavailable. Oversized bodies are rejected
rather than treated as verified audio. Defaults and valid ranges are in the README.

To cancel, start a check and press Ctrl+C while the client is still running.
This requests client cancellation and cancels an active HTTP request. Expected
cancellation exit code: `130`. A fast request can finish before the keypress;
a completed run with exit `0` does not demonstrate cancellation.

When a diagnostic result is available, report publication uses a separate writing
budget so that partial observations can be saved after cancellation or a deadline.
Cancellation before a result exists may leave no report. Output failures can also
prevent complete publication. Comparison observes cancellation during loading,
comparison, and publication.

Native Linux SIGINT cancellation with partial JSON/HTML publication has been
verified; see [STATUS.md](STATUS.md) for the recorded scope.

## 12. Run regression checks from source

This section is for a source checkout with the .NET 10 SDK. From the repository
root, build the executable regression project:

```sh
dotnet build ./ServiceCheck/Tests/Tsubaki.ServiceCheck.Regression.csproj --configuration Release
```

After a successful build:

```sh
dotnet ./ServiceCheck/Tests/bin/Release/net10.0/Tsubaki.ServiceCheck.Regression.dll
```

Read `$LASTEXITCODE` in PowerShell or `$?` in Bash immediately after the command.
Expected: every check passes, zero failures, and process exit code `0`.
The suite uses controlled HTTP/network fixtures, generated temporary files,
and native loopback connections. No running Tsubaki server or models are needed.
The regression executable is separate from the normal portable product archive.

## Exit codes and troubleshooting

| Exit code | Meaning | Example |
| --- | --- | --- |
| `0` | Required diagnostic checks passed, or a comparison was published. Supporting warnings are allowed. | Verified speech, a supporting status-route warning, or successful offline comparison. |
| `1` | A completed diagnostic run failed a required check. | A refused speech connection or silent PCM16 response. |
| `2` | Invalid arguments, profile, authentication configuration, or input report. | Input above its cap or a profile supplied as a report. |
| `3` | Incomplete diagnostics, internal error, or publication failure. | Missing required evidence, a run deadline, unsupported required audio coverage, or unwritable output. |
| `130` | User cancellation. | A handled cancellation while the command is active. |

Start with the execution state, then the required checks and their result codes.
A failed supporting check and a failed required check affect the aggregate result
differently. Preserve JSON with HTML when recording a deployment change; diagnostic
JSON is the input for future offline comparisons.

| Observation | What to review |
| --- | --- |
| `ip_literal` | Expected when the URL contains an IP address instead of a hostname. |
| Connection refused or timed out | Server address, listening port, binding, and network reachability from this client. |
| Metadata unavailable but speech passed | Route configuration and access restrictions; inspect the supporting findings. |
| HTTP 401 or 403 | Authentication required or rejected by the deployment. |
| Speech returned HTTP success but audio failed | Container and signal findings; an HTTP response alone does not establish valid speech audio. |
| HTML did not open automatically | Open the printed path in an available browser. On Linux, quote paths with spaces when using `xdg-open`. |
| Reports could not be published | Output permissions, free space, and any surviving paths printed by the utility. |

The same commands and report formats work on Windows and Linux. Client-side
evidence can show where an observation failed, but it does not establish a
specific firewall cause, remote GPU load, or the server's internal model state.
