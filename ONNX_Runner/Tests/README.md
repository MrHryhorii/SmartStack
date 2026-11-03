# Tsubaki TTS Engine tests

Regression and integration tests for [Tsubaki TTS Engine](../readMe.md), a local C#/.NET 10 text-to-speech server using Piper, OpenVoice voice cloning and ONNX Runtime. They cover text chunking, multilingual pronunciation, request handling, audio processing, streaming, resource ownership and release packaging.

Run commands from the `ONNX_Runner` directory. The order starts with everyday text/server checks and ends with specialist audio, hardware and distribution checks.

## Prerequisites

- Use the .NET 10 SDK and Release builds.
- These are standalone console runners. Use `dotnet run`; building the solution or calling `dotnet test` does not execute them.
- Nonzero exit codes indicate a failed check or invalid arguments. Preserve failure logs before changing expectations or fixtures.
- `-p:CpuOnly=true` selects CPU engine dependencies. CPU passes do not establish DirectML/CUDA/WebGPU hardware performance.
- Native pronunciation and real synthesis need eSpeak NG and `PiperNative/espeak-ng-data`. Windows uses bundled native files; Linux needs a compatible eSpeak NG library. `TSUBAKI_ESPEAK_LIBRARY` selects an explicit engine library.
- Codec/HTTP tests need the native encoders used by the server, including LAME for MP3. Model-dependent commands use the bundled model files.

The engine excludes `Tests/**` from compilation/publishing. These checks add no test work to ordinary server requests.

## 1. Text chunking and language handling

Start here after editing sentence boundaries, abbreviations, technical text, Unicode punctuation, emoji or multilingual routing. These runners do not perform neural synthesis.

```shell
dotnet run --project Tests/TextChunkerRegression -c Release -- --failures-only
dotnet run --project Tests/LanguageSegmentationRegression -c Release -- --failures-only
dotnet run --project Tests/PunctuationRegression -c Release -- --failures-only
```

Omit `--failures-only` for passing-case messages. TextChunkerRegression also accepts a focused category:

```shell
dotnet run --project Tests/TextChunkerRegression -c Release -- --failures-only --category="Ambiguous letter endings"
```

## 2. Server configuration, pronunciation and HTTP

The configuration runner checks defaults, capabilities and DSP contracts. Native pronunciation needs a compatible Piper JSON and eSpeak NG. The HTTP runners start isolated production servers on temporary ports, disable browser launch and write reports.

```shell
dotnet run --project Tests/ServerConfigurationRegression -c Release -p:CpuOnly=true -- --failures-only
dotnet run --project Tests/TechnicalSpeechIntegration -c Release -- Model/en_US-hfc_female-medium.onnx.json
dotnet run --project Tests/ServerDefaultsRegression -c Release -p:CpuOnly=true -- --synthesis --http test-artifacts/defaults/http --report test-artifacts/defaults/results.json
dotnet run --project Tests/StartupWarmupRegression -c Release -p:CpuOnly=true -- --http test-artifacts/startup/http --report test-artifacts/startup/results.json
```

For full configuration checks with real Piper/OpenVoice CPU inference:

```shell
dotnet run --project Tests/ServerConfigurationRegression -c Release -p:CpuOnly=true -- --failures-only Model/en_US-hfc_female-medium.onnx Model/en_US-hfc_female-medium.onnx.json Cloner Voices
```

The first two paths enable Piper synthesis; the optional `Cloner Voices` pair adds cloning/fallback checks. `--synthesis-only` runs only model-dependent cases. TechnicalSpeechIntegration accepts two additional positional arguments for an exact eSpeak library and the parent of `espeak-ng-data`.

ServerDefaultsRegression covers provider priority, tolerant optional fields, input coercion, API isolation, codecs, streaming/SSE, fragmented bodies and unsafe startup configuration. Its HTTP comparisons use temporary deterministic model JSON; shipped configuration is preserved. Omit `--synthesis`/`--http` for fast reader/adapter checks.

StartupWarmupRegression covers cloned warmups, active DSP, English/automatic language routing, markers, numbering, rate-limit isolation, global switches and failure/cancellation. Its default mode omits child HTTP servers. Longer passages increase startup/test time.

See [defaults/override details](ServerDefaultsRegression/README.md) and [startup profiles](StartupWarmupRegression/README.md). Report paths need an existing parent directory when HTTP mode is omitted.

