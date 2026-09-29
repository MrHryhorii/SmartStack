# Mwandishi STT

A local .NET 10 speech journal and transcription and translation server. The name *Mwandishi* comes from Swahili for a writer or scribe. It decodes with FFmpeg, segments speech with Silero VAD, and sends completed segments to Whisper while the decoder processes later audio. Raw audio starts inference during upload; multipart audio is spooled first to honor form fields in any order. The default API response contains the complete transcript.

## Browser journal

Open `http://localhost:5050/` after the server starts. The browser interface captures a microphone, streams encoded audio to the local `/live` WebSocket, and displays timestamped text as VAD segments finish. It can also translate speech to English through the same local pipeline. The language selector starts with automatic detection; language and translation are locked until recording stops. The light/dark preference is stored in `localStorage`, and separate journal sessions are stored in IndexedDB. Copy, download, and delete apply to the selected recording. The server stores no completed journal.

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

Microphone access requires a secure browser context: `localhost` works over HTTP; remote access requires HTTPS and WSS. MediaRecorder chooses WebM/Opus, Ogg/Opus, or MP4/AAC when supported by the browser. The application sends all chunks of one recording to one FFmpeg process. Browser upload `fetch` is half duplex, so the browser uses the separate WebSocket route to receive text while the microphone is still active. OpenAI-compatible HTTP routes remain available.

The browser sends binary audio messages to `/live?language=auto&translate=false` and finishes with the text message `stop`. The server sends JSON text messages: `session.ready`, `transcript.text.delta` (ordered `index`, `delta`, audio `start` and `end` seconds), `transcript.text.done` (complete text, duration, language), or `error`. A delta's timestamp is the approximate audio offset, including the VAD pre-roll; it is not the time at which inference finished. The client waits for `done` before marking the recording complete. For long sessions, download the journal as a separate backup because browser storage may be cleared by the user or the browser.

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

`POST /v1/audio/transcriptions` returns recognized text in the source language. `POST /v1/audio/translations` translates speech to English. Both accept OpenAI-style `multipart/form-data` with a `file` field. Supported optional fields are `model`, `prompt`, `temperature` (0–1), and `response_format` (`json`, `text`, `verbose_json`, `srt`, `vtt`). Transcriptions also accept `language`, `stream` (boolean), repeated `timestamp_granularities[]` values (`segment`, `word`) with `verbose_json`, and `chunking_strategy`. Unknown options and unsupported formats fail explicitly. The submitted `model` value is accepted but does not select different weights.

```bash
curl -F language=en -F file=@sample.wav -F response_format=json http://localhost:5050/v1/audio/transcriptions
```

OpenAI clients may send multipart fields in any order. The server temporarily stores the uploaded file on disk while reading the form, then runs VAD and Whisper through the bounded producer/consumer pipeline. The temporary file is removed automatically when the request ends. This lets a `language` or `prompt` field after `file` affect the whole recording, but multipart transcription starts after the upload completes.

Raw audio is also accepted as a local extension with `Content-Type: audio/*` or `application/octet-stream`; pass `language`, `prompt`, `temperature`, `response_format`, `stream`, `chunking_strategy`, and `timestamp_granularities[]` as query parameters. This route still runs VAD and Whisper while bytes arrive:

```bash
curl -H 'Content-Type: audio/wav' --data-binary @sample.wav 'http://localhost:5050/v1/audio/transcriptions?language=en'
```

By default the server returns the selected format after processing completes. Set `stream=true` on a transcription request to receive `text/event-stream` with `transcript.text.delta` events as ordered VAD segments finish, followed by one `transcript.text.done` event with the complete text. Use `curl -N` to display events as they arrive. The `SttSettings:StreamResponse` setting remains an optional server-wide default; the per-request `stream` field overrides it. OpenAI's hosted `whisper-1` ignores `stream=true`, so SSE for this local `whisper-1` alias is an intentional extension. Translation requests have no SSE option. A proxy may buffer the response despite server flushes.

For a live lecture journal, send a continuous raw audio body and append each delta in the client. The runner keeps the complete text for the final `transcript.text.done` event, but it does not retain timestamp and token metadata for plain JSON or SSE responses. The journal itself belongs to the client; the server closes the response after input EOF and final inference. Multipart uploads start inference only after the complete form has been received.

`verbose_json` contains detected language, decoded duration, and Whisper segment timestamps by default. Request `timestamp_granularities[]=word` for token-derived word timings; requesting word timings adds inference work. `srt` and `vtt` use the same segment timestamps. The local VAD and parallel segments can produce slightly different boundaries from hosted Whisper.

`GET /v1/models` lists `whisper-1` and `gpt-4o-transcribe` as API aliases; `GET /v1/models/{id}` retrieves either. Both aliases use **the same loaded GGML Whisper weights** and this runner's SSE implementation. The `gpt-4o-transcribe` identifier is offered for streaming-capable client selection; it does **not** indicate OpenAI GPT-4o weights, accuracy, or GPT-only features. Model entries include `supports_streaming`, `loaded_model`, `model_family`, and `multilingual` as local metadata. `GET /health` (and `/v1/health`) reports readiness after model loading and warm-up, the actual backend, queue occupancy, and the same model details. The GGML header can identify an architecture such as `base`, but cannot verify the origin, training, or exact checkpoint version of its weights. The health endpoints and additional model fields are local extensions.

