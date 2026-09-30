# Mwandishi STT

A local .NET 10 speech journal and transcription and translation server. The name *Mwandishi* comes from Swahili for a writer or scribe. It decodes with FFmpeg, segments speech with Silero VAD, and sends completed segments to Whisper while the decoder processes later audio. Raw audio with explicit VAD starts inference during upload; multipart audio is spooled first to honor form fields in any order. Completed files use a single block unless chunking is requested. The default API response contains the complete transcript.

## Browser journal

Open `http://localhost:5050/` after the server starts. The browser interface captures a microphone, streams uncompressed mono PCM to the local `/live` WebSocket, and displays timestamped text as VAD segments finish. It can also translate speech to English through the same local pipeline. The language selector starts with automatic detection; language and translation are locked until recording stops. Each new browser session starts with translation off, and the active mode and source language are shown while recording. Select the spoken language explicitly if automatic detection mistakes short or mixed-language speech. The language selection and light/dark preference are stored in `localStorage`, and separate journal sessions are stored in IndexedDB. Copy, download, and delete apply to the selected recording; Delete all recordings clears the entire browser journal after confirmation. The server stores no completed journal.

After listening begins, the console prints the browser URL, the OpenAI API
base URL (for example `http://localhost:5050/v1`), and the transcription and
translation routes. On a desktop, the application tries to open the browser
page automatically. Set `ServerSettings:OpenBrowserOnStart` to `false` for a
headless server or when using your own browser. The printed `localhost` URL is
for clients on the same machine. By default, the server listens on port 5050
on all interfaces, allows browser origins through CORS, and accepts WebSocket
clients from any origin. It has no built-in authentication. To keep it local,
set `Kestrel:Endpoints:Http:Url` to `http://localhost:5050`. To restrict
cross-origin HTTP browser clients, set `ServerSecurity:CorsAllowAnyOrigin=false`
and edit `CorsAllowedOrigins`. CORS does not restrict non-browser clients or the
separate `/live` WebSocket route; use a firewall or reverse proxy if you want
to restrict access to the server itself.

Microphone access requires a secure browser context: `localhost` works over HTTP; remote access requires HTTPS and WSS. AudioWorklet captures mono float32 PCM without lossy encoding and sends it at the AudioContext sample rate. The microphone requests input without browser noise suppression, echo cancellation, or automatic gain control; unsupported constraints may be ignored. The server uses one FFmpeg process for resampling to 16 kHz. Stopping flushes the final partial PCM block before the stop command. AudioWorklet requires a supported browser and a secure context. Browser upload `fetch` is half duplex, so the browser uses the separate WebSocket route to receive text while the microphone is still active. OpenAI-compatible HTTP routes remain available.

The browser sends little-endian mono float32 PCM messages to `/live?language=auto&translate=false&audio_format=pcm_f32le&sample_rate=48000` (using the actual AudioContext sample rate, not a fixed 48000) and finishes with the text message `stop`. Omitting `audio_format` retains encoded-file support for existing `/live` clients. Raw PCM declarations require a sample rate between 8000 and 192000 Hz. These are local WebSocket options; the OpenAI-style HTTP fields and encoded file uploads are unchanged. The server sends JSON text messages: `session.ready`, `transcript.text.delta` (ordered `index`, `delta`, audio `start` and `end` seconds), `transcript.text.done` (complete text, duration, language), or `error`. A delta's timestamp is the approximate audio offset, including the VAD pre-roll; it is not the time at which inference finished. The client waits for `done` before marking the recording complete. For long sessions, download the journal as a separate backup because browser storage may be cleared by the user or the browser.

## Requirements

- .NET 10 SDK and PowerShell to create the release archives. The default release archives include the .NET runtime.
- FFmpeg is bundled in both release archives. Direct source builds use a local `ffmpeg` or one on `PATH`.
- A GGML Whisper model and the matching Silero ONNX model, configured in `appsettings.json` or downloaded on first launch.
- For Vulkan inference: a working Vulkan loader and GPU driver on Windows x64 or Linux x64.

The project references `Whisper.net.Runtime.Vulkan` version `1.9.1`. Its published NuGet package already includes five native Linux x64 `.so` files and copies them to `runtimes/vulkan/linux-x64` during build and publish. No custom Whisper runtime build, Vulkan SDK, or CUDA installation is needed to run the published application. The `Whisper.net.Runtime` package provides CPU fallback. The server logs the runtime selected by Whisper.net; `UseGpu=true` alone does not prove GPU inference.

## Build Windows and Linux releases