## 3. Shared audio pipeline and resampling

Run after changing gain, peak protection, output buffering, resampling or encoding. Reference WAV checks accept mono/stereo input. These numeric PCM checks are distinct from the neural HTTP checks above.

```shell
dotnet run --project Tests/SignalPipelineRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/StreamingGainRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/OutputVolumeRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/ResamplerRegression -c Release -p:CpuOnly=true -- --voice Voices/female.wav --voice Voices/male.wav
dotnet run --project Tests/CodecPipelineRegression -c Release -p:CpuOnly=true
```

Resampler options:

- Repeat `--voice path.wav` for more reference recordings.
- `--baseline path/to/TsubakiTTS.dll` compares a retained compatible assembly; current and historical flat audio namespaces are recognized.
- `--benchmark` adds timing when a baseline is supplied and correctness passes.
- `--report path.json` writes a result; create its parent directory first.

Zero previous-PCM comparisons means no baseline was supplied. Do not describe that run as a comparison with a previous implementation.

## 4. Allocations and resource lifetime

Check current-thread managed allocation budgets, pooled buffers, encoders, normal/SSE completion, cancellation, write failures and synthesis-gate ownership. Compare the same runtime, architecture, build mode and inputs; these are not native-memory or VRAM measurements.

```shell
dotnet run --project Tests/TextAnalysisAllocations -c Release -- --iterations=10000
dotnet run --project Tests/ResourceLifetimeRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/EffectResourceRegression -c Release -p:CpuOnly=true
```

## 5. Character effects and output paths

Specialist checks for transfer curves, fixed reference moments, strength/mixing rules, streaming state, tails, output gain and hot-loop allocations. Telephone/device equalization and additional mixer contracts are also covered by ServerConfigurationRegression and the shared pipeline suites.

```shell
dotnet run --project Tests/OverdriveRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/BitcrusherRegression -c Release -p:CpuOnly=true -- --failures-only
dotnet run --project Tests/RingRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/ModulationRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/CassetteRegression -c Release -p:CpuOnly=true -- --failures-only
dotnet run --project Tests/FmPipelineRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/FmCompletionRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/TacticalPipelineRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/HarmonizerRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/RobotRegression -c Release -p:CpuOnly=true
```

Frozen waveform/moment fixtures protect accepted processing. Investigate source changes, namespaces and fixture assumptions before updating them. Numeric power shares and PCM equality do not independently establish perceived loudness or voice quality.

FM's `--character-only` is a focused subset. Its `--record`/`--corpus` modes and Robot's `--audition` require external corpus inputs and create study artifacts. They are optional research modes, not substitutes for default regressions.

## 6. Spatial environments and tails

Run the affected environment after a local change, or the whole group after modifying shared spatial processing. Hall/Cavern/Forest/Room/LivingRoom/Underwater also check real output-pipeline tails. The broad tail sweep can take longer than individual environments.

```shell
dotnet run --project Tests/RoomRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/LivingRoomRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/BathroomRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/HallRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/CavernRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/ForestRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/UnderwaterRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/DeepWaterRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/SwimmingPoolRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/MuffledRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/InnerVoiceRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/LargeSpaceRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/StadiumRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/OutdoorRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/UrbanRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/ClassicSpatialRegression -c Release -p:CpuOnly=true
dotnet run --project Tests/SpatialTailRegression -c Release -p:CpuOnly=true
```

## 7. Model download smoke checks

Use a disposable build directory with internet access. These PowerShell commands modify only that copy and its voice caches.

```powershell
$modelSmoke = Join-Path ([IO.Path]::GetTempPath()) ("Tsubaki-ModelSmoke-" + [guid]::NewGuid().ToString("N"))
dotnet build ONNX_Runner.csproj -c Release -p:CpuOnly=true -o $modelSmoke
foreach ($modelSmokeFolder in @("Model", "Cloner")) {
    $modelSmokePath = Join-Path $modelSmoke $modelSmokeFolder
    if (Test-Path -LiteralPath $modelSmokePath) { Remove-Item -LiteralPath $modelSmokePath -Recurse -Force }
}
$modelSmokeListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$modelSmokeListener.Start()
$modelSmokePort = $modelSmokeListener.LocalEndpoint.Port
$modelSmokeListener.Stop()
Push-Location $modelSmoke
try {
    dotnet ./TsubakiTTS.dll --Kestrel:Endpoints:Http:Url "http://127.0.0.1:$modelSmokePort" --StartupSettings:OpenBrowserOnStart false --HardwareSettings:ForcePiperToCpu true --ModelSettings:AutoDownloadFallbackModel true
}
finally { Pop-Location }
```

