# 🌸 Tsubaki TTS Engine (ONNX Runner)

Tsubaki TTS Engine is a blazingly fast, portable, and production-grade Text-to-Speech server written in **C# (.NET 8)**. It leverages the power of **Piper (VITS)** neural networks and **OpenVoice** to generate high-fidelity audio with support for instant voice cloning and real-time DSP effects.

---

## 🖥️ Web Dashboard

![Dashboard preview](tsubaki-tts.png)
_Built-in web interface for testing voices and DSP effects_

---

## 🚀 Why Tsubaki over Python alternatives?

Most modern open-source TTS engines are written in Python. This often leads to "dependency hell": CUDA version conflicts, gigabytes of PyTorch libraries, and virtual environment nightmares.

Tsubaki is built with an **engineering-first approach** to distribution:

- **No Python Required:** Runs purely on compiled C# and `Microsoft.ML.OnnxRuntime`.
- **Portable (Self-Contained):** Can be compiled into a single executable. Just download and run.
- **Dynamic Hardware Acceleration:** Automatically detects and utilizes your GPU (DirectML for Windows, CUDA for Linux) and gracefully falls back to CPU without crashing.
- **Memory Protection (OOM Guard):** Built-in queueing and semaphore system that calculates available VRAM/RAM to prevent server crashes under heavy load.

---

## ✨ Key Features

| Feature                           | Description                                                                                                                                                        |
| --------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 🔌 **OpenAI API Compatible**      | Exposes a `/v1/audio/speech` endpoint that perfectly mimics the official OpenAI API. Drop-in replacement for SillyTavern, LangChain, AutoGen, and other AI agents. |
| 🧬 **Zero-Shot Voice Cloning**    | Integrated with the OpenVoice V2 architecture. Clone any voice instantly by dropping a clean 10-second `.wav` file into the `Voices` folder.                       |
| 🌍 **Foreign Word Pronunciation** | Offline language detection via Lingua. Detects foreign words and applies phoneme approximation for natural accented pronunciation.                                 |
| 🎛️ **Studio-Grade DSP Effects**   | Real-time audio effects (Telephone, Overdrive, Reverb, etc.), pitch and volume shifting.                                                                           |
| 🌊 **Real-Time Streaming**        | Supports Chunked Transfer Encoding — listen to audio before generation is complete.                                                                                |
| 🖥️ **Built-in Web Dashboard**     | Sleek, user-friendly web interface available out-of-the-box for testing voices and effects.                                                                        |

---

## 🛠️ Building from Source

> **Note:** Pre-compiled binaries (ready-to-use `.exe` and Linux builds) will be available in the **Releases** tab soon.