From the project directory, run `./Build-Releases.ps1` in PowerShell (or
`pwsh ./Build-Releases.ps1` from a shell). The script publishes self-contained
`win-x64` and `linux-x64` versions, downloads the pinned LGPL FFmpeg binaries
from BtbN, and produces two ZIP files in a new timestamped `Builds/` directory.
The archives include the application, FFmpeg, warm-up sample, browser UI,
documentation, and dependency license notices. Models are deliberately absent:
the first run downloads them into `Models/`. A network connection is therefore
required once per installation. The script itself needs a network connection
for NuGet restore and FFmpeg; Windows 10/11 includes the `tar` command used for
the Linux FFmpeg package.

Extract into a writable folder so the first launch can save models. Start
`MwandishiSTT.exe` on Windows or `./MwandishiSTT` on Linux. The Linux ZIP
records executable file permissions and uses forward slashes for directory
entries. If an unpacking tool discards permissions, run
`chmod +x MwandishiSTT`; the application repairs the bundled FFmpeg permission
at startup. A compatible GPU driver is needed for Vulkan inference; set
`SttSettings__UseGpu=false` to use the CPU runtime.

For a smaller package that uses an already installed .NET 10 runtime, pass
`-FrameworkDependent` and run `dotnet MwandishiSTT.dll`. To reuse downloaded
FFmpeg archives on another build, pass `-WindowsFFmpegArchive` and
`-LinuxFFmpegArchive` with paths to the exact upstream files. The upstream
archive names and source revision are pinned in the script. The script checks
both archives against the release's `checksums.sha256` before extracting them;
for an offline build supply that file with `-FFmpegChecksumsFile`. These sums
come from the same publisher as the archives, so independently pin the hashes
if a stronger supply-chain guarantee is needed. Each output ZIP records the
archive and packaged executable SHA-256 hashes in its provenance file; read
`THIRD-PARTY-NOTICES.md` before redistributing the result.

To publish only for Linux x64 during development:

```bash
dotnet publish -c Release -r linux-x64 --self-contained false
```

The direct `dotnet publish` command does not bundle FFmpeg. Check that
`runtimes/vulkan/linux-x64` in the publish directory contains the five `.so`
files. Test on a Linux machine with a working Vulkan driver: publishing
successfully does not prove that the target GPU will execute inference.

## API

`POST /v1/audio/transcriptions` returns recognized text in the source language. `POST /v1/audio/translations` translates speech to English. Both accept OpenAI-style `multipart/form-data` with a `file` field. The local server requires only `file`; if `model` is omitted it uses the loaded Whisper model (the hosted OpenAI API requires `model`). Missing or empty optional form fields keep their defaults. Supported optional fields are `model`, `prompt`, `temperature` (0–1), and `response_format` (`json`, `text`, `verbose_json`, `srt`, `vtt`). Transcriptions also accept `language`, `stream` (boolean), repeated `timestamp_granularities[]` values (`segment`, `word`) with `verbose_json`, and `chunking_strategy`. Unknown options and unsupported formats fail explicitly. The submitted `model` value is accepted but does not select different weights.

```bash
curl -F language=en -F file=@sample.wav -F response_format=json http://localhost:5050/v1/audio/transcriptions
```

OpenAI clients may send multipart fields in any order. The server temporarily stores the uploaded file on disk while reading the form, then runs the selected whole-block or VAD path and Whisper through the bounded producer/consumer pipeline. The temporary file is removed automatically when the request ends. This lets a `language` or `prompt` field after `file` affect the whole recording, but multipart transcription starts after the upload completes.

Raw audio is also accepted as a local extension with `Content-Type: audio/*` or `application/octet-stream`; pass `language`, `prompt`, `temperature`, `response_format`, `stream`, `chunking_strategy`, and `timestamp_granularities[]` as query parameters. With explicit `chunking_strategy=auto`, this route runs VAD and Whisper while bytes arrive. Without a strategy, it waits for EOF before transcribing the accumulated block:

```bash
curl -H 'Content-Type: audio/wav' --data-binary @sample.wav 'http://localhost:5050/v1/audio/transcriptions?language=en'
```

By default the server returns the selected format after processing completes. Set `stream=true` on a transcription request to receive `text/event-stream` with `transcript.text.delta` events as ordered audio pieces finish, followed by one `transcript.text.done` event with the complete text. Use `curl -N` to display events as they arrive. Missing or empty `stream` always means `false`; only an explicit `stream=true` enables SSE. The former `SttSettings:StreamResponse` setting is no longer used. OpenAI's hosted `whisper-1` ignores `stream=true`, so SSE for this local `whisper-1` alias is an intentional extension. Translation requests have no SSE option. A proxy may buffer the response despite server flushes.