Wait for loading/warm-up and inspect `/tsbk/server/status`: `synthesisReady` and `voiceCloning.available` should be true. Stop with Ctrl+C. Expect a Piper fallback pair under `Model/Fallback` and three OpenVoice files under `Cloner`.

For partial-file recovery, stop the copy, remove one file from each family, then repeat the launch from `$modelSmoke`:

```powershell
Remove-Item (Join-Path $modelSmoke "Model/Fallback/fallback.onnx.json")
Remove-Item (Join-Path $modelSmoke "Cloner/tone_color.onnx")
```

Only missing files should be fetched; compare remaining file hashes to check reuse. Repeat individually for fallback ONNX, tone extractor, tone converter and tone JSON. Failed transfers must leave no completed partial model.

Piper's shipped `AutoDownloadFallbackModel` is `false`: an interactive launch asks before downloading; non-interactive input needs `true` for automatic retrieval. A usable cached fallback is reused regardless of that switch. A primary voice missing ONNX/JSON uses the configured complete fallback pair; an unrelated fallback JSON is not used to repair an arbitrary custom voice. OpenVoice fetches missing files when cloning is enabled; disabled cloning does not request them. Unavailable OpenVoice retains base Piper speech.

## 8. Inference concurrency and hardware

CPU fixtures validate pool ownership, cancellation, concurrency policy and forced-CPU routing. They do not establish real GPU execution or performance.

```shell
dotnet run --project Tests/InferenceConcurrencyRegression -c Release -p:CpuOnly=true
```

WebGPU-compiled guard/routing fixtures:

```shell
dotnet run --project Tests/InferenceConcurrencyRegression -c Release -p:CpuOnly=false -p:UseWebGpu=true
```

These also check version guards and preservation of OpenVoice device selection. They do not require proof of physical GPU execution.

For real HTTP concurrency, first start a server with the intended backend/device settings, then run PowerShell 7:

```powershell
pwsh -NoProfile -File Tests/InferenceConcurrencyRegression/Test-WebGpuRequests.ps1 -BaseUrl http://localhost:5045 -Voice female -Requests 6 -OutputDirectory test-artifacts/webgpu-http
```

The voice must exist. The script checks simultaneous successful WAV responses; confirm the actual backend/device in server diagnostics. Real DirectML/CUDA/WebGPU runs require suitable hardware and drivers.

## 9. Dependency and release packaging checks

Run the NAudio boundary checks after updating audio dependencies.

```shell
dotnet run --project Tests/NAudioUpgradeRegression -c Release -p:CpuOnly=true
```

The specialist distribution checks need PowerShell 7.4+, restored packages and real matching published artifacts. They are not executed by the console runners.

Example Windows CPU release with a dedicated corresponding-source cache:

```powershell
pwsh -NoProfile -File tools/Publish-WithLicenses.ps1 -Variant Windows-CPU -SourcesRoot test-artifacts/corresponding-sources
$testReleaseRun = (Get-ChildItem ./Publish-Licensed -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$testPublish = Join-Path $testReleaseRun "tsubaki-tts-engine-windows-x64-cpu"
$testPackageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath("UserProfile")) ".nuget/packages" }
$testSignalPackage = Join-Path $testPackageRoot "signalsmithstretch-cs/1.1.2"
pwsh -NoProfile -File Tests/HarmonizerRegression/PublishLicenseChecks.ps1 -PublishDirectory $testPublish -RuntimeIdentifier win-x64 -PackageDirectory $testSignalPackage -WorkDirectory test-artifacts/signalsmith-licenses -SourceDirectory test-artifacts/corresponding-sources
pwsh -NoProfile -File Tests/ReleasePackagingRegression/Verify-Release.ps1 -RunDirectory $testReleaseRun
```

Use the matching Linux CPU artifact and `linux-x64` for a Linux package. Checks cover native/package identity, notices, corresponding sources, source hashes and client ZIP inventory. Use artifacts from the current matching release run.

For setup, APIs and deployment, return to the [main Tsubaki TTS Engine project](https://github.com/MrHryhorii/SmartStack/tree/main/ONNX_Runner).