Ensure you have the [.NET 8 SDK](https://dotnet.microsoft.com/download) installed.

```bash
# Clone the repository
git clone https://github.com/YourUsername/Tsubaki-TTS.git
cd Tsubaki-TTS
```

**Build for Windows (Self-Contained `.exe`):**

```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

**Build for Linux:**

```bash
dotnet publish -c Release -r linux-x64 --self-contained true
```

The compiled server will be located in `bin/Release/net8.0/[OS]/publish/`.

---

## 📂 Installation & Model Management

### 1. Adding a Piper Model (ONNX)

The server features a highly flexible model discovery system. You have **3 ways** to specify the path to your `.onnx` and `.json` model files:

#### Option A: "Out of the Box" (Relative Path)

Place your model files into the `Model` folder next to the executable. The server will automatically find them on startup.

#### Option B: Change Directory (via `appsettings.json`)

If you store models on a different drive, open `appsettings.json` and change the `ModelDirectory`:

```json
"ModelSettings": {
  "ModelDirectory": "D:\\AI_Models\\Piper"
}
```

#### Option C: Exact File Paths (Advanced)

If your files have custom names or are scattered across the system, you can specify exact paths.

> ⚠️ **WINDOWS USERS:** When writing absolute paths in JSON, you must use double backslashes (`\\`)!

```json
"ModelSettings": {
  "ExactModelFilePath": "C:\\Models\\voice.onnx",
  "ExactConfigFilePath": "D:\\Configs\\voice_config.json"
}
```

---

### 2. Voice Cloning (OpenVoice)

The server will automatically download the necessary base cloner models from HuggingFace on the first run.

**To add a new voice:**

1. Place a clean voice sample (`.wav`, 5–15 seconds) into the `Voices` folder.
2. The filename (e.g., `John.wav`) becomes the voice ID.
3. Use `"voice": "John"` in your API requests.

---

## 🎛️ Default Effects & Environments

Since standard OpenAI clients (like SillyTavern) cannot send custom DSP effect parameters, Tsubaki allows you to set a **Default Effect** in `appsettings.json`. This effect will be automatically applied to all incoming API requests unless overridden.

```json
"EffectsSettings": {
  "EnableGlobalEffects": true,
  "DefaultEffect": "LoFiTape",
  "DefaultIntensity": 1.0,
  "DefaultEnvironment": "LivingRoom",
  "DefaultEnvironmentIntensity": 0.8
}
```

### Available Voice Effects (`DefaultEffect`)

| Value           | Description                                                    |
| --------------- | -------------------------------------------------------------- |
| `None`          | Bypass — clean audio                                           |
| `Telephone`     | Lo-Fi equalization with hard transistor clipping               |
| `Overdrive`     | Warm tube saturation and cubic waveshaping distortion          |
| `Bitcrusher`    | Retro 8-bit / Arcade style sample rate decimation              |
| `RingModulator` | Classic Robot / Dalek metallic effect                          |
| `Flanger`       | Modulated short delay with heavy feedback                      |
| `Chorus`        | Thick, multi-voice ensemble effect                             |
| `LoFiTape`      | Simulates the warmth and coloration of an analog cassette tape |

### Available Spatial Environments (`DefaultEnvironment`)

| Value          | Description                                                     |
| -------------- | --------------------------------------------------------------- |
| `None`         | Dry signal only                                                 |
| `LivingRoom`   | Small room with short, bright reverb                            |
| `ConcreteHall` | Large hall with long, dense reverb and strong early reflections |
| `Forest`       | Open outdoor space with long, diffuse reverb                    |
| `Underwater`   | Muffled underwater acoustic properties                          |

---

## 💻 Docker & Linux Deployment

A `Dockerfile` is provided for containerized deployments.

> **Important for Bare-Metal Linux:** The TTS engine and MP3 encoder rely on native system packages. If running directly on a Linux host (without Docker), install them first:

```bash
sudo apt-get update && sudo apt-get install espeak-ng libmp3lame0
```

---

## 📡 API Documentation

Tsubaki mimics the standard OpenAI `/v1/audio/speech` endpoint.

**Example POST Request:**

```bash
curl http://localhost:5045/v1/audio/speech \
  -H "Content-Type: application/json" \
  -d '{
    "model": "tts-1",
    "input": "Hello world! This is a test of the speech synthesis engine.",
    "voice": "piper_base",
    "response_format": "mp3",
    "speed": 1.0,
    "stream": true,
    "effect": "Telephone",
    "environment": "ConcreteHall"
  }'
```

A detailed **Swagger UI** with all extended parameters (Pitch, NoiseScale, etc.) is available at `http://localhost:5045/swagger` when the server is running.

---

## 📚 Open Source Credits & Acknowledgements

Tsubaki TTS Engine stands on the shoulders of giants. A massive thank you to the authors of the original models and open-source libraries that made this possible:

**AI Models & Datasets:**

- [**Piper TTS**](https://github.com/rhasspy/piper) — The core VITS neural network architecture by Rhasspy.
- [**OpenVoice V2**](https://github.com/myshell-ai/OpenVoice) — The innovative tone color cloning architecture by MyShell.
- [**PHOIBLE**](https://phoible.org/) — Cross-linguistic phonological data used for fallback phoneme matching.

**C# / .NET Libraries:**

- [**Microsoft.ML.OnnxRuntime**](https://github.com/microsoft/onnxruntime) — GPU-accelerated neural network inference.
- [**NAudio & NAudio.Lame**](https://github.com/naudio/NAudio) — Audio processing and MP3 encoding.
- [**SoundTouch.Net**](https://github.com/owoudenberg/soundtouch.net) — High-quality pitch and tempo shifting (WSOLA algorithm).
- [**SearchPioneer.Lingua**](https://github.com/searchpioneer/lingua-dotnet) — Fast, offline language detection for foreign word pronunciation.

---

## 📝 License & Usage

This project is open-source.

We strongly believe in the open-source community. If you use this engine (ONNX Runner / Tsubaki) in your products, create a fork, or integrate it into a commercial or open-source project, please **provide a link back to this original repository** in your documentation or credits section. Your attribution helps this project grow!
