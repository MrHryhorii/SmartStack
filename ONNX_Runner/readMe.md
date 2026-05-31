# Tsubaki TTS Engine

Production-grade local Text-to-Speech server for AI agents, companions, VTubers, and OpenAI-compatible applications.

Built with **C# (.NET 10)**, powered by **Piper (VITS)** neural networks and **OpenVoice V2**. Generates high-fidelity audio with support for instant voice cloning and real-time DSP effects.

- OpenAI-compatible API — drop-in replacement for existing tools
- Zero-shot voice cloning via OpenVoice V2
- Real-time streaming (Chunked Transfer Encoding)
- Studio-grade DSP effects and spatial environments
- No Python, no CUDA dependency hell
- Windows and Linux support

---

## Web Dashboard

![Dashboard preview](tsubaki-tts.png)

Built-in web interface for testing voices and DSP effects, available immediately after launch.

Dashboard features:

- voice selection and testing
- DSP effects and spatial environments
- real-time streaming playback
- pitch and volume control
- voice cloning validation

---

# Download

**[Tsubaki TTS Engine v1.0.4 (GitHub Releases)](https://github.com/MrHryhorii/SmartStack/releases/tag/tsubakitts-v1.0.4)**

Direct Plug-and-Play binary downloads for Windows and Linux. Includes pre-configured base models and cloneable voices.

---

# Why Tsubaki Instead of Python TTS Stacks?

Most modern open-source TTS engines are written in Python. This often leads to "dependency hell": CUDA version conflicts, gigabytes of PyTorch libraries, and virtual environment nightmares.

Tsubaki is built with an engineering-first approach to distribution:

- **No Python Required:** Runs purely on compiled C# and `Microsoft.ML.OnnxRuntime`.
- **Portable (Self-Contained):** Can be compiled into a single executable. Just download and run.
- **Dynamic Hardware Acceleration:** Automatically detects and utilizes your GPU (DirectML for Windows, CUDA for Linux) and gracefully falls back to CPU without crashing.
- **Memory Protection (OOM Guard):** Built-in queueing and semaphore system that calculates available VRAM/RAM to prevent server crashes under heavy load.
- **True Concurrency (Shared Memory):** Python servers often duplicate massive 2GB+ neural networks in RAM for every parallel worker just to bypass the GIL. Tsubaki loads the model exactly once. Multiple API requests are processed concurrently using a shared memory space, keeping RAM usage flat regardless of how many AI agents are talking at the same time.

| Tsubaki                               | Typical Python TTS                   |
| ------------------------------------- | ------------------------------------ |
| Single executable                     | Python virtual environments          |
| OpenAI-compatible out of the box      | Custom APIs required                 |
| DirectML support (NVIDIA, AMD, Intel) | Often CUDA-only                      |
| Built-in DSP effects                  | External audio processing chains     |
| Real-time streaming                   | Full audio generated before playback |
| CPU-first deployment                  | GPU dependency pressure              |
| Portable self-contained builds        | Fragile installations                |
| True multithreading (Shared RAM)      | GIL bottleneck / Memory duplication  |

---

# Key Features

| Feature                    | Description                                                                                                                                                        |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| OpenAI API Compatible      | Exposes a `/v1/audio/speech` endpoint that perfectly mimics the official OpenAI API. Drop-in replacement for SillyTavern, LangChain, AutoGen, and other AI agents. |
| Zero-Shot Voice Cloning    | Integrated with the OpenVoice V2 architecture. Clone any voice instantly by dropping a clean 10-second `.wav` file into the `Voices` folder.                       |
| Foreign Word Pronunciation | Offline language detection via Lingua. Detects foreign words and applies phoneme approximation for natural accented pronunciation.                                 |
| Studio-Grade DSP Effects   | Real-time audio effects (Telephone, Overdrive, Reverb, etc.), pitch and volume shifting.                                                                           |
| Real-Time Streaming        | Supports Chunked Transfer Encoding — listen to audio before generation is complete.                                                                                |
| Built-in Web Dashboard     | Sleek, user-friendly web interface available out-of-the-box for testing voices and effects.                                                                        |
| OOM Guard                  | Built-in queueing and semaphore system that prevents VRAM/RAM crashes under heavy load.                                                                            |
| No Python Required         | Pure C# and ONNX Runtime. Automatically detects GPU or falls back to CPU.                                                                                          |

---

# Who Is This For?

Tsubaki is designed for:

- AI companions and chatbots
- SillyTavern users
- AI VTubers
- local LLM stacks (Open WebUI, Ollama frontends)
- LangChain / AutoGen pipelines
- home AI servers
- any OpenAI-compatible application that needs local TTS

---

# Quick Start

## 1. Download the Release

Download the latest binary for your OS:

https://github.com/MrHryhorii/SmartStack/releases

---

## 2. Download a Piper Voice Model

All official Piper voices are hosted on HuggingFace:

https://huggingface.co/rhasspy/piper-voices/tree/main

For each voice you need to download exactly **2 files**:

- `.onnx` — the neural network weights (the large file)
- `.onnx.json` — metadata: sample rate, phonemes, speaker IDs

Place both files into the `Model/` folder next to the executable.

> Both files are required. The engine will fail to load without the accompanying `.json` config.

---

## 3. Run the Server

Start the executable. The browser dashboard opens automatically at:

```
http://localhost:5045
```

---

## 4. Test Speech Synthesis

```bash
curl http://localhost:5045/v1/audio/speech \
  -H "Content-Type: application/json" \
  -d '{
    "model": "tts-1",
    "input": "Hello from Tsubaki TTS.",
    "voice": "piper_base",
    "response_format": "mp3"
  }'
```

That is enough for a complete first launch.

---

# OpenAI API Compatibility

Tsubaki mimics the standard OpenAI `/v1/audio/speech` endpoint. Any tool or agent that knows how to talk to the OpenAI API can talk to Tsubaki immediately — just change the base URL.

Compatible with:

- SillyTavern
- Open WebUI
- LangChain
- AutoGen
- any custom OpenAI client

## Standard Request

```bash
curl http://localhost:5045/v1/audio/speech \
  -H "Content-Type: application/json" \
  -d '{
    "model": "tts-1",
    "input": "Text to synthesize.",
    "voice": "piper_base",
    "response_format": "mp3",
    "speed": 1.0
  }'
```

## Standard Parameters

| Field             | Type   | Description                                                                   |
| ----------------- | ------ | ----------------------------------------------------------------------------- |
| `model`           | string | Any value (e.g. `"tts-1"`) — ignored, present for compatibility               |
| `input`           | string | Text to synthesize                                                            |
| `voice`           | string | `piper_base` for the base Piper voice, or a cloned voice name (e.g. `"John"`) |
| `response_format` | string | `mp3`, `wav`, `opus`, `pcm`                                                   |
| `speed`           | float  | Speech speed multiplier. `1.0` is default.                                    |

---

# Real-Time Streaming

Tsubaki supports HTTP chunked streaming. Audio playback can begin before the full synthesis finishes — useful for AI companions, streaming agent pipelines, and real-time conversations.

Enable streaming per request:

```json
{
  "stream": true
}
```

Recommended server-side streaming configuration in `appsettings.json`:

```json
"StreamSettings": {
  "EnableStreaming": true,
  "FlushAfterEachSentence": true,
  "MinChunkSizeKb": 8
}
```

`FlushAfterEachSentence: true` means the client receives each synthesized sentence immediately as it is generated, rather than waiting for the full response. This is recommended for AI companion backends and real-time agent systems.

> WAV format does not support true chunked streaming because its header requires the total file size to be written upfront. For streaming, use `mp3` or `opus`.

---

# Advanced API Parameters

Tsubaki supports additional parameters beyond the standard OpenAI API. These are especially useful for **AI agents** that can mechanically convey emotional state, acoustic context, or synthesis style per request — without any changes to the server configuration.

Standard OpenAI clients (SillyTavern, AutoGen, LangChain) simply ignore these extra fields, so backward compatibility is always preserved.

A detailed **Swagger UI** with all extended parameters is available at `http://localhost:5045/swagger` when the server is running.

## Full Extended Request Example

```bash
curl http://localhost:5045/v1/audio/speech \
  -H "Content-Type: application/json" \
  -d '{
    "model": "tts-1",
    "input": "This message is coming from an old intercom.",
    "voice": "John",
    "response_format": "mp3",
    "speed": 1.0,
    "stream": true,

    "effect": "Telephone",
    "effect_intensity": 0.8,

    "environment": "ConcreteHall",
    "environment_intensity": 0.3,

    "pitch": 0.9,
    "volume": 1.5,

    "noise_scale": 0.667,
    "noise_w": 0.8,

    "clone_intensity": 0.85,
    "tone_temperature": 0.7
  }'
```

## DSP Effect Parameters

| Parameter               | Type   | Description                                                |
| ----------------------- | ------ | ---------------------------------------------------------- |
| `effect`                | string | DSP character effect to apply. See available values below. |
| `effect_intensity`      | float  | Effect intensity. `1.0` is full strength.                  |
| `environment`           | string | Acoustic spatial environment. See available values below.  |
| `environment_intensity` | float  | Reverb intensity. `0.25` is recommended.                   |

### Available Effects

| Value           | Description                                                                                              |
| --------------- | -------------------------------------------------------------------------------------------------------- |
| `None`          | Bypass — clean audio                                                                                     |
| `Telephone`     | Lo-Fi equalization with hard transistor clipping                                                         |
| `Overdrive`     | Warm tube saturation and cubic waveshaping distortion                                                    |
| `Bitcrusher`    | Retro 8-bit / Arcade style sample rate decimation                                                        |
| `RingModulator` | Classic Robot / Dalek metallic effect                                                                    |
| `Flanger`       | Modulated short delay with heavy feedback                                                                |
| `Chorus`        | Thick, multi-voice ensemble effect                                                                       |
| `LoFiTape`      | Simulates the warmth and coloration of an analog cassette tape                                           |
| `NeuralStutter` | Simulates a cognitive malfunction in the AI's neural core by intelligently looping tonal voice fragments |

### Available Environments

| Value          | Description                                                     |
| -------------- | --------------------------------------------------------------- |
| `None`         | Dry signal only                                                 |
| `LivingRoom`   | Small room with short, bright reverb                            |
| `ConcreteHall` | Large hall with long, dense reverb and strong early reflections |
| `Forest`       | Open outdoor space with long, diffuse reverb                    |
| `Underwater`   | Muffled underwater acoustic properties                          |

## Synthesis Parameters

| Parameter     | Type  | Description                                                             |
| ------------- | ----- | ----------------------------------------------------------------------- |
| `pitch`       | float | Pitch shift multiplier. `1.0` is original. `0.85` is noticeably deeper. |
| `volume`      | float | Output volume multiplier with soft-knee limiter. `1.0` is original.     |
| `noise_scale` | float | Controls pronunciation variance (intonation noise). Default: `0.667`.   |
| `noise_w`     | float | Phoneme duration variance (rhythm). Default: `0.8`.                     |
| `stream`      | bool  | Enable or disable streaming for this specific request.                  |

## Cloning Parameters

| Parameter          | Type  | Description                                                                                           |
| ------------------ | ----- | ----------------------------------------------------------------------------------------------------- |
| `clone_intensity`  | float | Latent space blend ratio between Piper base fingerprint and target voice. `0.85–0.9` recommended.     |
| `tone_temperature` | float | Variance during timbre transfer (tau). `0.7` is stable. `1.0` is more expressive but noise-sensitive. |

> **For AI agents:** These parameters can be passed dynamically per utterance — allowing an agent to mechanically express state. `"Telephone"` + `"ConcreteHall"` for a basement interrogation, `"LoFiTape"` for a flashback, higher `pitch` for tension, or tweaking `tone_temperature` to stabilize voice artifacts on the fly. Standard OpenAI clients ignore these fields silently, so backward compatibility is always preserved.

---

# Server-Side DSP Defaults

Since standard OpenAI clients (like SillyTavern) cannot send custom DSP effect parameters, Tsubaki allows you to set a **Default Effect** in `appsettings.json`. This effect will be automatically applied to all incoming API requests unless explicitly overridden by a custom client (like the built-in web dashboard).

## Default Effects & Environments

```json
"EffectsSettings": {
  "EnableGlobalEffects": true,
  "DefaultEffect": "LoFiTape",
  "DefaultIntensity": 1.0,
  "DefaultEnvironment": "LivingRoom",
  "DefaultEnvironmentIntensity": 0.25
}
```

Set `"DefaultEffect": "None"` to bypass effects entirely.

---

## Default Pitch & Volume

```json
"DspSettings": {
  "EnableLowPassFilter": true,
  "LowPassCutoffFrequency": 11000.0,
  "LowPassQFactor": 0.577,
  "DefaultPitch": 1.0,
  "DefaultVolume": 1.0
}
```

### DefaultPitch

Sets the server-wide pitch shift applied to all generated audio.

| Value  | Effect                  |
| ------ | ----------------------- |
| `0.5`  | One octave lower        |
| `0.85` | Noticeably deeper voice |
| `1.0`  | Original (no change)    |
| `1.15` | Slightly higher voice   |
| `2.0`  | One octave higher       |

**Why use it?** Standard OpenAI-compatible clients (SillyTavern, AutoGen, LangChain) have no way to send a `pitch` parameter in their requests. If the base voice or a cloned voice feels too deep or too bright for your AI assistant's character, set this once and every request will automatically use the adjusted pitch — no client changes required.

**Per-request override:** If a client explicitly sends `"pitch": 0.85` in the request body, that value takes priority and the server default is ignored for that request only.

### DefaultVolume

Sets the server-wide volume multiplier applied to all generated audio. The engine uses a **soft-knee limiter** — the gain is applied linearly up to 80% of the signal ceiling, after which a smooth algebraic curve prevents harsh digital clipping on loud peaks.

| Value  | Gain (approx.) | Practical effect                               |
| ------ | -------------- | ---------------------------------------------- |
| `0.25` | −12 dB         | Very quiet — good for mixing under other audio |
| `0.5`  | −6 dB          | Noticeably quieter                             |
| `0.71` | −3 dB          | Slightly quieter                               |
| `1.0`  | 0 dB           | Original level (no change)                     |
| `1.41` | +3 dB          | Slightly louder                                |
| `2.0`  | +6 dB          | Noticeably louder — good for quiet clones      |
| `4.0`  | +12 dB         | Maximum boost — soft-knee limiter fully active |

**Why use it?** The perceived loudness of a cloned voice is determined almost entirely by the recording level of the reference `.wav` file. If your voice sample was recorded quietly, the cloned output will also be quiet. `DefaultVolume` lets you compensate for this once in `appsettings.json` rather than adjusting every client.

**Per-request override:** If a client explicitly sends `"volume": 2.0` in the request body, that value takes priority and the server default is ignored for that request only.

---

# Voice Cloning (OpenVoice V2)

## Adding a Voice

1. Place a clean voice sample (`.wav`, 5–15 seconds) into the `Voices/` folder.
2. The filename becomes the voice ID: `John.wav` → `"voice": "John"`.
3. Use it in any API request:

```json
{
  "voice": "John"
}
```

For the base Piper voice without cloning: `"voice": "piper_base"`.

The server automatically:

- extracts the voice fingerprint on first run using the Tone Extractor model
- caches the embedding as a `.voice` file next to the sample
- loads the cached fingerprint instantly on every subsequent run

## Recommended Sample Quality

- 5–15 seconds of clean speech
- minimal background noise
- no music, no reverb
- no clipping — if the sample peaks above 0 dBFS, the cloned output will also clip and distort
- **Recommended peak level: around −6 to −3 dBFS** — loud enough to fully capture the voice character, with just enough headroom to avoid distortion

## OpenVoice Cloning Models

The voice cloning engine requires separate OpenVoice ONNX models. Tsubaki downloads them **automatically from HuggingFace on the first run** — no manual setup needed.

If you prefer to download them manually:

**[Hinotsuba/OpenVoice-ONNX-v2 on HuggingFace](https://huggingface.co/Hinotsuba/OpenVoice-ONNX-v2)**

Place all three files into the `Cloner/` folder:

| File                | Description                                                                 |
| ------------------- | --------------------------------------------------------------------------- |
| `tone_extract.onnx` | Extracts a 256-dimensional voice fingerprint from a reference audio sample  |
| `tone_color.onnx`   | Transfers the extracted voice characteristics onto the generated base audio |
| `tone_config.json`  | Hyperparameters and structural configuration for both models                |

> These models are released under the **MIT License** and are free for commercial use.

---

# Piper Voice Models

## Finding Voice Models

All official Piper voices are hosted on HuggingFace:

**[rhasspy/piper-voices on HuggingFace](https://huggingface.co/rhasspy/piper-voices/tree/main)**

The repository contains **35 languages**, each in its own folder (`en`, `de`, `fr`, `uk`, `zh`, etc.).

## What to Download

For each voice you need to download exactly **2 files**:

| File          | Extension    | Description                                  |
| ------------- | ------------ | -------------------------------------------- |
| Model weights | `.onnx`      | The neural network — this is the large file  |
| Model config  | `.onnx.json` | Metadata: sample rate, phonemes, speaker IDs |

### How to Download a Voice

1. Browse to your language folder, e.g. [`/en`](https://huggingface.co/rhasspy/piper-voices/tree/main/en)
2. Navigate into a voice subfolder (e.g. `en_US/lessac/medium/`)
3. Download both files: `en_US-lessac-medium.onnx` and `en_US-lessac-medium.onnx.json`
4. Place both files into the `Model/` folder next to the executable

> Both files **must be present** — the engine will fail to load without the accompanying `.json` config.

## Available Quality Tiers

Most voices come in multiple quality levels. Higher quality = larger file and more VRAM:

| Quality  | Approx. Size | Notes                            |
| -------- | ------------ | -------------------------------- |
| `x_low`  | ~5 MB        | Fast, lower fidelity             |
| `low`    | ~15 MB       | Good for low-end hardware        |
| `medium` | ~60 MB       | Recommended for most use cases   |
| `high`   | ~130 MB      | Best quality, requires more VRAM |

> **For voice cloning, the `high` quality tier (22050 Hz) is strongly recommended.** Its fuller frequency spectrum allows the OpenVoice neural network to operate without producing instability artifacts such as trembling or "crying" effects that are common on `medium` (16 kHz) models.

---

# Installation & Model Management

## Adding a Piper Model

The server features a highly flexible model discovery system. There are **3 ways** to specify the path to your `.onnx` and `.json` files:

### Option A — Out of the Box (Recommended)

Place your model files into the `Model/` folder exactly next to the compiled executable. The server will automatically find them on startup.

### Option B — Change Directory

If you store models on a different drive, open `appsettings.json` and change the `ModelDirectory`:

```json
"ModelSettings": {
  "ModelDirectory": "D:\\AI_Models\\Piper"
}
```

### Option C — Exact File Paths (Advanced)

If your files have custom names or are scattered across the system, you can specify exact paths:

```json
"ModelSettings": {
  "ExactModelFilePath": "C:\\Models\\voice.onnx",
  "ExactConfigFilePath": "D:\\Configs\\voice_config.json"
}
```

> **Windows users:** When writing absolute paths in JSON, you must use double backslashes (`\\`).

---

# Building From Source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Clone Only the ONNX_Runner Folder (Recommended)

```bash
git clone --filter=blob:none --sparse https://github.com/MrHryhorii/SmartStack.git
cd SmartStack
git sparse-checkout set ONNX_Runner
cd ONNX_Runner
```

Alternatively, clone the full SmartStack monorepo:

```bash
git clone https://github.com/MrHryhorii/SmartStack.git
cd SmartStack/ONNX_Runner
```

## Compiling the Server

Tsubaki uses a smart build system. You can build a **Full** version (includes GPU libraries, very large) or a **Lightweight CPU-only** version.

> For 90% of users and home servers, the **CPU-only version is highly recommended**. It is significantly smaller, completely hardware-agnostic, and the performance difference on modern CPUs is negligible.

### 1. Windows (Full: DirectML + CPU)

Automatically uses your GPU via DirectX 12 — works with NVIDIA, AMD, and Intel GPUs:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -o ./Publish/Tsubaki-Windows-Full
```

### 2. Windows (Lightweight: CPU Only)

```bash
dotnet publish -c Release -r win-x64 -p:CpuOnly=true --self-contained true -o ./Publish/Tsubaki-Windows-CPU
```

### 3. Linux (Lightweight: CPU Only) — Recommended

```bash
dotnet publish -c Release -r linux-x64 -p:CpuOnly=true --self-contained true -o ./Publish/Tsubaki-Linux-CPU
```

### 4. Linux (Full: CUDA + CPU) — Advanced Users Only

Builds the NVIDIA CUDA version. See the Linux Deployment section for strict hardware and software requirements:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -o ./Publish/Tsubaki-Linux-Full
```

### 5. Docker (Lightweight CPU)

The provided `Dockerfile` is pre-configured to build the lightweight CPU version to keep your container small and stable:

```bash
docker-compose up --build -d
```

> **Voice model required:** The compiled application does not include a Piper voice model to keep the binary size small. Before starting the server, download a voice model (`.onnx` + `.onnx.json`) and configure its path. See the Installation & Model Management section above.

---

# Docker & Linux Deployment

## Docker (Recommended for Servers)

The provided `docker-compose.yml` and `Dockerfile` are highly optimized and pre-configured to build the **Lightweight CPU** version. All native dependencies are handled automatically:

```bash
docker-compose up --build -d
```

## Bare-Metal Linux (CPU)

If you are running directly on a Linux host without Docker, you must install the native TTS engine and MP3 encoder libraries before running the server:

```bash
sudo apt-get update && sudo apt-get install -y espeak-ng libmp3lame0
```

The server also includes a **Linux auto-healing system**: on startup it automatically detects and fixes common native library symlink issues for `libespeak-ng.so` and `libmp3lame`, which are particularly common in containerized environments.

## Bare-Metal Linux (CUDA GPU) — Not Recommended

Tsubaki supports NVIDIA GPU acceleration on Linux, but we **strongly advise against using it** unless absolutely necessary.

For TTS tasks, the performance gain over a modern CPU is often negligible, while the downsides are significant:

- **Massive Build Size:** The CUDA build is over 1.5 GB larger.
- **High Power Consumption:** Keeps the GPU active and draws significantly more power.
- **Dependency Hell:** You must manually install and strictly match exact versions of proprietary NVIDIA libraries.

If you still want to proceed, your host system must have the following installed and correctly added to `$PATH`:

- Proprietary NVIDIA Linux Drivers
- NVIDIA CUDA Toolkit (v12.x compatible)
- NVIDIA cuDNN (v9.x)

> If any of these are missing or mismatched, the ONNX runtime will crash with `libcudnn.so.9: cannot open shared object file` and gracefully fall back to CPU execution anyway.

---

# Server Configuration (appsettings.json)

The `appsettings.json` file is completely pre-configured and ready to use out-of-the-box. Most users only ever need to set the model path and the languages list — everything else can safely be left at its defaults.

---

## Phonemizer & Language Settings

The `PhonemizerSettings` block controls how the server handles foreign words encountered in text. **In most cases, you should manually configure the `"SupportedLanguages"` list** — this is the single most impactful setting to configure after your model path.

```json
"PhonemizerSettings": {
  "SupportedLanguages": ["en", "uk", "fr"],
  "UseLanguageDetector": true,
  "MaxBonusMultiplier": 0.50,
  "BonusMinLetterCount": 8,
  "BonusMaxLetterCount": 32
}
```

- **How it works:** Add languages that your base Piper model doesn't speak natively, but might encounter in your texts — for example, an English model reading a French name or a Ukrainian phrase. The engine uses offline language detection via [Lingua](https://github.com/searchpioneer/lingua-dotnet) to identify the foreign words and approximate their pronunciation using the base model's available phonemes, producing a natural "accented" result rather than skipping or mangling the word.

- **Format:** Use short espeak language codes — `"en"`, `"uk"`, `"de"`, `"fr"`, `"zh"`, etc.

- **Performance Warning:** Every language added to this list increases memory consumption and slows down the overall voice synthesis. It is highly recommended to limit this list to **2–3 languages** that are most likely to appear in your texts.

- **Context & Punctuation:** If foreign words are phonetically similar to the model's native language and appear in a "wall of text" without proper punctuation, the detector might misidentify them and pronounce them incorrectly. Proper punctuation (commas, quotes, separate sentences) drastically improves accuracy. Languages with completely different alphabets (e.g., Cyrillic vs. Latin) are detected far more reliably than similar-looking Latin languages.

- **Priority Tweaks:** Other parameters in this block (like `"MaxBonusMultiplier"`) shift the detection priority back towards the model's native language for short, ambiguous, or borrowed words. The defaults are well-tuned and rarely need adjustment.

---

## Network & Access

- **`Kestrel > Endpoints > Http > Url`** — Defines the port the server listens on. Default is `http://+:5045`.

- **`CorsSettings`** — Controls Cross-Origin Resource Sharing. Setting `"AllowAnyOrigin": true` completely disables access limits and is perfectly fine for local or home use. If set to `false`, the server will only accept requests from the domains listed in `"AllowedOrigins"`, which you can freely edit to secure your endpoints.

- **`ApiSettings > MaxTextLength`** — Imposes a hard character limit on text-to-speech requests. Setting this to `0` removes the limit entirely, which is perfectly fine for personal or home use.

---

## Text Processing

- **`ChunkerSettings`** — Piper models notoriously struggle with massive, unbroken blocks of text. This setting automatically slices "walls of text" and run-on sentences into smaller, logical chunks for stable and high-quality generation. Keep this enabled.

```json
"ChunkerSettings": {
  "MaxChunkLength": 250,
  "SentencePauseSeconds": 0.3
}
```

---

## Resource Management

- **`HardwareSettings`** — Note that this is **not** a strict hardware cap. It simply tells the server's internal queueing system how much free resources you generally have versus how much a single request consumes. Actual memory usage depends entirely on your chosen Piper model. **For home use, you can completely ignore this section.**

- **`RateLimitSettings`** — Provides basic anti-spam and anti-DDoS protection by restricting the number of requests allowed from a single IP address within a specific time window. Useful for public-facing deployments.

```json
"RateLimitSettings": {
  "PermitLimit": 20,
  "WindowSeconds": 10,
  "QueueLimit": 5
}
```

---

## Audio & DSP

- **`EffectsSettings`** — Standard OpenAI API clients (like SillyTavern or AutoGen) do not support sending custom DSP parameters in their requests. This section allows you to define a `"DefaultEffect"` that the server will automatically apply to all incoming API requests unless explicitly overridden by a custom client (like the built-in web dashboard).

- **`DspSettings`** — Adds an audio cleanup pass (Low-Pass Filter) to remove high-frequency noise, and allows setting **server-wide default pitch and volume** applied to every request. This is especially useful when Tsubaki is used as a personal AI assistant backend — you can tune the voice character once in the config and every client, including standard OpenAI-compatible ones that cannot send custom parameters, will automatically receive the adjusted audio.

- **`ClonerSettings`** — Controls the OpenVoice cloning behavior. It is best not to touch these. Increasing the intensity often yields a caricature-like exaggeration of the voice characteristics, while decreasing it simply reverts the audio back to the default base model's voice.

---

# Fine-Tuning Voice Cloning

## Tone Temperature

```json
"ClonerSettings": {
  "ToneTemperature": 1.0
}
```

This parameter controls the variance in the latent space during voice color transfer.

- **High temperature (1.0 and above):** Makes the voice more emotional and lively, but significantly increases sensitivity to base model noise. On low-frequency models (16 kHz) or `medium` quality, this often causes micro-vibrations perceived as trembling or sobbing.

- **Low temperature (0.5 – 0.7):** Stabilizes the sound wave, making the voice feel "firmer" and more confident. This is the recommended setting for eliminating the trembling effect on models with a limited frequency range.

## Clone Intensity

```json
"ClonerSettings": {
  "CloneIntensity": 1.0
}
```

Defines the blending coefficient (Latent Space Blending) between the base Piper fingerprint and the target voice.

- **Value 1.0:** Full timbre transfer, which can amplify digital artifacts.

- **Value 0.8 – 0.9 (Recommended):** Preserves some of the original Piper model's articulatory stability while overlaying the character of the chosen voice. This provides the best balance between voice similarity and audio cleanliness.

## Cloned Voice Volume

The perceived loudness of a cloned voice is **not** controlled by `ClonerSettings` — it is shaped by two factors: the **recording level of the reference sample** and the **natural pitch of the cloned voice**.

- **Recording level:** OpenVoice extracts a voice fingerprint from the magnitude spectrogram of your `.wav` file and transfers its energy envelope onto the generated audio. A quietly recorded sample produces a quietly cloned voice, regardless of the base Piper model's output level. If the sample is too loud or peaks above 0 dBFS (clipping), the cloned audio will also clip and distort.

- **Voice pitch:** Lower-pitched voices — deep male voices, bass characters — naturally concentrate their spectral energy in the low-frequency range, which results in a lower overall magnitude in the spectrogram. Because OpenVoice transfers this energy profile directly, deep voices will consistently sound quieter than brighter, higher-pitched ones, even from identically recorded samples. This is an inherent property of the cloning architecture, not a bug.

**Recommended recording level:** aim for peaks around **−6 to −3 dBFS** — loud enough to fully capture the voice character, with just enough headroom to avoid distortion.

If the cloned voice is too quiet, compensate using `DefaultVolume` in `DspSettings`, or send `"volume"` per request:

```json
{
  "voice": "John",
  "volume": 2.0
}
```

This applies a clean gain stage with soft-knee limiting **before** the audio is encoded, so the result stays clean without harsh digital clipping.

> **Quality Tip:** To achieve crystal-clear cloning without temperature adjustments, use Piper models at the **High (22050 Hz)** quality tier. Their fuller frequency spectrum allows the OpenVoice neural network to operate without producing instability artifacts.

---

# ONNX Runtime Optimization

This section provides low-level control over the internal MLAS (Microsoft Linear Algebra Subprograms) math engine, heavily optimizing CPU execution. The defaults are already configured as the "golden standard" for cross-platform hardware acceleration. Change these only if you understand the consequences.

```json
"OnnxSettings": {
  "EnableGraphOptimization": true,
  "ExecutionMode": "Sequential",
  "IntraOpNumThreads": 0,
  "InterOpNumThreads": 1,
  "EnableMemoryPattern": true,
  "EnableCpuMemArena": true
}
```

| Parameter             | Description                                                                                                                                                                                                                           |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `IntraOpNumThreads`   | Threads used for matrix math within a single neural network node. `0` enables smart auto-detection of all available physical cores (Highly Recommended). To manually constrain CPU usage, set this to your exact physical core count. |
| `InterOpNumThreads`   | Parallelization across different graph nodes. For TTS (which is strictly sequential), this must **always be `1`**. Higher values cause thread thrashing and micro-stutters.                                                           |
| `EnableMemoryPattern` | Pre-allocates memory blocks during model load rather than dynamically during inference. Decreases latency per request by 5–10%.                                                                                                       |
| `EnableCpuMemArena`   | Utilizes an isolated memory arena for ONNX tensors. **Critical for .NET:** Bypasses the C# Garbage Collector entirely during audio generation, eliminating GC-induced freezes on long texts.                                          |
| `ExecutionMode`       | `"Sequential"` is the safest and fastest mode for Piper and OpenVoice architectures, as they do not benefit from parallel graph execution.                                                                                            |

## Concurrency & CPU Bottlenecks

The TTS engine and API are fully thread-safe and natively support concurrent HTTP requests. However, by default, the engine is optimized for maximum single-request speed (utilizing all cores via `IntraOpNumThreads: 0`). Hitting the CPU with multiple simultaneous requests in this mode will cause severe CPU context-switching, scaling down generation speed linearly.

**Handling High-Load Environments:**

- **Option A (Lowest Latency):** Keep defaults and process requests sequentially using the built-in `RateLimitSettings` or a message queue (e.g., RabbitMQ, Redis).
- **Option B (Maximum Concurrency):** To optimize for parallel processing, lower `IntraOpNumThreads` to `1` or `2`. This restricts each request to fewer cores, allowing the CPU to smoothly handle multiple simultaneous users without thread thrashing.

---

# API Endpoints

| Method | Endpoint                 | Description                          |
| ------ | ------------------------ | ------------------------------------ |
| `POST` | `/v1/audio/speech`       | Main TTS endpoint                    |
| `POST` | `/v1/audio/phonemize`    | Text phonemization (for diagnostics) |
| `GET`  | `/v1/audio/voices`       | List available voices                |
| `GET`  | `/v1/audio/effects`      | List available DSP effects           |
| `GET`  | `/v1/audio/environments` | List available acoustic environments |
| `GET`  | `/v1/models`             | OpenAI-compatible model listing      |
| `GET`  | `/v1/models/{id}`        | OpenAI-compatible model by ID        |
| `GET`  | `/health`                | Server health check                  |

## Swagger UI

A detailed Swagger UI with all extended parameters (Pitch, Volume, NoiseScale, CloneIntensity, etc.) is available at:

```
http://localhost:5045/swagger
```

---

# Open Source Credits & Acknowledgements

Tsubaki TTS Engine stands on the shoulders of giants. A massive thank you to the authors of the original models and open-source libraries that made this possible.

## AI Models & Datasets

- [**Piper TTS**](https://github.com/rhasspy/piper) — The core VITS neural network architecture by Rhasspy.
- [**OpenVoice V2**](https://github.com/myshell-ai/OpenVoice) — The innovative tone color cloning architecture by MyShell.
- [**PHOIBLE**](https://phoible.org/) — Cross-linguistic phonological data used for fallback phoneme matching.

## C# / .NET Libraries

- [**Microsoft.ML.OnnxRuntime**](https://github.com/microsoft/onnxruntime) — GPU-accelerated neural network inference.
- [**NAudio & NAudio.Lame**](https://github.com/naudio/NAudio) — Audio processing and MP3 encoding.
- [**SoundTouch.Net**](https://github.com/owoudenberg/soundtouch.net) — High-quality pitch and tempo shifting (WSOLA algorithm).
- [**SearchPioneer.Lingua**](https://github.com/searchpioneer/lingua-dotnet) — Fast, offline language detection for foreign word pronunciation.

---

# License & Usage

This project is open-source.

We strongly believe in the open-source community. If you use this engine (ONNX Runner / Tsubaki) in your products, create a fork, or integrate it into a commercial or open-source project, please **provide a link back to this original repository** in your documentation or credits section. Your attribution helps this project grow.
