# Mwandishi STT

Mwandishi STT is a local speech-to-text server and browser speech journal built with .NET 10.

It uses FFmpeg to decode incoming audio, Silero VAD to detect speech segments, and Whisper through Whisper.net for transcription and speech-to-English translation. The server exposes OpenAI-compatible HTTP routes, optional Server-Sent Events for incremental transcription, and a WebSocket endpoint for live microphone sessions.

The project runs locally on Windows x64 and Linux x64. Whisper can use Vulkan when available and falls back to CPU when the Vulkan runtime cannot be loaded.

## Contents

- [Features](#features)
- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [Models](#models)
- [Configuration](#configuration)
- [HTTP API](#http-api)
- [Live WebSocket API](#live-websocket-api)
- [Browser journal](#browser-journal)
- [Concurrency and rate limiting](#concurrency-and-rate-limiting)
- [Building](#building)
- [Validation and tests](#validation-and-tests)
- [Security notes](#security-notes)
- [Troubleshooting](#troubleshooting)
- [Third-party components](#third-party-components)

## Features

- Local transcription with Whisper GGML models.
- Local speech-to-English translation using Whisper's translation task.
- OpenAI-compatible `/v1/audio/transcriptions` and `/v1/audio/translations` routes.
- JSON, plain text, `verbose_json`, SRT, and WebVTT responses.
- Optional word and segment timestamps.
- Optional SSE transcription output with incremental text deltas.
- Live WebSocket transcription for browser or custom microphone clients.
- Silero VAD with configurable profiles and per-request `server_vad` overrides.
- FFmpeg-based decoding and resampling to 16 kHz mono float PCM.
- Streaming RMS level normalization before VAD and Whisper.
- Vulkan inference through Whisper.net with CPU fallback.
- Bounded request queues and a separate global Whisper inference limit.
- Fixed-window HTTP rate limiting.
- Built-in browser journal stored entirely in the browser.
- Automatic model download with SHA-256 verification for the pinned default models.
- Self-contained Windows x64 and Linux x64 release builds.

## How it works

The main audio pipeline is:

```text
HTTP or WebSocket input
        |
        v
FFmpeg decode / resample
        |
        v
16 kHz mono float PCM
        |
        v
Streaming RMS normalization
        |
        v
Silero VAD or whole-file buffering
        |
        v
Whisper workers
        |
        v
Ordered transcript / translation
        |
        v
JSON, text, subtitles, SSE, or WebSocket events
```

FFmpeg and VAD can continue processing later audio while Whisper handles completed speech segments. Bounded channels provide backpressure instead of allowing unlimited audio or segment buffering.

For completed HTTP files, VAD is not enabled unless `chunking_strategy` is explicitly supplied. Without chunking, the completed recording is processed as one block. Live WebSocket sessions always use VAD.

## Requirements

### Running a packaged release

- Windows x64 or Linux x64.
- A writable application directory if models must be downloaded on first launch.
- Internet access on first launch when `AutoDownload:Enable` is `true` and the configured models are not already present.
- A Vulkan-capable driver if GPU inference is requested.
- No Python installation is required.
- No CUDA installation is required.
- No Vulkan SDK is required for the packaged application.

The release archives include:

- the .NET runtime by default;
- the application;
- FFmpeg;
- Whisper.net CPU and Vulkan native runtimes;
- the browser UI;
- the warm-up audio file;
- third-party notices.

Model files are not included in release archives.

### Building from source

Install:

- .NET 10 SDK;
- Git;
- FFmpeg on `PATH`, unless a usable `ffmpeg` or `ffmpeg.exe` is placed next to the built application;
- PowerShell 7 or Windows PowerShell when using `Build-Releases.ps1`.

The release script also needs network access for NuGet restore and FFmpeg downloads unless the required FFmpeg archives and checksum file are supplied manually.

## Quick start

### 1. Clone the repository

```bash
git clone https://github.com/MrHryhorii/SmartStack.git
cd SmartStack/STT_Runner
```

### 2. Restore dependencies

```bash
dotnet restore
```

### 3. Check FFmpeg

For a source build, make sure this succeeds:

```bash
ffmpeg -version
```

A packaged release already contains FFmpeg.

### 4. Run the server

```bash
dotnet run
```

By default the server listens on:

```text
http://localhost:5050
```

The default Kestrel configuration actually binds to all interfaces with `http://+:5050`; `localhost` is the local client address printed by the application.

On first launch, the default Whisper and Silero VAD models are downloaded into `Models/` when they are not already present.

### 5. Test transcription

```bash
curl -F "file=@sample.wav" \
     -F "language=en" \
     http://localhost:5050/v1/audio/transcriptions
```

Example response:

```json
{
  "text": "Recognized speech."
}
```

## Models

Mwandishi STT uses two model files:

| Model | Default | Purpose |
| --- | --- | --- |
| Whisper | `ggml-small.bin` | Speech recognition and speech-to-English translation |
| Silero VAD | `silero_vad.onnx` | Speech activity detection and segmentation |

The default Whisper model is the multilingual `small` model in whisper.cpp GGML format. It is approximately 488 MB on disk.

OpenAI `.pt` Whisper checkpoints cannot be loaded directly by Whisper.net. Use a compatible whisper.cpp GGML model.

### Model resolution order

For each model the server uses this order:

1. `SttSettings:ExactWhisperFilePath` or `SttSettings:ExactVadFilePath`, when configured.
2. A file in `SttSettings:ModelDirectory` using the configured model filename.
3. Automatic download when the file does not exist and `AutoDownload:Enable` is `true`.

Relative paths are resolved from the application directory, not from the shell's current working directory.

### Default model verification

The default download URLs are pinned to specific revisions and have built-in SHA-256 digests. The digest is checked on startup when the default URL is used.

For custom URLs:

- leave the SHA-256 setting empty to disable digest verification;
- or provide the expected 64-character SHA-256 value.

Downloads are written to a temporary `.download` file and renamed only after the download and optional checksum verification succeed.

### GPU and CPU runtime

`SttSettings:UseGpu=true` requests the Vulkan runtime. Whisper.net tries Vulkan first and CPU second.

If Vulkan cannot be loaded, the server logs a warning and continues on CPU.

`UseGpu=true` therefore means "request Vulkan", not "guarantee GPU inference". Check `/health` or the startup log to see the runtime that was actually loaded.

## Configuration

The main configuration file is:

```text
appsettings.json
```

Standard ASP.NET Core configuration overrides are supported. Environment variables use double underscores for nested keys.

Example:

```bash
SttSettings__UseGpu=false
```

On PowerShell:

```powershell
$env:SttSettings__UseGpu = "false"
dotnet run
```

Configuration changes are read at startup. Restart the server after changing model, VAD, normalization, concurrency, or server settings.

### Default configuration

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://+:5050"
      }
    }
  },
  "ServerSettings": {
    "OpenBrowserOnStart": true
  },
  "ServerSecurity": {
    "MaxConcurrentRequests": 4,
    "MaxQueuedRequests": 8,
    "QueueWaitSeconds": 120,
    "MaxConcurrentWhisper": 2,
    "EnableRateLimiting": true,
    "RateLimitMaxRequests": 100,
    "RateLimitWindowSeconds": 60,
    "CorsAllowAnyOrigin": true,
    "CorsAllowedOrigins": [
      "http://localhost:3000",
      "http://127.0.0.1:3000",
      "http://localhost:11434",
      "http://127.0.0.1:11434",
      "http://localhost:5001",
      "http://127.0.0.1:5001",
      "http://localhost:5173",
      "http://localhost:8080",
      "http://localhost:5050"
    ]
  },
  "SttSettings": {
    "ModelDirectory": "Models",
    "WhisperModelName": "ggml-small.bin",
    "VadModelName": "silero_vad.onnx",
    "ExactWhisperFilePath": "",
    "ExactVadFilePath": "",
    "UseGpu": true,
    "GpuDeviceIndex": 0,
    "WhisperWorkers": 2,
    "WarmUpAudioFile": "Assets/warmup.wav",
    "DefaultLanguage": "auto"
  },
  "AutoDownload": {
    "Enable": true,
    "WhisperUrl": "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small.bin",
    "VadUrl": "https://huggingface.co/Hinotsuba/silero_vad_ggml-base/resolve/ee6290b4dde18d884258a108a809daffb6ca11cb/silero_vad.onnx",
    "WhisperSha256": "",
    "VadSha256": ""
  },
  "VadSettings": {
    "Profile": "segment",
    "Profiles": {
      "segment": {
        "PauseMs": 800,
        "MaxSegmentSeconds": 0,
        "MaxBufferedSegmentSeconds": 600,
        "PrefixPaddingMs": 300,
        "TailPaddingMs": 300,
        "Threshold": 0.5,
        "SplitOverlapMs": 128,
        "MinSpeechMs": 250,
        "ExitThreshold": null
      }
    }
  },
  "AudioNormalization": {
    "Enabled": true,
    "TargetRmsDbfs": -20,
    "NoiseFloorDbfs": -70,
    "MaxGainDb": 30,
    "MinGainDb": -18,
    "PeakDbfs": -1,
    "RmsWindowMs": 250,
    "AttackMs": 100,
    "ReleaseMs": 1000
  }
}
```

### `Kestrel`

| Setting | Default | Description |
| --- | --- | --- |
| `Kestrel:Endpoints:Http:Url` | `http://+:5050` | HTTP listener. `+` binds to all interfaces. Use `http://localhost:5050` for local-only listening. |

### `ServerSettings`

| Setting | Default | Description |
| --- | --- | --- |
| `OpenBrowserOnStart` | `true` | Opens the browser journal after the server starts. Disable for headless use. |

### `ServerSecurity`

| Setting | Default | Description |
| --- | ---: | --- |
| `MaxConcurrentRequests` | `4` | Maximum active HTTP or live request pipelines. Must be at least 1. |
| `MaxQueuedRequests` | `8` | Number of additional requests allowed to wait for a request slot. `0` disables waiting. |
| `QueueWaitSeconds` | `120` | Maximum time a queued request waits for a slot. Must be at least 1 second. |
| `MaxConcurrentWhisper` | `2` | Global limit for simultaneous Whisper inference across all requests. Values below 1 are clamped to 1. |
| `EnableRateLimiting` | `true` | Enables the fixed-window HTTP rate limiter on transcription and translation routes. |
| `RateLimitMaxRequests` | `100` | Number of allowed HTTP requests per fixed window. |
| `RateLimitWindowSeconds` | `60` | Fixed rate-limit window length. |
| `CorsAllowAnyOrigin` | `true` | Allows any browser origin for HTTP API requests. |
| `CorsAllowedOrigins` | see JSON | Used only when `CorsAllowAnyOrigin` is `false`. |

The request queue and the HTTP rate limiter are separate systems:

- request queue exhaustion or timeout returns HTTP `503`;
- fixed-window rate-limit exhaustion returns HTTP `429`.

CORS applies to browser HTTP requests. It does not authenticate clients, block normal non-browser HTTP clients, or restrict the separate `/live` WebSocket route.

### `SttSettings`

| Setting | Default | Description |
| --- | --- | --- |
| `ModelDirectory` | `Models` | Directory used for managed local model files and automatic downloads. |
| `WhisperModelName` | `ggml-small.bin` | Filename used inside `ModelDirectory`. Changing the filename does not change the download URL. |
| `VadModelName` | `silero_vad.onnx` | VAD filename used inside `ModelDirectory`. |
| `ExactWhisperFilePath` | empty | Explicit existing Whisper model path. Takes priority over `ModelDirectory` and auto-download. |
| `ExactVadFilePath` | empty | Explicit existing Silero VAD model path. Takes priority over `ModelDirectory` and auto-download. |
| `UseGpu` | `true` | Requests Whisper.net Vulkan inference. CPU is used as fallback when Vulkan cannot load. |
| `GpuDeviceIndex` | `0` | Vulkan GPU device index passed to Whisper.net. |
| `WhisperWorkers` | `2` | Number of Whisper processors created per request. Must be at least 1. |
| `WarmUpAudioFile` | `Assets/warmup.wav` | WAV used to warm transcription and translation paths before the server begins listening. |
| `DefaultLanguage` | `auto` | Default Whisper source language when a request does not provide one. |

The warm-up file must be:

- WAV;
- 16 kHz;
- between 1 and 30 seconds.

The server performs VAD warm-up and then runs both Whisper transcription and translation warm-up before it accepts traffic.

### `AutoDownload`

| Setting | Default | Description |
| --- | --- | --- |
| `Enable` | `true` | Downloads missing managed model files. If disabled, missing models cause startup to fail. |
| `WhisperUrl` | pinned `ggml-small.bin` URL | Full HTTP or HTTPS URL for the Whisper model. |
| `VadUrl` | pinned Silero URL | Full HTTP or HTTPS URL for the VAD model. |
| `WhisperSha256` | empty | Optional custom SHA-256. The pinned default URL uses a built-in hash when this field is empty. |
| `VadSha256` | empty | Optional custom SHA-256. The pinned default URL uses a built-in hash when this field is empty. |

If you replace a managed model, either remove the existing destination file or change its configured local filename. Existing files are reused rather than downloaded again on every startup.

### `VadSettings`

`VadSettings:Profile` selects one named object under `VadSettings:Profiles`.

The default profile is `segment`.

You can add another profile:

```json
"VadSettings": {
  "Profile": "long-form",
  "Profiles": {
    "segment": {
      "PauseMs": 800,
      "MaxSegmentSeconds": 0,
      "MaxBufferedSegmentSeconds": 600,
      "PrefixPaddingMs": 300,
      "TailPaddingMs": 300,
      "Threshold": 0.5,
      "SplitOverlapMs": 128,
      "MinSpeechMs": 250,
      "ExitThreshold": null
    },
    "long-form": {
      "PauseMs": 1200,
      "MaxSegmentSeconds": 30,
      "MaxBufferedSegmentSeconds": 600,
      "PrefixPaddingMs": 300,
      "TailPaddingMs": 300,
      "Threshold": 0.5,
      "SplitOverlapMs": 128,
      "MinSpeechMs": 250,
      "ExitThreshold": null
    }
  }
}
```

| Field | Default | Valid range | Description |
| --- | ---: | --- | --- |
| `PauseMs` | `800` | `32..5000` | Silence required to close a speech segment. |
| `MinSpeechMs` | `250` | `0..5000` | Minimum speech duration. Shorter candidates are discarded. `0` disables this filter. |
| `PrefixPaddingMs` | `300` | `0..5000` | Audio retained before the speech trigger. |
| `TailPaddingMs` | `300` | `0..5000` | Audio retained after detected speech end. |
| `Threshold` | `0.5` | `0..1` | Probability required to enter or resume speech. |
| `ExitThreshold` | `null` | `0..Threshold` or `null` | Probability below which silence begins. `null` derives a lower threshold automatically. |
| `SplitOverlapMs` | `128` | `0..5000` | Audio overlap used only for forced duration splits. Must be shorter than `MaxSegmentSeconds` when forced splitting is enabled. |
| `MaxSegmentSeconds` | `0` | `0..MaxBufferedSegmentSeconds` | Forced maximum speech segment duration. `0` disables forced splitting. |
| `MaxBufferedSegmentSeconds` | `600` | `1..3600` | Safety limit for buffered audio. Also limits whole-file mode. |

The VAD model operates on 512 new samples at 16 kHz for each inference step, which corresponds to 32 ms of audio.

### `AudioNormalization`

Normalization runs before both VAD and Whisper for decoded files and live PCM.

It is a causal RMS level controller, not LUFS normalization. It does not change sample count, timing, or playback speed.

| Field | Default | Valid range | Description |
| --- | ---: | --- | --- |
| `Enabled` | `true` | boolean | Enables or disables level normalization. |
| `TargetRmsDbfs` | `-20` | `-40..-6` | Target speech RMS level. Must remain below `PeakDbfs`. |
| `NoiseFloorDbfs` | `-70` | `-90..-20` | Frames below this level do not drive upward gain. Must remain below the target. |
| `MaxGainDb` | `30` | `0..30` | Maximum amplification. |
| `MinGainDb` | `-18` | `-60..0` | Minimum requested gain. |
| `PeakDbfs` | `-1` | `-12..0` | Output peak ceiling. |
| `RmsWindowMs` | `250` | `32..5000` | RMS averaging window. |
| `AttackMs` | `100` | `1..5000` | Gain reduction response time. |
| `ReleaseMs` | `1000` | `1..10000` | Gain increase response time. |

Non-finite PCM values are replaced with zero even when normalization is disabled.

## HTTP API

Default local base URL:

```text
http://localhost:5050
```

OpenAI-style base URL:

```text
http://localhost:5050/v1
```

### Endpoint summary

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/v1/audio/transcriptions` | Transcribe audio in its source language. |
| `POST` | `/v1/audio/translations` | Translate speech to English. |
| `GET` | `/v1/models` | List local API model aliases. |
| `GET` | `/v1/models/{id}` | Get one API model alias. |
| `GET` | `/v1/languages` | List Whisper language codes exposed by the live client API. |
| `GET` | `/health` | Server readiness, loaded model, backend, and queue state. |
| `GET` | `/v1/health` | Same response as `/health`. |
| `GET` | `/live` | WebSocket upgrade route for live transcription. |

Swagger is enabled only when the ASP.NET environment is `Development`:

```text
http://localhost:5050/swagger
```

### `POST /v1/audio/transcriptions`

Transcribes speech without translating it.

#### Multipart request

Content type:

```text
multipart/form-data
```

Exactly one file field named `file` is required.

Supported form fields:

| Parameter | Required | Default | Description |
| --- | --- | --- | --- |
| `file` | yes | none | Audio file. Exactly one `file` field is allowed. |
| `model` | no | loaded local model | Accepted for API compatibility. It does not switch weights. |
| `language` | no | `auto` | Whisper language code or `auto`. |
| `prompt` | no | empty | Initial prompt passed to Whisper. |
| `temperature` | no | `0` | Whisper temperature from `0` to `1`. |
| `response_format` | no | `json` | `json`, `text`, `verbose_json`, `srt`, or `vtt`. |
| `stream` | no | `false` | Enables SSE when explicitly `true`. Only valid with `response_format=json`. |
| `timestamp_granularities[]` | no | segments for `verbose_json` | Repeated `segment` and/or `word` values. Only valid with `verbose_json`. |
| `timestamp_granularities` | no | same | Non-bracketed alias accepted by the local server. |
| `chunking_strategy` | no | whole-file mode | `auto` or a JSON `server_vad` object. |

Missing or empty optional values keep their defaults.

Example:

```bash
curl -F "file=@sample.wav" \
     -F "language=en" \
     -F "response_format=json" \
     http://localhost:5050/v1/audio/transcriptions
```

#### Raw audio request

As a local extension, the same endpoint also accepts a raw request body with:

```text
Content-Type: audio/*
```

or:

```text
Content-Type: application/octet-stream
```

Parameters are passed through the query string:

```bash
curl -H "Content-Type: audio/wav" \
     --data-binary @sample.wav \
     "http://localhost:5050/v1/audio/transcriptions?language=en&response_format=json"
```

With raw input and explicit `chunking_strategy=auto`, FFmpeg, VAD, and Whisper can operate while the request body is still arriving.

Without a chunking strategy, the server waits for end-of-file and processes the completed recording as one block.

Multipart uploads are always spooled to a temporary file first so fields can appear in any form order. Multipart inference begins after the complete form has been received.

#### Chunking strategy

When `chunking_strategy` is omitted or empty:

```text
whole completed recording -> one Whisper block
```

When set to:

```text
auto
```

the active server VAD profile is used.

Example:

```bash
curl -F "file=@lecture.wav" \
     -F "chunking_strategy=auto" \
     http://localhost:5050/v1/audio/transcriptions
```

A per-request VAD override can be supplied as JSON:

```json
{
  "type": "server_vad",
  "prefix_padding_ms": 384,
  "silence_duration_ms": 800,
  "threshold": 0.5
}
```

Supported `server_vad` fields:

| Field | Valid range | Description |
| --- | --- | --- |
| `type` | `server_vad` | Required strategy type. |
| `silence_duration_ms` | `32..5000` | Overrides the profile pause duration. |
| `prefix_padding_ms` | `0..5000` | Overrides the profile prefix padding. |
| `threshold` | `0..1` | Overrides the speech threshold. |

Missing fields inherit values from the active VAD profile.

Multipart clients may also submit individual fields:

```text
chunking_strategy[type]=server_vad
chunking_strategy[prefix_padding_ms]=384
chunking_strategy[silence_duration_ms]=800
chunking_strategy[threshold]=0.5
```

Dotted names such as `chunking_strategy.threshold` are also accepted.

Do not submit both the JSON strategy and individual strategy fields in the same request.

#### Timestamp granularities

Timestamps are available only with:

```text
response_format=verbose_json
```

Segment timestamps:

```bash
curl -F "file=@sample.wav" \
     -F "response_format=verbose_json" \
     -F "timestamp_granularities[]=segment" \
     http://localhost:5050/v1/audio/transcriptions
```

Word timestamps:

```bash
curl -F "file=@sample.wav" \
     -F "response_format=verbose_json" \
     -F "timestamp_granularities[]=word" \
     http://localhost:5050/v1/audio/transcriptions
```

Both:

```bash
curl -F "file=@sample.wav" \
     -F "response_format=verbose_json" \
     -F "timestamp_granularities[]=segment" \
     -F "timestamp_granularities[]=word" \
     http://localhost:5050/v1/audio/transcriptions
```

If `verbose_json` is requested without any explicit timestamp granularity, segment timestamps are included by default.

Word timestamps use token timing and add inference work.

### `POST /v1/audio/translations`

Runs Whisper's speech-to-English translation task.

Supported fields:

| Parameter | Required | Default | Description |
| --- | --- | --- | --- |
| `file` | yes | none | Audio file. |
| `model` | no | loaded local model | Accepted for compatibility. Does not switch weights. |
| `prompt` | no | empty | Initial Whisper prompt. |
| `temperature` | no | `0` | Whisper temperature from `0` to `1`. |
| `response_format` | no | `json` | `json`, `text`, `verbose_json`, `srt`, or `vtt`. |

Translation does not accept:

- `language`;
- `stream`;
- `chunking_strategy`;
- `timestamp_granularities[]`.

Example:

```bash
curl -F "file=@speech.wav" \
     -F "response_format=json" \
     http://localhost:5050/v1/audio/translations
```

### Response formats

#### `json`

```json
{
  "text": "Recognized speech."
}
```

#### `text`

Returns UTF-8 `text/plain`.

#### `verbose_json`

Base response:

```json
{
  "task": "transcribe",
  "language": "English",
  "duration": 4.21,
  "text": "Recognized speech.",
  "segments": []
}
```

For translation:

```json
{
  "task": "translate",
  "language": "english",
  "duration": 4.21,
  "text": "Translated speech.",
  "segments": []
}
```

Segment objects may contain:

```json
{
  "id": 0,
  "seek": 0,
  "start": 0.0,
  "end": 2.4,
  "text": "Recognized speech.",
  "tokens": [],
  "temperature": 0.0,
  "avg_logprob": 0.0,
  "compression_ratio": 0.0,
  "no_speech_prob": 0.0
}
```

Word timing objects contain:

```json
{
  "word": "speech",
  "start": 1.4,
  "end": 1.8
}
```

Fields that were not requested are omitted.

#### `srt`

Returns SubRip subtitles with Whisper segment timestamps.

#### `vtt`

Returns WebVTT subtitles with Whisper segment timestamps.

### SSE transcription

SSE is available only for transcription:

```text
POST /v1/audio/transcriptions
response_format=json
stream=true
```

Example:

```bash
curl -N \
     -F "file=@sample.wav" \
     -F "stream=true" \
     -F "chunking_strategy=auto" \
     http://localhost:5050/v1/audio/transcriptions
```

Delta event:

```text
data: {"type":"transcript.text.delta","delta":"Recognized text"}
```

Final event:

```text
data: {"type":"transcript.text.done","text":"Recognized text"}
```

If an error occurs after the SSE response has started, the server sends an SSE `error` event containing an OpenAI-style error object.

`stream=true` is a local extension for the `whisper-1` alias. OpenAI's hosted `whisper-1` behavior should not be inferred from this implementation.

### Model aliases

`GET /v1/models` exposes two API identifiers:

```text
whisper-1
gpt-4o-transcribe
```

Both identifiers point to the same locally loaded GGML Whisper weights.

`gpt-4o-transcribe` is a compatibility alias for clients that expect a streaming-capable transcription model name. It does not mean that GPT-4o weights or GPT-specific features are loaded.

The `model` field on audio requests is accepted for compatibility but does not change the loaded model.

Example:

```bash
curl http://localhost:5050/v1/models
```

A model entry includes:

```json
{
  "id": "whisper-1",
  "object": "model",
  "created": 0,
  "owned_by": "local",
  "loaded_model": "ggml-small.bin",
  "model_family": "small",
  "multilingual": true,
  "supports_streaming": true
}
```

`created` is derived from the local model file modification time.

### Health

Routes:

```text
GET /health
GET /v1/health
```

Both return the same readiness information.

Example shape:

```json
{
  "status": "ok",
  "model": "whisper-1",
  "loaded_model": "ggml-small.bin",
  "model_family": "small",
  "multilingual": true,
  "backend": "Vulkan",
  "queue": {
    "available": 4,
    "waiting": 0
  }
}
```

The endpoint is registered only after dependency checks, model loading, and warm-up have completed.

`model_family` and `multilingual` are inferred from the GGML header. They identify architecture metadata, not the source, training history, or exact checkpoint identity of arbitrary model weights.

### Languages

Route:

```text
GET /v1/languages
```

Response:

```json
{
  "data": [
    {
      "code": "auto",
      "name": "Auto detect"
    },
    {
      "code": "en",
      "name": "English"
    }
  ]
}
```

The complete list is generated from the language codes supported by Whisper.net.

### Request limits and errors

The Kestrel request body limit is:

```text
512 MiB
```

Multipart form rules:

- exactly one file field named `file`;
- empty files are rejected;
- multipart boundary length is limited;
- individual text fields are limited to 32 KiB;
- unknown parameters are rejected instead of ignored.

Common HTTP responses:

| Status | Meaning |
| --- | --- |
| `400` | Invalid or unsupported request option. |
| `415` | Request is neither multipart form data nor supported raw audio content type. |
| `429` | Fixed-window HTTP rate limit exceeded. |
| `499` | Client cancelled after request processing began. |
| `503` | Request queue full or queue wait timed out. |
| `500` | Unexpected server-side transcription failure. |

Validation errors use this shape:

```json
{
  "error": {
    "message": "Description of the error.",
    "type": "invalid_request_error"
  }
}
```

Options requiring hosted GPT capabilities, such as speaker diarization or GPT-only metadata, are not implemented by the local GGML Whisper backend and should not be treated as supported.

## Live WebSocket API

Route:

```text
GET /live
```

The request must upgrade to WebSocket. A normal HTTP request receives status `426 Upgrade Required`.

### Query parameters

| Parameter | Required | Default | Description |
| --- | --- | --- | --- |
| `language` | no | `auto` | Whisper language code or `auto`. |
| `translate` | no | `false` | `true` runs Whisper speech-to-English translation. |
| `audio_format` | no | encoded stream | Omit for an FFmpeg-detectable encoded stream, or use `pcm_f32le` for raw float PCM. |
| `sample_rate` | with `pcm_f32le` | none | Required for raw PCM. Valid range: `8000..192000`. |

Example browser-style connection:

```text
ws://localhost:5050/live?language=auto&translate=false&audio_format=pcm_f32le&sample_rate=48000
```

For remote microphone access from a browser, use HTTPS and WSS because microphone capture requires a secure browser context. `localhost` is allowed over HTTP by browsers.

### Client protocol

After accepting the connection, the server sends:

```json
{
  "type": "session.ready"
}
```

Audio is then sent as WebSocket binary messages.

For `pcm_f32le`, the binary payload must contain:

- little-endian float32 samples;
- mono audio;
- the sample rate declared in the query string.

For encoded mode, binary messages form one continuous encoded audio stream consumed by FFmpeg.

To end input, send this WebSocket text message:

```text
stop
```

No other control message is accepted.

### Server events

Incremental text:

```json
{
  "type": "transcript.text.delta",
  "delta": "Recognized text",
  "index": 0,
  "start": 0.3,
  "end": 2.7
}
```

`start` and `end` are approximate positions in the source audio timeline. They are not inference completion times.

Final result:

```json
{
  "type": "transcript.text.done",
  "text": "Complete recognized text.",
  "duration": 8.42,
  "language": "English"
}
```

Error:

```json
{
  "type": "error",
  "message": "Error description."
}
```

The server closes a successful session after the final result.

Live sessions consume the same request-slot pool and global Whisper inference pool as HTTP requests.

## Browser journal

Open:

```text
http://localhost:5050/
```

The included browser application:

- captures the microphone;
- streams mono float32 PCM to `/live`;
- shows incremental transcript text;
- can switch between transcription and translation before recording starts;
- lets the user select automatic or explicit source language;
- stores the preferred language and theme in `localStorage`;
- stores journal recordings in IndexedDB;
- supports copying, downloading, and deleting saved journal sessions.

The server does not store completed journal entries.

The browser requests microphone capture without browser noise suppression, echo cancellation, or automatic gain control. A browser may ignore unsupported media constraints.

## Concurrency and rate limiting

There are three separate concurrency controls.

### Active request slots

`ServerSecurity:MaxConcurrentRequests` limits active HTTP and WebSocket pipelines.

Additional requests can wait in the FIFO request queue controlled by:

```text
MaxQueuedRequests
QueueWaitSeconds
```

The request body is not read until an HTTP request obtains a slot.

### Whisper workers per request

`SttSettings:WhisperWorkers` controls how many Whisper processors one request may use for completed VAD segments.

Results are returned in source audio order even when later segments finish first.

The pipeline keeps at most approximately twice the worker count of unfinished segment work per request, providing backpressure.

### Global Whisper inference limit

`ServerSecurity:MaxConcurrentWhisper` limits simultaneous native Whisper inference across the entire process.

For example:

```json
{
  "ServerSecurity": {
    "MaxConcurrentWhisper": 1
  },
  "SttSettings": {
    "WhisperWorkers": 2
  }
}
```

creates two per-request workers, but only one can execute Whisper inference at a time.

For strict sequential inference, set both values to `1`.

Multiple simultaneous Vulkan inference jobs may increase VRAM use and do not necessarily improve throughput. Benchmark the target GPU and driver before increasing these values.

### Fixed-window HTTP rate limiting

The HTTP transcription and translation routes can also be limited by:

```text
EnableRateLimiting
RateLimitMaxRequests
RateLimitWindowSeconds
```

This limit is independent of request concurrency and queueing.

The `/live` WebSocket route uses request-slot admission but is not mapped through the fixed-window HTTP endpoint limiter.

## Building

### Development build

```bash
dotnet build -c Release
```

Run:

```bash
dotnet run -c Release
```

A direct source build does not download or bundle FFmpeg automatically. Provide a working FFmpeg executable either:

- on `PATH`; or
- next to the built application as `ffmpeg.exe` on Windows or `ffmpeg` on Linux.

### Direct publish

Windows x64:

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

Linux x64:

```bash
dotnet publish -c Release -r linux-x64 --self-contained false
```

Direct `dotnet publish` does not package FFmpeg. Use the release script for distributable archives.

### Build Windows and Linux release archives

From `STT_Runner`:

```powershell
./Build-Releases.ps1
```

or:

```bash
pwsh ./Build-Releases.ps1
```

The script builds both:

```text
win-x64
linux-x64
```

By default they are self-contained and include the .NET runtime.

Output is written under a new directory:

```text
Builds/<timestamp>-<id>/
```

The directory contains:

```text
MwandishiSTT-win-x64.zip
MwandishiSTT-linux-x64.zip
```

The script:

1. publishes the .NET application;
2. obtains pinned Windows and Linux x64 LGPL FFmpeg builds;
3. verifies the FFmpeg archive SHA-256 values against the release checksum manifest;
4. copies the FFmpeg executable and available legal files;
5. writes FFmpeg provenance information;
6. verifies required release files and Vulkan native runtime files;
7. creates the final ZIP archives;
8. preserves executable permissions in the Linux ZIP.

The release archives intentionally do not contain model weights.

### Release script parameters

```powershell
./Build-Releases.ps1 `
    -Configuration Release `
    -WindowsFFmpegArchive "path/to/windows.zip" `
    -LinuxFFmpegArchive "path/to/linux.tar.xz" `
    -FFmpegChecksumsFile "path/to/checksums.sha256" `
    -FrameworkDependent
```

| Parameter | Default | Description |
| --- | --- | --- |
| `-Configuration` | `Release` | .NET build configuration. |
| `-WindowsFFmpegArchive` | empty | Reuses an existing exact Windows FFmpeg archive instead of downloading it. |
| `-LinuxFFmpegArchive` | empty | Reuses an existing exact Linux FFmpeg archive instead of downloading it. |
| `-FFmpegChecksumsFile` | empty | Uses a local checksum manifest instead of downloading the upstream one. |
| `-FrameworkDependent` | off | Produces framework-dependent output without bundling the .NET runtime or native app host. |

For framework-dependent output, start the application with:

```bash
dotnet MwandishiSTT.dll
```

The currently pinned FFmpeg release script targets x64 Windows and Linux LGPL builds.

## Validation and tests

API contract checks:

```bash
python3 Tests/api_contract.py --base-url http://127.0.0.1:5050
```

Long streaming test:

```bash
python3 Tests/long_stream.py
```

WebSocket test:

```bash
python3 Tests/live_websocket.py
```

Raw PCM live test:

```bash
python3 Tests/pcm_live.py --sample path/to/recording.mp3
```

Disconnect and queue cleanup test:

```bash
python3 Tests/disconnect_cleanup.py
```

Signal-level checks without loading Whisper:

```bash
dotnet run --project Tests/SignalChecks.csproj -c Release
```

Model download and path checks:

```bash
dotnet run --project Tests/ModelChecks/ModelChecks.csproj -c Release
```

Browser PCM worklet checks:

```bash
node Tests/pcm_worklet.cjs
```

Browser language behavior checks:

```bash
node Tests/browser_language.cjs
```

The Python and Node.js test scripts are development checks only. They are not runtime dependencies of the server.

## Security notes

The default configuration is intended for a trusted local network or local machine, not for direct public Internet exposure.

Important defaults:

- the server has no built-in authentication;
- Kestrel listens on all interfaces with `http://+:5050`;
- HTTP CORS allows any origin;
- `/live` accepts WebSocket clients without origin-based access control.

To bind only to the local machine:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://localhost:5050"
      }
    }
  }
}
```

To restrict browser HTTP origins:

```json
{
  "ServerSecurity": {
    "CorsAllowAnyOrigin": false,
    "CorsAllowedOrigins": [
      "https://your-client.example"
    ]
  }
}
```

CORS is not authentication and does not secure the WebSocket route or non-browser clients.

For remote exposure, place the service behind an authenticated HTTPS reverse proxy or enforce access with a firewall.

## Troubleshooting

### `FFmpeg was not found`

A source build requires a working FFmpeg executable.

Check:

```bash
ffmpeg -version
```

or place `ffmpeg.exe` / `ffmpeg` next to the application.

### GPU requested but CPU is used

Check the startup log or:

```bash
curl http://localhost:5050/health
```

If `backend` reports CPU, verify the Vulkan driver and selected device.

The project does not require the Vulkan SDK, but the operating system still needs a working Vulkan loader and GPU driver.

### Startup fails because a model is missing

Either enable automatic download:

```json
{
  "AutoDownload": {
    "Enable": true
  }
}
```

or provide exact local paths:

```json
{
  "SttSettings": {
    "ExactWhisperFilePath": "D:/Models/ggml-small.bin",
    "ExactVadFilePath": "D:/Models/silero_vad.onnx"
  }
}
```

### Custom model URL does not download

`AutoDownload:WhisperUrl` and `AutoDownload:VadUrl` must be complete HTTP or HTTPS file URLs.

Changing only `WhisperModelName` or `VadModelName` changes the local destination name, not the source URL.

### Short speech is detected in the wrong language

Automatic language detection has little context for very short phrases. Send an explicit Whisper language code:

```bash
curl -F "file=@sample.wav" \
     -F "language=uk" \
     http://localhost:5050/v1/audio/transcriptions
```

### Long completed files fail in whole-file mode

Without `chunking_strategy`, the completed recording is buffered as one block and is limited by `VadSettings:Profiles:<active>:MaxBufferedSegmentSeconds`.

For long recordings, either:

- increase that safety limit; or
- use `chunking_strategy=auto`.

### Remote browser cannot access the microphone

Browsers require a secure context for microphone capture.

Use:

- `http://localhost:5050` on the same machine; or
- HTTPS and WSS for remote access.

## Third-party components

Direct project dependencies include:

| Component | Version | Purpose |
| --- | --- | --- |
| .NET | 10 | Web server and application runtime |
| Microsoft.ML.OnnxRuntime | 1.25.1 | Silero VAD inference |
| Whisper.net | 1.9.1 | Managed Whisper API |
| Whisper.net.Runtime | 1.9.1 | CPU native Whisper runtime |
| Whisper.net.Runtime.Vulkan | 1.9.1 | Vulkan native Whisper runtime |
| Swashbuckle.AspNetCore | 10.1.7 | Swagger/OpenAPI UI and generation |
| Microsoft.AspNetCore.OpenApi | 10.0.7 | ASP.NET OpenAPI integration |
| Microsoft.OpenApi | 2.7.5 | OpenAPI model support |
| FFmpeg | pinned by `Build-Releases.ps1` | Audio decoding and resampling |

The default Whisper and Silero models are downloaded separately and are not included in release archives.

See `THIRD-PARTY-NOTICES.md` and the `Licenses/` directory before redistributing packaged releases.