For a live lecture journal, use /live or send a continuous raw audio body with stream=true and chunking_strategy=auto, then append each delta in the client. The runner keeps the complete text for the final `transcript.text.done` event, but it does not retain timestamp and token metadata for plain JSON or SSE responses. The journal itself belongs to the client; the server closes the response after input EOF and final inference. Multipart uploads start inference only after the complete form has been received.

`verbose_json` contains detected language, decoded duration, and Whisper segment timestamps by default. Request `timestamp_granularities[]=word` for token-derived word timings; requesting word timings adds inference work. `srt` and `vtt` use the same segment timestamps. The local VAD and parallel segments can produce slightly different boundaries from hosted Whisper.

`GET /v1/models` lists `whisper-1` and `gpt-4o-transcribe` as API aliases; `GET /v1/models/{id}` retrieves either. Both aliases use **the same loaded GGML Whisper weights** and this runner's SSE implementation. The `gpt-4o-transcribe` identifier is offered for streaming-capable client selection; it does **not** indicate OpenAI GPT-4o weights, accuracy, or GPT-only features. Model entries include `supports_streaming`, `loaded_model`, `model_family`, and `multilingual` as local metadata. `GET /health` (and `/v1/health`) reports readiness after model loading and warm-up, the actual backend, queue occupancy, and the same model details. The GGML header can identify an architecture such as `base`, but cannot verify the origin, training, or exact checkpoint version of its weights. The health endpoints and additional model fields are local extensions.

`chunking_strategy=auto` uses the active VAD profile. An omitted or empty strategy processes the completed file as one block, matching the documented OpenAI file behavior. To override a transcription request, send a JSON object such as `{"type":"server_vad","prefix_padding_ms":384,"silence_duration_ms":800,"threshold":0.5}`. Multipart clients may instead send individual fields such as `chunking_strategy[type]=server_vad`. Missing VAD object options inherit the active profile. The duration accepts 32–5000 ms, padding 0–5000 ms, and threshold 0–1. Translation also processes the file as one block and does not accept `chunking_strategy`. Whole-block requests retain decoded audio up to the profile's MaxBufferedSegmentSeconds safety limit (600 seconds by default); raise that limit for longer completed files, or choose auto for long transcriptions. Live microphone sessions always use VAD and bounded segment queues.

Hosted GPT transcription options such as `include[]=logprobs`, `keywords`, language candidates, speaker labels, and `diarized_json` require capabilities absent from the local GGML model; they return a validation error instead of fabricated data. Translation has no `language`, `stream`, or timestamps option. SSE is currently available only for transcription with `response_format=json`.

All VAD tuning lives in `appsettings.json` under `VadSettings`. `Profile` selects a named object in `Profiles`; the default is `segment`. This is a local profile name, not an OpenAI `chunking_strategy` value. Copy that object to create another profile and select its name. Restart the server after changing profiles.

| Profile field | Default | Meaning |
| --- | --- | --- |
| `PauseMs` | 800 | Silence needed to end any speech segment; 32–5000 ms. No short-segment override. |
| `MinSpeechMs` | 250 | Discard brief speech candidates as noise; 0 disables this filter, maximum 5000 ms. Padding is excluded when a pause closes the segment. |
| `PrefixPaddingMs` | 300 | Keep audio before the speech trigger; 0–5000 ms. |
| `TailPaddingMs` | 300 | Keep audio after the detected speech end; 0–5000 ms, limited by available audio. |
| `Threshold` | 0.5 | Probability needed to enter or resume speech; 0–1. |
| `ExitThreshold` | null | Probability below which a silence candidate starts. Null derives `max(Threshold - 0.15, 0.01)`, capped at Threshold. Explicit values must be 0–Threshold. |
| `SplitOverlapMs` | 128 | Overlap at forced duration splits; 0–5000 ms, below the forced segment duration. |
| `MaxSegmentSeconds` | 0 | Zero disables forced splits; set 30 for a duration limit. |
| `MaxBufferedSegmentSeconds` | 600 | Memory safety limit; 1–3600 seconds, at least MaxSegmentSeconds. |

Silero receives 512 new samples at 16 kHz plus the previous 64 waveform samples, independently of its recurrent state. State and waveform context are private to each request and persist across its segments. Hysteresis preserves uncertain speech between the two thresholds. All audio inside a segment, including internal pauses, stays intact. Normal pause padding does not duplicate samples in neighboring segments; forced splits may overlap explicitly. The final partial frame keeps its real length; VAD-only zero padding never reaches Whisper or inflates duration.

