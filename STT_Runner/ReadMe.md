# Simple Whisper STT Runner 🎙️🚀

## **Simple Whisper STT Runner** is a high-performance local backend server for Speech-to-Text, built on .NET 10.

The project is designed as a lightweight, private alternative to cloud solutions, providing an OpenAI-compatible API for audio transcription and translation.

This is a pure **Headless Service (Backend Only)**. It does not include a frontend, and interaction with the system as well as endpoint testing is performed via the built-in **Swagger UI**.

## 🛠 Tech Stack

**Runtime:** .NET 10 (C#)  
**STT Engine:** Whisper.net using the **Vulkan** runtime for cross-platform GPU acceleration (Windows/Linux)  
**VAD Engine:** Microsoft.ML.OnnxRuntime with the **Silero VAD v5** model  
**Media Processing:** **FFmpeg** (automatic download and configuration) for decoding any media containers

## 🚀 Key Features

### 1. Audio and Video Container Processing

Thanks to the integrated FFmpeg-based pipeline, the server can process not only raw audio files but also any video containers (MP4, MKV, AVI, etc.).

The audio track is extracted, normalized, and passed to the neural network entirely in memory (In-Memory Piping), without creating temporary files on disk.

### 2. “Three-Body Pipeline”

To achieve minimal latency, processing is divided into three asynchronous stages connected via channels (`System.Threading.Channels`):

**Stage 1:** FFmpeg converts the input stream into 16kHz mono float32  
**Stage 2 (VAD):** Silero VAD v5 analyzes the stream in real time using recurrent memory (RNN state [2, 1, 128]) for accurate speech boundary detection  
**Stage 3 (Whisper):** As soon as VAD detects the end of a speech segment, it is immediately sent for transcription

### 3. OpenAI-Compatible API

The server supports standard routes `/v1/audio/transcriptions` and `/v1/audio/translations`, allowing it to be used as a direct replacement for the OpenAI Whisper API in existing applications (e.g., in combination with local LLMs).

## ⚠️ Technical Note: GPU “Cold Start”

This project follows the principle of **clean code without hacks**. This means no artificial background load is used to keep the GPU active.

**Consequence:**

Due to aggressive power-saving behavior of modern GPU drivers (especially when using Vulkan), the GPU may enter sleep mode after ~30 seconds of inactivity.

In such cases, **the first request after idle may experience a 2–3 second delay**, required to "wake up" the GPU and reinitialize the compute graph.

During active usage, when pauses between requests are shorter than 30 seconds, processing remains effectively instant.

## 💡 Use Cases

**Local AI Assistants:** Providing a voice interface for smart home systems with full privacy (data never leaves your server)

**Automated Video Transcription:** Fast generation of transcripts or subtitles for video files of any size

**Private Chatbots:** Using it as a Speech-to-Text module for corporate LLM systems where data security is critical

**Media Archive Tools:** Indexing audio and video content for keyword-based search

## ⚙️ Getting Started

- Specify the model path in `appsettings.json` (or allow the system to download them automatically from Hugging Face)
- Run the project — FFmpeg will be configured automatically on first startup
- Open the Swagger page (usually http://localhost:5050/swagger) to test the API

---

_Created with focus on performance, memory safety, and architectural integrity._