`chunking_strategy=auto` uses the configured VAD pause, 384 ms pre-roll, and threshold 0.5. To tune a transcription request, send a JSON object such as `{"type":"server_vad","prefix_padding_ms":384,"silence_duration_ms":800,"threshold":0.5}`. Multipart clients may instead send individual fields such as `chunking_strategy[type]=server_vad`. The duration accepts 32–5000 ms, padding 0–5000 ms, and threshold 0–1. Audio is always segmented by VAD even when this field is omitted; this is the runner's processing design. Translation uses the configured VAD and does not accept `chunking_strategy`.

Hosted GPT transcription options such as `include[]=logprobs`, `keywords`, language candidates, speaker labels, and `diarized_json` require capabilities absent from the local GGML model; they return a validation error instead of fabricated data. Translation has no `language`, `stream`, or timestamps option. SSE is currently available only for transcription with `response_format=json`.

VAD splits on `SttSettings:VadPauseMs` of silence (default `800` ms, rounded up to a 32 ms VAD frame; accepted range `32`–`5000` ms). A shorter pause sends segments to Whisper sooner but can cut at natural hesitations and change transcription quality. `SttSettings:MaxSegmentSeconds` is `0` by default: no duration-based segmentation occurs. Set it to `30` to split continuous speech approximately every 30 seconds; adjacent long segments retain a short overlap, so words at a split may repeat. `SttSettings:MaxBufferedSegmentSeconds` defaults to `600`: if a segment exceeds this memory safety limit, the request fails rather than growing without bound. Up to 384 ms of audio is kept before VAD reports a new speech segment to preserve quiet word onsets. The request body limit is 512 MiB.

`ServerSecurity:MaxConcurrentRequests` limits simultaneous uploads. `SttSettings:WhisperWorkers` controls the number of Whisper processors per request (default `2`; values must be positive, with no fixed worker cap). Workers independently transcribe completed VAD segments, then return text in the original audio order. If segment 1 finishes after segment 2, segment 2's text waits for segment 1. Up to twice the worker count of unfinished segments is retained per request; upstream channels provide backpressure. A live stream cannot process its next sentence until VAD finishes that segment.

`ServerSecurity:MaxConcurrentWhisper` is the global inference limit across all requests (default `2`). Set it to at least `2` for two workers to run at once; a lower limit serializes inference even when `WhisperWorkers` is `2`. Multiple workers may use more GPU memory and may not improve performance on a busy Vulkan device. The bundled 1.9.1 runtime has not been validated for simultaneous inference on every Linux driver: compare one and two workers on the target device before raising the global limit further. Set both options to `1` to use sequential inference.

Up to `ServerSecurity:MaxQueuedRequests` additional HTTP requests wait in a first-in-first-out queue before their bodies are read. The default queue holds eight requests, with a `QueueWaitSeconds` limit of 120 seconds. A full queue or an expired wait returns HTTP 503; a disconnected client leaves the queue. Set `MaxQueuedRequests` to `0` for immediate rejection when all active slots are occupied. The separate fixed-window rate limit still returns HTTP 429 when its request budget is exhausted.

The Whisper factory and VAD session remain loaded for the application's lifetime. At startup, before accepting requests, the server transcribes and translates the included eight-second spoken sample to exercise both Whisper tasks and warm the selected backend. `SttSettings:WarmUpAudioFile` can point to another 16 kHz WAV (1–30 seconds). The bundled `Assets/warmup.wav` is a 16 kHz mono conversion of [French Canadian Woman Giving Instructions 04.wav](https://freesound.org/people/vero.marengere/sounds/514877/) by vero.marengere, licensed CC0. This primes shader paths used by the sample; a different model, device, language, or audio shape may still cause some first-use work. Startup takes longer because warm-up runs before the server starts listening.

The local `.gitignore` explicitly includes `Assets/warmup.wav` even when the repository root ignores other WAV files.

## Configuration

`appsettings.json` controls model paths, download behavior, CORS, segmentation, the default SSE setting, concurrency, and browser launch at startup. Default model downloads use a fixed Hugging Face commit and SHA-256 values; managed model files are checked on every startup. If a managed model fails verification, replace the file with the expected model or configure a different name and its correct hash. Explicit `ExactWhisperFilePath` and `ExactVadFilePath` are user-supplied models and are not checked against the default hashes. `UseGpu=false` forces the Whisper CPU runtime. Swagger is enabled in the development environment at `/swagger`.

## Contract checks

Start the server with models available (for a portable CPU check, set `SttSettings__UseGpu=false`), then exercise success and validation paths for every supported request field, both routes, both model aliases, raw audio, multipart uploads, and SSE:

```bash
python3 Tests/api_contract.py --base-url http://127.0.0.1:5050
python3 Tests/long_stream.py
python3 Tests/live_websocket.py
```

To check cleanup after a client disconnect, start the warmed server with
`ServerSecurity__MaxConcurrentRequests=1`, `ServerSecurity__MaxQueuedRequests=2`,
`ServerSecurity__MaxConcurrentWhisper=1`, and `SttSettings__UseGpu=false`, then
run `python3 Tests/disconnect_cleanup.py`. It checks queue removal, active HTTP
and WebSocket slot release, and a successful transcription afterward. Native
Whisper inference may finish its current step before it observes cancellation.
