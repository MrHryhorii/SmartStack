# STT Runner

A local .NET 10 speech transcription and translation server. It reads uploads incrementally, decodes with FFmpeg, segments speech with Silero VAD, and sends completed segments to Whisper while the rest of the upload is still arriving. The API returns the complete transcript after the input ends. A file uploaded in one request uses the same pipeline.

## Requirements

- .NET 10 SDK to build, or the corresponding runtime for a framework-dependent deployment.
- FFmpeg on `PATH` or in the application directory. If neither exists, automatic download is attempted.
- A GGML Whisper model and the matching Silero ONNX model, configured in `appsettings.json` or downloaded on first launch.
- For Vulkan inference: a working Vulkan loader and GPU driver on Windows x64 or Linux x64.

The project references `Whisper.net.Runtime.Vulkan` version `1.9.1`. Its published NuGet package already includes five native Linux x64 `.so` files and copies them to `runtimes/vulkan/linux-x64` during build and publish. No custom Whisper runtime build, Vulkan SDK, or CUDA installation is needed to run the published application. The `Whisper.net.Runtime` package provides CPU fallback. The server logs the runtime selected by Whisper.net; `UseGpu=true` alone does not prove GPU inference.

To publish for Linux x64:

```bash
dotnet publish -c Release -r linux-x64 --self-contained false
```

Check that `runtimes/vulkan/linux-x64` in the publish directory contains the five `.so` files. Test on a Linux machine with a working Vulkan driver: publishing successfully does not prove that the target GPU will execute inference.

## API

`POST /v1/audio/transcriptions` returns recognized text in the source language. `POST /v1/audio/translations` translates speech to English. Both accept OpenAI-style `multipart/form-data` with one file field named `file` and optional `model` and `response_format` fields (`json`, `text`, `verbose_json`). Transcriptions also accept `language`. Put `language` before `file` so recognition can start as soon as the audio arrives. Fields after `file` can supply `response_format`, but cannot change the language of segments already processed.

```bash
curl -F language=en -F file=@sample.wav -F response_format=json http://localhost:5050/v1/audio/transcriptions
```

Raw audio is also accepted as the request body with `Content-Type: audio/*` or `application/octet-stream`; pass `language` and `response_format` as query parameters:

```bash
curl -H 'Content-Type: audio/wav' --data-binary @sample.wav 'http://localhost:5050/v1/audio/transcriptions?language=en'
```

The client can upload incrementally using chunked transfer. By default the server collects ordered text and returns the selected `response_format` at end of input. Set `SttSettings:StreamResponse` to `true` to return a `text/plain; charset=utf-8` response instead: each recognized speech segment is written and flushed as soon as Whisper finishes it. The HTTP response completes after all input is processed (the underlying HTTP keep-alive connection may remain open). In this mode `response_format` is ignored, so an OpenAI SDK expecting JSON should use the default mode. A proxy may buffer streaming output unless configured otherwise. For example, use `curl -N` to display chunks as they arrive.

VAD splits on roughly 800 ms of silence. `SttSettings:MaxSegmentSeconds` is `0` by default: no duration-based segmentation occurs. Set it to `30` to split continuous speech approximately every 30 seconds; adjacent long segments retain a short overlap, so words at a split may repeat. A continuous speech segment produces no text until it ends or reaches a configured forced split. `SttSettings:MaxBufferedSegmentSeconds` defaults to `600`: if a segment exceeds this memory safety limit, the request fails rather than growing without bound. The first ~192 ms of speech are preserved as pre-roll. The request body limit is 512 MiB.

`ServerSecurity:MaxConcurrentRequests` limits simultaneous uploads. `ServerSecurity:MaxConcurrentWhisper` limits inference on the shared GPU separately. The default is one Whisper inference at a time; tune only after measuring on your hardware.

Up to `ServerSecurity:MaxQueuedRequests` additional HTTP requests wait in a first-in-first-out queue before their bodies are read. The default queue holds eight requests, with a `QueueWaitSeconds` limit of 120 seconds. A full queue or an expired wait returns HTTP 503; a disconnected client leaves the queue. Set `MaxQueuedRequests` to `0` for immediate rejection when all active slots are occupied. The separate fixed-window rate limit still returns HTTP 429 when its request budget is exhausted.

The Whisper factory and VAD session remain loaded for the application's lifetime. At startup, before accepting requests, the server transcribes and translates the included eight-second spoken sample to exercise both Whisper tasks and warm the selected backend. `SttSettings:WarmUpAudioFile` can point to another 16 kHz WAV (1–30 seconds). The bundled `Assets/warmup.wav` is a 16 kHz mono conversion of [French Canadian Woman Giving Instructions 04.wav](https://freesound.org/people/vero.marengere/sounds/514877/) by vero.marengere, licensed CC0. This primes shader paths used by the sample; a different model, device, language, or audio shape may still cause some first-use work. Startup takes longer because warm-up runs before the server starts listening.

## Configuration

`appsettings.json` controls model paths, download behavior, CORS, segmentation, streaming responses, and concurrency. `UseGpu=false` forces the Whisper CPU runtime. Swagger is enabled in the development environment at `/swagger`; the curl examples above show streaming multipart and raw requests directly.