Pause detection has 32 ms resolution; padding uses sample-accurate lengths. EOF flushes an active segment that meets MinSpeechMs. The removed `MinSegmentMs` and `ShortSegmentPauseMs` fields no longer delay short phrases: remove them from old profiles. The old VAD settings under `SttSettings` have moved into the selected profile. API `server_vad` overrides apply only to that request. An explicit profile ExitThreshold is capped at a request's Threshold; null derives it from the request threshold. The request body limit is 512 MiB.

The 800 ms pause is the tested local default. It does not delay short speech differently or merge completed phrases. Silero's model, probability scores, short-noise filter, and end padding differ from the hosted service. Completed files use a single block by default; explicit auto/server_vad enables segmentation. SSE controls response delivery independently of this choice. The /live endpoint keeps incremental VAD segmentation.

### Input level normalization

`AudioNormalization` applies to both decoded uploads and live PCM, before VAD and Whisper. Each request owns a causal RMS level controller. It operates in place on the existing frames, adds no lookahead or whole-file buffering, and never changes sample count or speed. This is RMS normalization, not LUFS or a reproduction of OpenAI's undisclosed normalizer.

| Field | Default | Meaning |
| --- | --- | --- |
| `Enabled` | true | Set false for finite PCM passthrough and level-control comparisons. |
| `TargetRmsDbfs` | -20 | Target speech RMS; -40 to -6 dBFS, below PeakDbfs. |
| `NoiseFloorDbfs` | -70 | Frames below this RMS do not drive upward gain and are not amplified; -90 to -20 dBFS, below target. |
| `MaxGainDb` | 30 | Maximum amplification; 0–30 dB. |
| `MinGainDb` | -18 | Minimum requested gain; -60–0 dB. The peak limiter may attenuate further. |
| `PeakDbfs` | -1 | Output sample peak ceiling; -12–0 dBFS. |
| `RmsWindowMs` | 250 | Exponential speech-power averaging; 32–5000 ms. |
| `AttackMs` | 100 | Gain reduction response; 1–5000 ms. |
| `ReleaseMs` | 1000 | Gain increase response; 1–10000 ms. |

Gain changes are ramped within each frame. A final sample limiter protects the output ceiling during sudden peaks; it can alter very loud transients. Nonfinite samples are replaced with zero even when normalization is disabled. This level control cannot restore clipped recordings or guarantee correct transcription. Configuration changes require a server restart. Short or mixed-language segments can still be misrecognized by the default multilingual Whisper small model. Correct VAD and level control do not provide the context of a full recording; compare whole-file and auto modes on representative speech before reducing the pause further.

`ServerSecurity:MaxConcurrentRequests` limits simultaneous uploads. `SttSettings:WhisperWorkers` controls the number of Whisper processors per request (default `2`; values must be positive, with no fixed worker cap). Workers independently transcribe completed VAD segments, then return text in the original audio order. If segment 1 finishes after segment 2, segment 2's text waits for segment 1. Up to twice the worker count of unfinished segments is retained per request; upstream channels provide backpressure. A live stream cannot process its next sentence until VAD finishes that segment.

`ServerSecurity:MaxConcurrentWhisper` is the global inference limit across all requests (default `2`). Set it to at least `2` for two workers to run at once; a lower limit serializes inference even when `WhisperWorkers` is `2`. Multiple workers may use more GPU memory and may not improve performance on a busy Vulkan device. The bundled 1.9.1 runtime has not been validated for simultaneous inference on every Linux driver: compare one and two workers on the target device before raising the global limit further. Set both options to `1` to use sequential inference.

Up to `ServerSecurity:MaxQueuedRequests` additional HTTP requests wait in a first-in-first-out queue before their bodies are read. The default queue holds eight requests, with a `QueueWaitSeconds` limit of 120 seconds. A full queue or an expired wait returns HTTP 503; a disconnected client leaves the queue. Set `MaxQueuedRequests` to `0` for immediate rejection when all active slots are occupied. The separate fixed-window rate limit still returns HTTP 429 when its request budget is exhausted.

The Whisper factory and VAD session remain loaded for the application's lifetime. At startup, before accepting requests, the server transcribes and translates the included eight-second spoken sample to exercise both Whisper tasks and warm the selected backend. `SttSettings:WarmUpAudioFile` can point to another 16 kHz WAV (1–30 seconds). The bundled `Assets/warmup.wav` is a 16 kHz mono conversion of [French Canadian Woman Giving Instructions 04.wav](https://freesound.org/people/vero.marengere/sounds/514877/) by vero.marengere, licensed CC0. This primes shader paths used by the sample; a different model, device, language, or audio shape may still cause some first-use work. Startup takes longer because warm-up runs before the server starts listening.

