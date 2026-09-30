# SmartStack — Tsubaki TTS Engine and Local Speech Tools

SmartStack is a C#/.NET monorepo focused on local speech processing without Python-heavy runtime stacks.
Its main project is **Tsubaki TTS Engine**: a local Text-to-Speech server built around Piper and OpenVoice V2, with voice cloning, streaming audio, DSP effects, and an OpenAI-compatible API for Windows and Linux.

The repository also contains **Mwandishi STT**, an experimental local Speech-to-Text server showing how Whisper, Silero VAD, streaming transcription, and a browser speech journal can be used for applications beyond AI assistants.

## Tsubaki TTS Engine

**Local TTS, Piper, OpenVoice V2 voice cloning, OpenAI-compatible API, real-time streaming, DSP effects, Windows and Linux.**

Tsubaki is designed for local AI companions, SillyTavern, VTubers, games, visual novels, accessibility tools, voice-enabled applications, and other projects that need fast local speech synthesis without a cloud service.

Key features include:

- Piper-based local speech synthesis
- Zero-shot voice cloning with OpenVoice V2
- OpenAI-compatible `/v1/audio/speech` API
- Dedicated Tsubaki API with pitch, volume, cloning and DSP controls
- Streaming audio generation
- Built-in web dashboard
- CPU, DirectML, CUDA and WebGPU/Vulkan build options
- Windows and Linux support
- No Python runtime required

**Download:**
- [GitHub Releases](https://github.com/MrHryhorii/SmartStack/releases)
- [Tsubaki TTS Engine on itch.io](https://hinotsuba.itch.io/tsubaki-tts-engine)

**Documentation and source:**
- [Tsubaki TTS Engine documentation](https://github.com/MrHryhorii/SmartStack/tree/main/ONNX_Runner)

## Mwandishi STT

Mwandishi STT is a local .NET speech transcription and translation server built with Whisper.net, FFmpeg and Silero VAD. It provides OpenAI-style transcription endpoints, SSE streaming, a live WebSocket interface, and a browser-based speech journal.

It is intended primarily as a reference and experimental implementation for local transcription, dictation, speech journals, game tools, accessibility features, media processing, and other applications that need local Speech-to-Text.

Unlike Tsubaki, STT quality depends heavily on the selected Whisper model and VAD configuration. A small model is convenient for testing, but it can misrecognize short phrases, mixed-language speech and difficult recordings. Larger Whisper models generally require more memory and inference time, while VAD thresholds, pause duration, padding and segmentation must be tuned for the actual microphone, language and speaking style.

Do not treat the default VAD profile or model size as universal production settings. Test whole-file transcription and segmented transcription on representative audio before deployment.

**Documentation and source:**
- [Mwandishi STT documentation](https://github.com/MrHryhorii/SmartStack/tree/main/STT_Runner)

No standalone Mwandishi STT binary release is currently published in the repository; build instructions are included in its documentation.

## Repository

- [SmartStack source code](https://github.com/MrHryhorii/SmartStack)
- [Tsubaki TTS Engine](https://github.com/MrHryhorii/SmartStack/tree/main/ONNX_Runner)
- [Mwandishi STT](https://github.com/MrHryhorii/SmartStack/tree/main/STT_Runner)

Both projects are designed to run locally and keep speech processing on the user's machine.