The local `.gitignore` explicitly includes `Assets/warmup.wav` even when the repository root ignores other WAV files.

## Configuration

`appsettings.json` controls model paths, download behavior, CORS, segmentation profiles, concurrency, and browser launch at startup. The default weights are **multilingual Whisper small** in whisper.cpp GGML format and the existing compatible Silero ONNX model. `UseGpu=false` forces the Whisper CPU runtime.

| Setting | Purpose |
| --- | --- |
| `AutoDownload:WhisperUrl` | Complete download URL for a Whisper GGML file; defaults to `ggml-small.bin` at a pinned revision of [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp). OpenAI's original `.pt` checkpoint cannot be loaded directly by Whisper.net. |
| `AutoDownload:VadUrl` | Complete download URL for a Silero ONNX file compatible with the existing 16 kHz input/context/state contract. |
| `AutoDownload:WhisperSha256`, `VadSha256` | Optional custom file digests. Empty values use built-in verified digests for the exact pinned default URLs. For other URLs, empty values disable digest checking; supply the matching SHA-256 if desired. |
| `SttSettings:ModelDirectory` | Download directory; relative paths are anchored to the application's directory. |
| `SttSettings:WhisperModelName`, `VadModelName` | Local destination filenames. They do not change the remote URL or determine model architecture. |
| `SttSettings:ExactWhisperFilePath`, `ExactVadFilePath` | Existing local model files; take priority over downloads and default hashes. Relative paths are anchored to the application. A missing explicit file is a configuration error. |
| `AutoDownload:Enable` | Set false to require models already present locally. |

To change weights, edit the appropriate full URL and, if configured, its checksum. When replacing a managed model, remove the previous downloaded file or select a different local destination filename: existing files are reused, not overwritten at every startup. The former shared `RepositoryUrl` setting is replaced by the two full URLs. Renaming a compatible model does not change its detected family; startup reads the GGML header. Names and extensions cannot make an incompatible model format compatible.

Default downloads are checked against pinned SHA-256 digests on every startup. Downloads are streamed to a temporary file and renamed only after completion and checksum verification. The `small` weights need about 488 MB on disk and more memory than `base`. The models stay loaded until server shutdown. The tested defaults use an 800 ms VAD pause, two Whisper workers per request, and RMS normalization with `NoiseFloorDbfs=-70` and `MaxGainDb=30`. No special short-phrase merging is enabled. Explicitly select a language when a brief phrase does not provide enough evidence for automatic detection; fixed-language transcription can render foreign words phonetically.

Swagger is enabled in the development environment at `/swagger`. Its upload form shows only the common fields for each endpoint; attach a file and execute to get a JSON transcript. The model alias, language detection, temperature (`0`), response format (`json`), and streaming (`false`) have defaults. Advanced `chunking_strategy` and `timestamp_granularities[]` remain available to API clients without cluttering the basic form.

## Contract checks

Start the server with models available (for a portable CPU check, set `SttSettings__UseGpu=false`), then exercise success and validation paths for every supported request field, both routes, both model aliases, raw audio, multipart uploads, and SSE:

```bash
python3 Tests/api_contract.py --base-url http://127.0.0.1:5050
python3 Tests/long_stream.py
python3 Tests/live_websocket.py
python3 Tests/pcm_live.py --sample path/to/recording.mp3
```

To check cleanup after a client disconnect, start the warmed server with
`ServerSecurity__MaxConcurrentRequests=1`, `ServerSecurity__MaxQueuedRequests=2`,
`ServerSecurity__MaxConcurrentWhisper=1`, and `SttSettings__UseGpu=false`, then
run `python3 Tests/disconnect_cleanup.py`. It checks queue removal, active HTTP
and WebSocket slot release, and a successful transcription afterward. Native
Whisper inference may finish its current step before it observes cancellation.

Run `dotnet run --project Tests/SignalChecks.csproj -c Release` to verify VAD pause timing, hysteresis, PCM preservation, EOF length, forced overlap, and normalization bounds without loading Whisper.

Run `node Tests/pcm_worklet.cjs` to check PCM continuity, mono downmixing, byte order, and final-block flushing without a microphone.

Run `dotnet run --project Tests/ModelChecks/ModelChecks.csproj -c Release` for download URLs, renamed destinations, optional checksums, explicit-path precedence, and failed-download cleanup. Run `node Tests/browser_language.cjs` for remembered language selection and fallback to automatic detection.
