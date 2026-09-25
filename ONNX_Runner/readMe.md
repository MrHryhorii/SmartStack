# Tsubaki TTS Engine v1.0.8

Production-grade local Text-to-Speech engine for AI agents, companions, VTubers, and OpenAI-compatible applications.

Piper is fast, lightweight, and easy to run, but its voices are tied to the model you choose. Tsubaki is a standalone engine built around **Piper (VITS) voice models** that keeps Piper's efficiency while adding the missing layer: **voice freedom, cross-language pronunciation, and audio control**.

Use standard Piper models locally, then add zero-shot voice cloning, interchangeable voices, pitch and volume control, DSP effects, spatial environments, streaming, and per-request audio control. Even a single-language Piper voice can pronounce text in other configured languages: Tsubaki detects or follows the requested language, generates the appropriate phonemes, and adapts sounds the model does not support to the closest ones it can produce.

Built with **C# (.NET 10)** and **ONNX Runtime**, Tsubaki runs locally on Windows and Linux with CPU or GPU acceleration.

- Piper model engine — run standard Piper models locally
- Voice Freedom — use different voices without replacing the underlying Piper model
- Cross-Language Pronunciation — use one Piper voice to approximate speech in other configured languages without replacing the base model
- Zero-shot voice cloning via OpenVoice V2
- OpenAI-compatible API — works with existing OpenAI TTS clients and tools
- Dedicated Tsubaki API for detailed audio control
- Real-time streaming (Chunked Transfer Encoding and SSE)
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

**[Tsubaki TTS Engine v1.0.7 (GitHub Releases)](https://github.com/MrHryhorii/SmartStack/releases/tag/tsubakitts-v1.0.7)**

> This documentation describes the upcoming **v1.0.8**. The latest published binary release is currently **v1.0.7**.

Direct Plug-and-Play binary downloads for Windows and Linux. Includes pre-configured base models and cloneable voices.

---

# Why Tsubaki Instead of Python TTS Stacks?

Most modern open-source TTS engines are written in Python. This often leads to "dependency hell": CUDA version conflicts, gigabytes of PyTorch libraries, and virtual environment nightmares.

Tsubaki is built with an engineering-first approach to distribution:

- **No Python Required:** Runs purely on compiled C# and `Microsoft.ML.OnnxRuntime`.
- **Portable (Self-Contained):** Download the release ZIP, extract it, and run the included executable. No .NET SDK is needed.
- **Hardware Acceleration:** Runs on CPU by default, with optional WebGPU, DirectML, or CUDA acceleration depending on the selected build and hardware configuration.
- **Memory Protection (OOM Guard):** Built-in queueing and semaphore system that calculates available VRAM/RAM to prevent server crashes under heavy load.
- **True Concurrency:** On CPU and CUDA, concurrent requests share the same loaded model instead of requiring a full model/runtime copy per request. DirectML uses a fixed-size session pool because a single DirectML session cannot execute concurrently; see `HardwareSettings` for details.

| Tsubaki                               | Typical Python TTS                   |
| ------------------------------------- | ------------------------------------ |
| Self-contained release                | Python virtual environments          |
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
| OpenAI API Compatible      | Exposes a `/v1/audio/speech` endpoint compatible with the official OpenAI API. Compatible with SillyTavern, LangChain, AutoGen, and other clients that use the standard OpenAI TTS request fields. |
| Zero-Shot Voice Cloning    | Integrated with the OpenVoice V2 architecture. Clone any voice instantly by dropping a clean 10-second `.wav` file into the `Voices` folder.                       |
| Cross-Language Pronunciation | Detects configured languages automatically or follows an explicit language override, then adapts their pronunciation to the phoneme inventory of the active Piper model. This enables accented cross-language speech without requiring a multilingual base model. |
| Studio-Grade DSP Effects   | Real-time audio effects (Telephone, Overdrive, Reverb, etc.), pitch and volume shifting.                                                                           |
| Real-Time Streaming        | Supports Chunked Transfer Encoding and SSE — listen to audio before generation is complete.                                                                        |
| Built-in Web Dashboard     | Sleek, user-friendly web interface available out-of-the-box for testing voices and effects.                                                                        |
| OOM Guard                  | Built-in queueing and semaphore system that prevents VRAM/RAM crashes under heavy load.                                                                            |
| No Python Required         | Pure C# and ONNX Runtime. Choose a CPU, WebGPU, DirectML, or CUDA build.                                                                                           |

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

Download the build for your OS from:

https://github.com/MrHryhorii/SmartStack/releases

Extract the complete ZIP. For a first run, the CPU build is the simplest choice.

---

## 2. Add a Piper Voice Model (Optional)

The release includes a default Piper voice model. If you want to use a different voice, download its `.onnx` and `.onnx.json` files from [Piper voices](https://huggingface.co/rhasspy/piper-voices/tree/main) and put both in the `Model/` folder next to the executable. See *Piper Voice Models* below for details.

---

## 3. Run the Server

On Windows, open the extracted folder and double-click `TsubakiTTS.exe`.

On Linux, install eSpeak NG and the MP3 library. On Debian or Ubuntu:

```bash
sudo apt-get update
sudo apt-get install -y espeak-ng libmp3lame0
```

On Arch Linux:

```bash
sudo pacman -Syu --needed espeak-ng lame
```

Then, from the extracted folder, run:

```bash
chmod +x ./TsubakiTTS
./TsubakiTTS
```

Tsubaki opens the browser dashboard automatically when possible. You can also open it at `http://localhost:5045`.

---

## 4. Test Speech Synthesis

Type some text in the dashboard and click **Generate**. You can play the result there or click **Download** to save it. No terminal command is needed for this first test.

For a quick mixed-language test, paste this into the text box:

```text
Maya checked config.prod.json—twice—and whispered, "No... esto no funciona; but maybe, demain, ça ira?", then added (almost laughing): Я перевірю ще раз — pero, please, don't restart https://example.com/api?v=2.3.1&mode=fast; if Dr. Smith replies, say "sí, d'accord", otherwise... wait.
```

It mixes English, Spanish, French, and Ukrainian. In the dashboard's
**Pronunciation** selector, choose **Auto** to detect the language or select
a language yourself. Try both to hear the difference.

---

# OpenAI API Compatibility

Tsubaki mimics the standard OpenAI `/v1/audio/speech` endpoint. Clients that use the standard OpenAI TTS request fields can usually connect to Tsubaki by changing only the base URL.

Both API surfaces use the same synthesis engine. Server-side cloning, DSP, pitch, volume, and reverb defaults still apply to `/v1/audio/speech`; the dedicated `/tsbk/...` endpoint simply adds per-request control over Tsubaki-specific settings.

Compatible with:

- SillyTavern
- Open WebUI
- LangChain
- AutoGen
- any custom OpenAI client

## Standard Request

```bash
curl -o speech.mp3 http://localhost:5045/v1/audio/speech \
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

| Field             | Type          | Description |
| ----------------- | ------------- | ----------- |
| `model`           | string        | Any value (e.g. `"tts-1"`) — accepted for compatibility; the locally loaded Piper model is used. |
| `input`           | string        | Text to synthesize. |
| `voice`           | string/object | Local voice ID such as `"piper_base"` or `"John"`. `/v1` also accepts the OpenAI-style `{ "id": "John" }` object. |
| `response_format` | string        | `mp3`, `wav`, `opus`, `flac`, `aac`, `pcm`, or Tsubaki's `b64_json` representation. |
| `instructions`    | string        | Accepted for OpenAI compatibility but intentionally ignored; Piper/OpenVoice has no equivalent natural-language style-control input. |
| `speed`           | float         | Speech speed multiplier. `1.0` is default. |
| `stream_format`   | string        | `audio` (default) returns the normal audio/JSON response body; `sse` frames the response as Server-Sent Events. |
| `stream`          | bool          | Tsubaki extension that overrides the server's chunked-streaming default for this request. |

The Tsubaki endpoint reuses the same core fields and output formats where applicable, so they are not repeated below. Use `/tsbk/audio/speech` when you need Tsubaki-specific per-request controls.


### Base64 JSON Response

Set `"response_format": "b64_json"` when a client needs audio embedded in JSON instead of a binary audio response.

```json
{
  "audioContent": "<base64-encoded MP3>"
}
```

`b64_json` is a response representation, not a separate audio codec: Tsubaki generates MP3 audio and Base64-encodes those bytes into the `audioContent` field. It works with both `"stream": false` and `"stream": true`. With the default `"stream_format": "audio"`, streaming emits one continuous JSON/Base64 response progressively as audio becomes available, although clients that use a normal whole-document JSON parser still need the closing JSON suffix before parsing the complete object.

With `"stream_format": "sse"`, `b64_json` uses SSE events instead of one continuous JSON document. Each `speech.audio.delta` event contains an independently Base64-encoded MP3 chunk in `audioContent`:

```text
data: {"type":"speech.audio.delta","audioContent":"<base64-encoded MP3 chunk>"}
```

Clients should Base64-decode each `audioContent` value separately and concatenate the decoded MP3 bytes; the Base64 strings themselves should not be concatenated. `"stream": true` sends these delta events progressively during synthesis, while `"stream": false` generates the complete audio first and then returns it using the same SSE framing.

---

# Real-Time Streaming

Tsubaki supports HTTP chunked streaming and Server-Sent Events (SSE). Audio playback can begin before the full synthesis finishes — useful for AI companions, streaming agent pipelines, and real-time conversations.

Enable streaming per request:

```json
{
  "stream": true
}
```

To use SSE framing:

```json
{
  "stream": true,
  "stream_format": "sse"
}
```

`stream_format` controls how the HTTP response is framed: `"audio"` keeps the normal response body, while `"sse"` emits `speech.audio.delta` events followed by `speech.audio.done`. The `stream` flag controls delivery timing independently: `true` sends available deltas progressively during synthesis, while `false` generates the complete audio first and then returns it using the selected framing.

Recommended server-side streaming configuration in `appsettings.json`:

```json
"StreamSettings": {
  "EnableStreaming": true,
  "FlushAfterEachSentence": true,
  "MinChunkSizeKb": 8
}
```

`FlushAfterEachSentence: true` flushes each completed internal audio chunk immediately, including an `EarlySplit` first chunk. The setting name is retained for compatibility. This is recommended for AI companions and other latency-sensitive clients.

> WAV does not support true chunked streaming because its header requires the final file size upfront. `mp3`, `opus`, `flac`, `aac`, `pcm`, and `b64_json` can stream progressively.

---

# Voice Cloning (OpenVoice V2)

## Adding a Voice

1. Place a clean voice sample (`.wav`, 5–15 seconds) into the `Voices/` folder.
2. The filename becomes the voice ID: `John.wav` → `"voice": "John"`.
3. Start or restart the server. On startup, Tsubaki extracts the voice fingerprint and creates a `.voice` file next to the sample.
4. The new voice is then automatically available by that name in API requests and appears in the Web Dashboard voice list.

The `.voice` fingerprint is created only when a source `.wav` is present. Once the fingerprint has been created, the source `.wav` can be removed; the `.voice` file is sufficient for using the cloned voice. If a `.wav` is present without a corresponding `.voice` file, Tsubaki creates the fingerprint automatically at startup.

Use it in any API request:

```json
{
  "voice": "John"
}
```

For the base Piper voice without cloning: `"voice": "piper_base"`.

If `voice` is omitted or the requested cloned voice is unavailable, Tsubaki uses the base voice of the active Piper model. When a cloned voice is selected, Tsubaki first synthesizes speech with the active Piper model and then applies the cloned voice characteristics through OpenVoice V2.

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
| ------------------- | ---------------------------------------------------------------------------- |
| `tone_extract.onnx` | Extracts a 256-dimensional voice fingerprint from a reference audio sample  |
| `tone_color.onnx`   | Transfers the extracted voice characteristics onto the generated base audio |
| `tone_config.json`  | Hyperparameters and structural configuration for both models                |

> These ONNX files are conversions of the **OpenVoice V2** models by MyShell; Tsubaki does not claim authorship of the original models. They are distributed under the **MIT License** and are free for commercial use.

> **Performance Note:** Voice cloning is substantially heavier than base Piper synthesis. On CPU, increasing `IntraOpNumThreads` can reduce cloning latency; the shipped value is intentionally moderate so Tsubaki can share CPU time with other applications.

## Fine-Tuning Cloning Behavior

Both settings can be configured server-wide in `ClonerSettings` or overridden per request on the Tsubaki endpoint.

```json
"ClonerSettings": {
  "CloneIntensity": 1.0,
  "ToneTemperature": 0.7
}
```

| Setting | Practical meaning |
| ------- | ----------------- |
| `CloneIntensity` | `0.0` = base Piper voice, `0.5` = equal base/target blend, `1.0` = standard target voice, values above `1.0` exaggerate target-vs-base differences. |
| `ToneTemperature` | OpenVoice `tau`. `0.7` is Tsubaki's stable default, `1.0` is standard OpenVoice behavior; lower values are more conservative, higher values add variation and can introduce artifacts. It is not an emotion control. |

If a clone develops trembling or warbling, lower `tone_temperature` first.

## Cloned Voice Volume

The perceived loudness of a cloned voice is shaped by **reference loudness normalization**, the **remaining characteristics of the recording**, and the **natural pitch of the cloned voice**.

- **Reference loudness:** Tsubaki measures each reference `.wav`'s integrated loudness in LUFS and normalizes it to `ClonerSettings.ReferenceAudioTargetLufs` before extracting the voice embedding. This reduces the effect of recording level and ignores leading/trailing silence. It cannot recover detail already lost to clipping, so clean recordings are still important.

```json
"ClonerSettings": {
  "ReferenceAudioTargetLufs": -23.0
}
```

`-23 LUFS` is the EBU R128 broadcast reference. The important part is consistency: every reference is normalized against the same target, providing a common loudness baseline.

- **Residual recording characteristics:** Loudness normalization matches overall perceived loudness, not every detail of the source recording. Microphone response, room coloration, dynamics, and other characteristics can still influence the resulting clone.
- **Voice pitch:** Lower-pitched voices — deep male voices, bass characters — naturally concentrate more spectral energy in the low-frequency range. Because OpenVoice transfers this energy profile, deep voices can still sound quieter than brighter, higher-pitched voices even when the reference samples have been loudness-normalized. This is a property of the cloning process, not a bug.

**Recommended recording level:** aim for peaks around **−6 to −3 dBFS** — loud enough to capture the voice clearly, with enough headroom to avoid distortion. Loudness normalization handles the overall level; the peak recommendation is mainly about avoiding clipping and preserving a clean source signal. See *Recommended Sample Quality* above.

If the cloned voice is still too quiet, compensate using `DefaultVolume` or `VolumeBoosterDb` in `DspSettings` (see *Server-Side DSP Defaults* below), or send `"volume"` per request:

```json
{
  "voice": "John",
  "volume": 2.0
}
```

The volume adjustment is applied as a final gain stage with soft-knee limiting **after character and spatial effects, before encoding**. The cloning model itself always works from the natural, un-boosted waveform.

---

# Tsubaki Endpoint

Tsubaki also exposes `/tsbk/audio/speech` for full per-request control. It uses the same synthesis engine and core request fields described above, while adding DSP, spatial environments, pitch/volume, cloning controls, language routing, pronunciation variance, and synthesis chunk control.

Only `input` is required; omitted optional fields fall back to their engine or server defaults. Use `/v1/...` for OpenAI-shaped clients and `/tsbk/...` when you want Tsubaki-specific controls.

A detailed **Swagger UI** with every parameter is available at `http://localhost:5045/swagger` when running in Development mode.

## Full Request Example

```bash
curl -o speech.mp3 http://localhost:5045/tsbk/audio/speech \
  -H "Content-Type: application/json" \
  -d '{
    "model": "tts-1",
    "input": "This message is coming from an old intercom.",
    "voice": "John",
    "response_format": "mp3",
    "speed": 1.0,
    "stream": true,
    "stream_format": "audio",
    "early_split": true,
    "language": "auto",

    "effect": "Telephone",
    "effect_intensity": 0.8,

    "environment": "ConcreteHall",
    "environment_intensity": 0.3,
    "extend_reverb_tail": true,

    "pitch": 0.9,
    "volume": 1.5,

    "noise_scale": 0.667,
    "noise_w": 0.8,

    "clone_intensity": 0.85,
    "tone_temperature": 0.7,
    "low_pass_q_factor": 0.707
  }'
```

## DSP Effect Parameters

| Parameter               | Type   | Description                                                |
| ----------------------- | ------ | ---------------------------------------------------------- |
| `effect`                | string | DSP character effect to apply. See available values below. |
| `effect_intensity`      | float  | Effect intensity. `1.0` is full strength.                  |
| `environment`           | string | Acoustic spatial environment. See available values below.  |
| `environment_intensity` | float  | Reverb intensity. `0.25` is recommended.                   |
| `extend_reverb_tail`    | bool   | Lets the active `environment` reverb decay fully at the end of the request instead of being cut off. Overrides the server's `ExtendReverbTailOnFinish` default for this request only. Has no effect on character `effect`s. See *Reverb Tail Extension* in Server-Side DSP Defaults below. |

### Available Effects

| Value           | Description                                                                                  |
| --------------- | -------------------------------------------------------------------------------------------- |
| `None`          | Bypass — clean audio                                                                         |
| `Telephone`     | Vintage telephone filtering, line noise, and asymmetric saturation                           |
| `Overdrive`     | Warm tube saturation and cubic waveshaping distortion                                        |
| `Bitcrusher`    | Bit-depth and sample-rate reduction for retro digital distortion                             |
| `RingModulator` | Classic Robot / Dalek metallic effect                                                        |
| `Flanger`       | Modulated short delay with heavy feedback                                                    |
| `Chorus`        | Thick, multi-voice ensemble effect                                                           |
| `LoFiTape`      | Analog cassette-style saturation, wow/flutter, and tape hiss                                 |
| `DecoderGlitch` | Repeats short audio fragments to simulate a digital decoder glitch                           |
| `TacticalRadio` | CVSD codec simulation with heavy compression and slope-overload distortion                   |
| `FmRadio`       | Handheld FM radio effect with an amplitude limiter and signal-dependent noise floor          |
| `G711MuLaw`     | G.711 μ-law digital telephony codec simulation                                               |
| `G711ALaw`      | G.711 A-law digital telephony codec simulation                                               |

### Available Environments

| Value          | Description                                                                   |
| -------------- | ------------------------------------------------------------------------------ |
| `None`         | Dry signal only                                                               |
| `LivingRoom`   | Small room with short decay and balanced frequency response                   |
| `Stage`        | Performance stage with distinct pre-delay and slow shimmer                    |
| `ConcreteHall` | Large hall with long, dense reverb and strong early reflections               |
| `Dungeon`      | Tight stone space with short, dark, resonant flutter echoes                   |
| `Cave`         | Large enclosed space with deep resonance and very long, dark decay            |
| `Forest`       | Open outdoor space with discrete echoes rather than dense reverb tails        |
| `Muffled`      | Pure low-pass filter occlusion (simulates hearing through walls/earplugs)     |
| `Underwater`   | Muffled acoustics with high-frequency roll-off and slapback echo              |
| `InnerVoice`   | Micro-delay and dynamic low-pass to pull the voice inside the listener's head |

## Synthesis Parameters

| Parameter     | Type   | Description |
| ------------- | ------ | ----------- |
| `language`    | string | Forces an eSpeak language/dialect code (e.g. `"uk"`, `"fr-ca"`). Use `"auto"` or omit it for automatic detection and fallback routing. |
| `early_split` | bool   | Lets only the first synthesis chunk end at conservative clause punctuation to reduce time to first audio; normal sentence chunking resumes afterward. This can slightly change the rhythm or intonation of the first sentence and is mainly useful for latency-sensitive or streaming output. Overrides `ChunkerSettings.EarlySplit`. |
| `pitch`       | float  | Pitch multiplier. `1.0` is unchanged. |
| `volume`      | float  | Output volume multiplier with soft-knee limiting. `1.0` is unchanged. |
| `noise_scale` | float  | Pronunciation/intonation variance. Default: `0.667`. |
| `noise_w`     | float  | Phoneme-duration/rhythm variance. Default: `0.8`. |

## Cloning Parameters

| Parameter             | Type  | Description |
| --------------------- | ----- | ----------- |
| `clone_intensity`     | float | Per-request override of `ClonerSettings.CloneIntensity`; see *Fine-Tuning Cloning Behavior*. |
| `tone_temperature`    | float | Per-request OpenVoice `tau` override; see *Fine-Tuning Cloning Behavior*. |
| `low_pass_q_factor`   | float | Per-request low-pass resonance/roll-off override (`0.1`–`1.0`) for cloned voices; see *LowPassQFactor* below. |

> Tsubaki-specific controls can be changed per utterance, so agents can vary synthesis and acoustic context without changing server configuration.

---

# Server-Side DSP Defaults

Since standard OpenAI clients (like SillyTavern) cannot send custom DSP effect parameters, Tsubaki allows you to set a **Default Effect** in `appsettings.json`. This effect will be automatically applied to all incoming API requests unless explicitly overridden by a custom client (like the built-in web dashboard).

## Default Effects & Environments

The following is an example configuration that enables LoFiTape and LivingRoom as server-wide defaults. The shipped defaults keep both the effect and environment disabled (`None`).

```json
"EffectsSettings": {
  "EnableGlobalEffects": true,
  "DefaultEffect": "LoFiTape",
  "DefaultIntensity": 1.0,
  "DefaultEnvironment": "LivingRoom",
  "DefaultEnvironmentIntensity": 0.25,
  "ExtendReverbTailOnFinish": true,
  "ReverbTailSilenceFloor": 0.005
}
```

Set `"DefaultEffect": "None"` to bypass effects entirely.

---

### Reverb Tail Extension

The pause between sentences is designed for natural speech pacing, not for reverb decay. With long environments at high intensity, the reverb may still be audible when the generated speech reaches the end of the request. `ExtendReverbTailOnFinish` allows the active `environment` reverb to decay naturally after the generated voice ends, until the measured output level falls below the configured threshold.

`ReverbTailSilenceFloor` controls that threshold using linear amplitude. The default `0.005` is roughly −46 dBFS. Lower values allow the reverb to decay further; higher values end playback sooner.

The extension applies only to spatial `environment` reverb. Character `effect`s such as `LoFiTape` or `Telephone` are not extended.

**Effect on audio length:** Long-tail environments such as `Cave` and `ConcreteHall` can noticeably increase the final duration at high `environment_intensity`. At lower intensity (`0.25`, the shipped default), the additional time is usually small or zero.

> **Chunked requests:** Tsubaki can split a single large request into internal chunks for more predictable model load while carrying the reverb state between them. However, if an agent splits one logical turn into multiple separate `/tsbk/audio/speech` requests, each request has its own completion point and will wait for its own reverb tail to decay, creating unnatural gaps (dead air) between fragments. **To avoid this:**
>
> - **Recommended:** Send the complete turn as a single request and let Tsubaki handle the chunking internally.
> - **Request-by-request streaming:** Send `"extend_reverb_tail": false` on intermediate requests and enable it only on the final request. Alternatively, avoid strong reverb environments for this type of streaming.

**Per-request override:** `"extend_reverb_tail": true` or `false` overrides `ExtendReverbTailOnFinish` for that request only. `ReverbTailSilenceFloor` is server-only.


---

## Default Pitch & Volume

```json
"DspSettings": {
  "EnableLowPassFilter": true,
  "LowPassCutoffFrequency": 11000.0,
  "LowPassQFactor": 0.577,
  "DefaultPitch": 1.0,
  "DefaultVolume": 1.0,
  "VolumeBoosterDb": 8.0
}
```

### LowPassQFactor

Controls the resonance and roll-off curve of the low-pass filter used for cloned voices. It is primarily used to clean up high-frequency artifacts (metallic "sand") generated during OpenVoice cloning.

- **`0.577` (Bessel curve, server default):** Uses a smoother, more gradual roll-off around the cutoff, with no resonant peak. This produces gentler attenuation of high-frequency artifacts and is the default choice for cloned voices.
- **`0.707` (Butterworth curve):** Keeps the response flatter below the cutoff and transitions more sharply into attenuation at the cutoff. This preserves slightly more energy close to the cutoff, but may also leave more of the high-frequency character of neural artifacts exposed.

> In practice, both curves target the same few dB of artifact energy near the cutoff — the difference is measurable, not something most listeners will notice by ear. Treat this as a fine-tuning knob for controlled A/B comparisons, not a dramatic quality switch.

### DefaultPitch

Server-wide pitch multiplier: `1.0` leaves pitch unchanged, while `0.5`/`2.0` shift it one octave down/up. Tsubaki requests can override it per request with `pitch`; OpenAI-only clients use the server default.

### DefaultVolume

Server-wide volume multiplier with soft-knee limiting: `1.0` is unchanged, `0.5` is about −6 dB, `2.0` about +6 dB, and `4.0` is the maximum +12 dB setting. Tsubaki requests can override it with `volume`; `VolumeBoosterDb` is applied underneath this multiplier.

### VolumeBoosterDb

Applies a fixed gain correction in decibels underneath `DefaultVolume`/`volume`. It is intended as a baseline calibration for the engine's output rather than a per-request volume preference. A value of `0` disables the correction.

The booster and resolved `volume` are combined into a single final gain stage after character and spatial effects, before encoding. It is not an additional audio-processing pass. `VolumeBoosterDb` is server-only; clients that need a different playback level should use `volume`. A client that needs an *exact* absolute level can discover the current combined value via `GET /tsbk/server/status` → `dsp.volumeBoosterDb` / `dsp.defaultTotalGainDb`.

---

# Piper Voice Models

## Finding Voice Models

All official Piper voices are hosted on HuggingFace:

**[rhasspy/piper-voices on HuggingFace](https://huggingface.co/rhasspy/piper-voices/tree/main)**

The repository contains **35 languages**, each in its own folder (`en`, `de`, `fr`, `uk`, `cmn`, etc.).

## What to Download

For each voice you need to download exactly **2 files**:

| File          | Extension    | Description                                  |
| ------------- | ------------ | ---------------------------------------------- |
| Model weights | `.onnx`      | The neural network — this is the large file    |
| Model config  | `.onnx.json` | Metadata: sample rate, phonemes, speaker IDs  |

### How to Download a Voice

1. Browse to your language folder, e.g. [`/en`](https://huggingface.co/rhasspy/piper-voices/tree/main/en)
2. Navigate into a voice subfolder (e.g. `en_US/lessac/medium/`)
3. Download both files: `en_US-lessac-medium.onnx` and `en_US-lessac-medium.onnx.json`
4. Place both files into the `Model/` folder next to the executable

> Both files **must be present** — the engine will fail to load without the accompanying `.json` config.

## Available Quality Tiers

Most voices come in multiple quality levels. Higher quality = larger model and higher memory usage:

| Quality  | Approx. Size | Notes                             |
| -------- | ------------ | ----------------------------------- |
| `x_low`  | ~5 MB        | Fast, lower fidelity               |
| `low`    | ~15 MB       | Good for low-end hardware          |
| `medium` | ~60 MB       | Recommended for most use cases     |
| `high`   | ~130 MB      | Best quality, requires more memory |

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
  "ModelDirectory": "D:\\AI_Models\\Piper",
  "ExactModelFilePath": "",
  "ExactConfigFilePath": "",
  "Speaker": ""
}
```

`Speaker` selects a speaker only for multi-speaker Piper models. Set it to a key from the model's `speaker_id_map` (for example, `"3922"`). Leave it empty to use the model's first available speaker. If the configured key is not found, Tsubaki also falls back to the first available speaker. The setting is ignored for single-speaker models.

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

Tsubaki provides several build variants for different hardware configurations. You can build a **Lightweight CPU-only** version, or enable **WebGPU, DirectML, or CUDA** for hardware-accelerated configurations, especially voice cloning.

> **Which version should I choose?**
> For standard TTS generation, the **CPU-only version** is highly recommended. Piper models are designed to be fast and efficient on modern CPUs, making this the simplest option for most users.
>
> If you plan to use **Voice Cloning (OpenVoice V2)**, GPU acceleration is strongly recommended. WebGPU is the preferred choice for personal and local use because it provides broad GPU compatibility without requiring a vendor-specific runtime. DirectML is available as an alternative on Windows, while CUDA is available for NVIDIA GPUs on Linux.
>
> **WebGPU and concurrency:** The current WebGPU implementation is optimized for local and personal use. Piper base synthesis remains on the CPU, while OpenVoice voice conversion can use the GPU. GPU cloning requests are currently processed one at a time for stability. This is usually not a limitation for a personal TTS setup, where requests are generated sequentially. If WebGPU is unavailable, the cloning stage automatically falls back to the CPU. Parallel GPU execution is planned for a future release.

### 1. Windows (WebGPU + CPU) — Recommended for Voice Cloning

Uses WebGPU for hardware-accelerated OpenVoice voice cloning.

```bash
dotnet publish -c Release -r win-x64 -p:UseWebGpu=true --self-contained true -o ./bin/publish/tsubaki-tts-engine-windows-x64-webgpu
```

### 2. Windows (DirectML + CPU)

Alternative Windows GPU acceleration with support for NVIDIA, AMD, and Intel GPUs.

```bash
dotnet publish -c Release -r win-x64 --self-contained true -o ./bin/publish/tsubaki-tts-engine-windows-x64-directml
```

### 3. Windows (Lightweight: CPU Only) — Recommended for Base TTS

```bash
dotnet publish -c Release -r win-x64 -p:CpuOnly=true --self-contained true -o ./bin/publish/tsubaki-tts-engine-windows-x64-cpu
```

### 4. Linux (WebGPU + CPU) — Recommended for Voice Cloning

Uses WebGPU for hardware-accelerated OpenVoice voice cloning.

```bash
dotnet publish -c Release -r linux-x64 -p:UseWebGpu=true --self-contained true -o ./bin/publish/tsubaki-tts-engine-linux-x64-webgpu
```

### 5. Linux (Lightweight: CPU Only) — Recommended for Base TTS

```bash
dotnet publish -c Release -r linux-x64 -p:CpuOnly=true --self-contained true -o ./bin/publish/tsubaki-tts-engine-linux-x64-cpu
```

### 6. Linux (CUDA + CPU) — Advanced Users Only

Builds the NVIDIA CUDA version. See the Linux Deployment section for strict hardware and software requirements:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -o ./bin/publish/tsubaki-tts-engine-linux-x64-cuda
```

### 7. Docker (Lightweight CPU)

The provided `Dockerfile` is pre-configured to build the lightweight CPU version to keep your container small and stable:

```bash
docker-compose up --build -d
```

> **Voice model for source builds:** If your checkout has no Piper model, add a matching `.onnx` and `.onnx.json` pair to `Model/` before starting. Ready-to-use binary releases include a default model.

## Packaging a Binary Release

For a downloadable release with license notices, run this command from
`ONNX_Runner`. Regular users can use the ready-made release above.

Windows:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tools\Publish-WithLicenses.ps1 -Variant All
```

Linux (PowerShell 7):

```bash
pwsh -File ./tools/Publish-WithLicenses.ps1 -Variant All
```

`All` builds all six CPU, DirectML, CUDA, and WebGPU variants. To build one,
use `-Variant Windows-CPU`, `Windows-DML`, `Windows-WebGPU`, `Linux-CPU`,
`Linux-CUDA`, or `Linux-WebGPU`. The ZIPs are written to
`Publish-Licensed/<timestamp>/` with the license notices. The script checks
for the Piper model before publishing.

The script also prints the path to one separate corresponding-source package.
Upload it once alongside the binary ZIPs; its large archives are not copied
into each build.

---

# Docker & Linux Deployment

## Docker (Recommended for Servers)

The provided `docker-compose.yml` and `Dockerfile` are highly optimized and pre-configured to build the **Lightweight CPU** version. All native dependencies are handled automatically:

```bash
docker-compose up --build -d
```

## Bare-Metal Linux (CPU)

For bare-metal Linux, **eSpeak NG is required** for phonemization. LAME is optional and only needed for `mp3` and `b64_json`; without it, `wav`, `flac`, `opus`, `aac`, and `pcm` remain available.

```bash
sudo apt-get update
sudo apt-get install -y espeak-ng
sudo apt-get install -y libmp3lame0   # optional, but recommended for MP3 client compatibility
```

Tsubaki detects these native libraries at startup and reports which audio formats are available.

## Bare-Metal Linux (CUDA GPU) — Not Recommended

Tsubaki supports NVIDIA GPU acceleration on Linux, but we **strongly advise against using it** unless absolutely necessary.

For TTS workloads, the performance gain over a modern CPU is often small, while the downsides are significant:

- **Massive Build Size:** The CUDA build is over 1.5 GB larger.
- **High Power Consumption:** GPU execution consumes significantly more power.
- **Dependency Hell:** CUDA requires a compatible system-level NVIDIA stack that is not bundled with Tsubaki.

If you still want to proceed, the host system must provide:

- NVIDIA Linux driver with CUDA 13.x support
- NVIDIA CUDA 13.x runtime libraries / CUDA Toolkit 13.x
- NVIDIA cuDNN 9.x for CUDA 13

CUDA and cuDNN libraries must also be discoverable by the Linux dynamic linker, typically through the system library path or `LD_LIBRARY_PATH`.

> If CUDA or cuDNN is missing, incompatible, or cannot be loaded, the ONNX Runtime CUDA provider may fail to initialize and Tsubaki will fall back to CPU execution.

---

# Server Configuration (appsettings.json)

The `appsettings.json` file is completely pre-configured and ready to use out-of-the-box. Most users only ever need to set the model path and the languages list — everything else can safely be left at its defaults.

---

## Phonemizer & Language Settings

### Cross-Language Pronunciation

Tsubaki does not turn a monolingual Piper model into a true multilingual model. Instead, it allows the active voice to **approximate other languages using the sounds that model already knows how to produce**.

When automatic language detection is enabled, Tsubaki can recognize languages listed in `SupportedLanguages` and phonemize each detected part of the text using the appropriate eSpeak language rules. The same behavior can be requested explicitly with the per-request `language` parameter when the language of the text is already known.

The resulting phonemes are checked against the active Piper model. If a sound is not available in that model's phoneme inventory, Tsubaki maps it to the closest supported alternative. This allows foreign-language speech to remain understandable while naturally retaining some accent or losing distinctions that the base model cannot represent.

In simplified form:

`text → language selection → target-language phonemes → model phoneme adaptation → Piper`

For the most reliable automatic switching, keep the languages you actually expect in `SupportedLanguages`. If the language is already known, passing `language` directly avoids the need to infer it.

### Inline Phoneme Input

Tsubaki can also accept phonemes directly inside normal input text.

- **`[[...]]` is an explicit raw-phoneme block.** Its contents are always treated as phonemes and bypass normal text phonemization and language detection. The phonemes are still validated against the active Piper model, so sounds that the model does not support are adapted to the closest available alternatives.
- **`[...]` and `/.../` are compatibility forms rather than strict commands.** Tsubaki treats them as phoneme input only when their contents actually contain recognizable IPA-style phonetic symbols. Otherwise they remain ordinary text.

For example:

```text
The word is [[həˈloʊ]].
```

explicitly supplies phonemes, while:

```text
The word is [hello].
```

does not automatically bypass normal text pronunciation merely because brackets were used.

This softer handling of single brackets and slashes is intentionally a compatibility safety net for applications or text formats that already use conventional pronunciation notation. Use `[[...]]` when raw phoneme input is intentional and unambiguous.

### Language Detection Configuration

`PhonemizerSettings` controls how Tsubaki detects and routes text outside the active Piper model's own language. The model language is always included automatically; `SupportedLanguages` only adds languages that Tsubaki should actively consider switching to.

```json
"PhonemizerSettings": {
  "SupportedLanguages": ["en", "uk", "fr"],
  "UseLanguageDetector": true,
  "ForeignValidationMaxLetters": 5,
  "MaxBonusMultiplier": 0.60,
  "BonusMinLetterCount": 8,
  "BonusMaxLetterCount": 32,
  "MixedLanguageOverrideThreshold": 0.85,
  "MinSentenceLengthForOverride": 20
}
```

For normal use, keep the defaults and list only the **2–3 additional languages** most likely to appear in your text. Fewer candidates improve both detection reliability and performance.

- `UseLanguageDetector: true` enables automatic language detection and script-aware fallback behavior.
- `UseLanguageDetector: false` disables statistical language detection and its memory/CPU cost.
- Leaving `SupportedLanguages` empty does **not** disable detection; it simply leaves no additional languages beyond the active model language.
- Language values may use base codes such as `"en"`, `"uk"`, `"de"`, `"fr"`, `"cmn"`, or extended eSpeak tags such as `"en-us"`, `"fr-ca"`, and `"en-gb-x-rp"`.

> **Recommendation:** Leave the advanced values unchanged unless automatic detection produces unwanted language switches or fails to preserve the base model's accent.

<details>
<summary><strong>Advanced: How language detection works and how to tune it</strong></summary>

#### 1. Script routing comes first

A different writing system is the easiest case. If Cyrillic text reaches a Latin-script model, for example, Tsubaki can identify the script reliably and use diagnostic letters and script-level fallbacks when statistical language detection is uncertain. This reduces the chance that foreign text will be pronounced letter-by-letter.

Keeping `UseLanguageDetector: true` also keeps this automatic routing path available. If you only need the base language plus script-level handling of clearly different alphabets, `SupportedLanguages` can remain empty.

#### 2. Same-script languages require statistical detection

Languages that share a script are harder to distinguish. English, French, and Spanish all use Latin characters, so Tsubaki uses Lingua to estimate the most likely language.

Detection is performed on **chunks** — a script run between punctuation boundaries — rather than isolated words. A foreign word without punctuation around it is therefore evaluated together with its neighbors, which usually provides more context but can also pull an ambiguous word toward the surrounding language.

#### 3. Short chunks receive a base-language bonus

Very short text is statistically ambiguous, so Tsubaki can bias it toward the active Piper model's language.

| Parameter | Behavior |
| --------- | -------- |
| `MaxBonusMultiplier` | Maximum bonus applied to the base-model language. `0.60` means up to ×1.6. |
| `BonusMinLetterCount` | Chunks at or below this length receive the full bonus. |
| `BonusMaxLetterCount` | Chunks at or above this length receive no bonus. Between the two limits, the bonus is reduced gradually. |

Example: `"Hi"` receives the full short-text bonus and stays English. `"I am Alejandro"` is longer, so the bonus is weaker and a strongly Spanish result can still win.

#### 4. Very short foreign results can be validated again

`ForeignValidationMaxLetters` adds an extra safeguard for short chunks that were classified as foreign. At or below this length, the foreign result must still beat the base-model language after the short-text bonus is applied.

Set it to `0` to disable this extra validation.

#### 5. Whole-sentence confidence can override an ambiguous chunk

The short-text bonus has a limit. For longer sentences, Tsubaki can also measure the active model language across the **whole sentence** and use that context to correct short ambiguous chunks.

| Parameter | Behavior |
| --------- | -------- |
| `MixedLanguageOverrideThreshold` | Minimum whole-sentence confidence required before the base-model language can override a short chunk. Default: `0.85`. |
| `MinSentenceLengthForOverride` | Minimum sentence length in letters before this override is allowed. Default: `20`. |

Only chunks shorter than `BonusMaxLetterCount` are eligible for this correction.

Example: in `"Yes, I am Hermes, an AI model created by Anthropic."`, `"Hermes"` alone may be detected as French. A confidently English surrounding sentence can override that result and keep the English pronunciation.

#### 6. The sentence override is intentionally conservative, not semantic

The override only knows the surrounding sentence's language confidence. It cannot know whether a foreign phrase is accidental or intentional.

For example, in:

```text
...and whispered: c'est la vie.
```

the surrounding sentence can be confident enough in English to override the French phrase despite the punctuation. With the default threshold of `0.85`, a measured English confidence of `0.9666` is sufficient.

If deliberate foreign phrases should switch languages more freely:

- raise `MixedLanguageOverrideThreshold` toward `1.0`;
- set it above `1.0` to disable the sentence-level override entirely;
- or reduce the amount of surrounding base-language context.

#### 7. Practical tuning

| Goal | What to change |
| ---- | -------------- |
| Reduce accidental switches on short text | Increase `MaxBonusMultiplier` and/or `ForeignValidationMaxLetters`. |
| Let deliberate foreign phrases switch more freely | Raise `MixedLanguageOverrideThreshold` toward `1.0`, or above `1.0` to disable the sentence-level override. |
| Reduce ambiguity between similar same-script languages | Keep `SupportedLanguages` limited to languages you actually expect. |
| Disable statistical detection and save RAM | Set `UseLanguageDetector: false`. |
| Keep automatic handling without adding extra same-script languages | Keep `UseLanguageDetector: true` and leave `SupportedLanguages` empty. |

Language detection from a few characters is inherently uncertain. These settings bias that uncertainty toward the behavior that best fits your use case; they cannot make every short name, acronym, or mixed-language phrase unambiguous.

Finally, language routing controls **pronunciation rules**, not the acoustic identity of the Piper model. eSpeak can apply another language or dialect's phonetic rules, but sounds missing from the active Piper model are still mapped to the closest available phonemes. Cross-language speech therefore keeps the base voice and may retain an accent rather than becoming fully native speech.

</details>

---

## Network & Access

- **`Kestrel > Endpoints > Http > Url`** — Defines the port the server listens on. Default is `http://+:5045`.

- **`CorsSettings`** — Controls Cross-Origin Resource Sharing. Setting `"AllowAnyOrigin": true` completely disables access limits and is perfectly fine for local or home use. If set to `false`, the server will only accept requests from the domains listed in `"AllowedOrigins"`, which you can freely edit to secure your endpoints.

- **`ApiSettings > MaxTextLength`** — Imposes a hard character limit on text-to-speech requests. Setting this to `0` removes the limit entirely, which is perfectly fine for personal or home use.

---

## Text Processing

- **`ChunkerSettings`** — Controls sentence chunking and the optional low-latency first split. Normal text is split at sentence boundaries; `MaxChunkLength` is only an emergency cap for unusually long single sentences.

```json
"ChunkerSettings": {
  "MaxChunkLength": 200,
  "EarlySplit": true,
  "SentencePauseSeconds": 0.3
}
```

- **`MaxChunkLength`** — emergency cap for one already-detected sentence. Long sentences prefer a natural pause mark, then whitespace, then a safe character boundary.
- **`EarlySplit`** — allows one conservative clause-level split before the first audio chunk to reduce time to first audio; normal sentence chunking resumes immediately afterward. This can slightly change the rhythm or intonation of the first sentence. It is mainly useful when low latency matters; for non-streaming output there is usually little benefit to enabling it. `/tsbk/audio/speech` can override it per request with `early_split`.
- **`SentencePauseSeconds`** — pause added only after real sentence boundaries. Early and emergency continuation chunks do not receive this artificial sentence pause.

---

## Resource Management

- **`HardwareSettings`** — Tells the server's internal queueing system how many generation requests are allowed to run at the same time, and handles hardware routing. **For home use, you can completely ignore this section and leave the defaults.**

```json
"HardwareSettings": {
  "MaxConcurrentGpuRequests": 3,
  "MaxConcurrentCpuRequests": 2,
  "PiperGpuDeviceId": 0,
  "OpenVoiceGpuDeviceId": 0,
  "ForcePiperToCpu": true
}
```

| Parameter                 | Description                                                                                                                                                                                                                                                                                                                                                                                                            |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `MaxConcurrentGpuRequests` | Maximum number of requests processed on the GPU simultaneously. **On CUDA (NVIDIA, Linux only), this is primarily a concurrency throttle** — concurrent requests share the same loaded model rather than requiring one full model copy per request. **On DirectML (Windows — NVIDIA, AMD, and Intel all route through this backend), this number is not just a throttle — it is the exact size of the session pool kept resident in VRAM**, because DirectML cannot run one session from multiple threads concurrently. Raising this value on DirectML increases VRAM usage predictably and linearly: model size × `MaxConcurrentGpuRequests`, and separately again for the OpenVoice Tone Color Converter if voice cloning is enabled. |
| `MaxConcurrentCpuRequests` | Maximum number of concurrent CPU-based generation tasks. `0` or a negative value automatically derives a safe limit from the available logical processors and `OnnxSettings.Cpu.IntraOpNumThreads`. If `IntraOpNumThreads` is also automatic (`0`), Tsubaki uses one concurrent CPU request. |
| `PiperGpuDeviceId`         | Hardware index (starting at `0`) of the GPU used to execute the base Piper neural network. |
| `OpenVoiceGpuDeviceId`     | Hardware index of the GPU used to execute the OpenVoice Tone Color Converter. Can be assigned a different ID in multi-GPU setups to split the computational load. |
| `ForcePiperToCpu`          | Forces the base Piper model to execute on the CPU regardless of GPU presence. When `true` (Hybrid Routing), the CPU handles parallel Piper text-to-speech generation while the GPU is reserved exclusively for the computationally heavy OpenVoice cloning passes. This keeps CPU synthesis independent from the GPU voice-conversion stage, allowing the CPU to prepare additional requests while the GPU processes a cloned voice. |

> **DirectML users:** think of this setting as a direct trade — each unit of `MaxConcurrentGpuRequests` buys one more simultaneous request, at the cost of one more full copy of the relevant model(s) sitting in VRAM. CUDA and CPU users don't pay this cost, since they share one session across all concurrent requests. **On WebGPU, GPU voice conversion is currently processed one request at a time for stability, while multiple CPU synthesis requests can still be prepared in parallel.**

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

- **`EffectsSettings`** — Server-wide default character effect and spatial environment, automatically applied to every request unless overridden by a custom client. See *Server-Side DSP Defaults* above for why this matters specifically for OpenAI-compatible clients.

- **`DspSettings`** — Adds an audio cleanup pass (Low-Pass Filter), server-wide default pitch and volume, and a fixed `VolumeBoosterDb` gain correction. This lets you calibrate the engine's baseline output once while keeping `DefaultVolume`/`volume` available for playback-level control.

- **`ClonerSettings`** — Server defaults for cloning strength, OpenVoice conversion stability, and reference-audio loudness normalization. See *Fine-Tuning Cloning Behavior* and *Cloned Voice Volume* above.
- **`EnableCloning`** — Enables or disables OpenVoice voice cloning. Leave it `true` to use voices stored in the `Voices/` folder; when enabled, those voices are discovered automatically at server startup and appear in the Web Dashboard voice list.

---

# ONNX Runtime Optimization

This section provides low-level control over the internal MLAS (Microsoft Linear Algebra Subprograms) math engine, heavily optimizing execution. Since the CPU and GPU execution paths have fundamentally different concurrency needs, `Cpu` and `Gpu` use independent threading and memory profiles rather than a single shared configuration. The defaults are already configured as a balanced "golden standard" for home use. **If you are not tuning performance, leave this entire section unchanged.** Change these only if you understand the consequences.

```json
"OnnxSettings": {
  "EnableGraphOptimization": true,
  "Cpu": {
    "ExecutionMode": "Sequential",
    "IntraOpNumThreads": 4,
    "InterOpNumThreads": 1,
    "EnableMemoryPattern": true,
    "EnableCpuMemArena": true
  },
  "Gpu": {
    "ExecutionMode": "Sequential",
    "IntraOpNumThreads": 1,
    "InterOpNumThreads": 1,
    "EnableMemoryPattern": true,
    "EnableCpuMemArena": false
  }
}
```

| Parameter             | Description                                                                                                                                                                                                                                                                                                                        |
| --------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `IntraOpNumThreads`   | Threads used for matrix math within a single neural network node. `0` lets ONNX Runtime choose the thread count automatically. The `Cpu` profile defaults to `4` to balance per-request throughput with concurrent requests; the `Gpu` profile defaults to `1`. |
| `InterOpNumThreads`   | Parallelization across different graph nodes. For TTS (which is strictly sequential), this must **always be `1`**. Higher values cause thread thrashing and micro-stutters.                                                                                                                                                       |
| `EnableMemoryPattern` | Pre-allocates memory blocks during model load rather than dynamically during inference. Decreases latency per request by 5–10%.                                                                                                                                                                                                    |
| `EnableCpuMemArena`   | Utilizes an isolated memory arena for ONNX tensors. **Critical for .NET:** Bypasses the C# Garbage Collector entirely during audio generation, eliminating GC-induced freezes on long texts. Disabled by default in the `Gpu` profile, since this optimization targets CPU-resident memory and doesn't apply to tensors that live in VRAM. |
| `ExecutionMode`       | `"Sequential"` is the safest and fastest mode for Piper and OpenVoice architectures, as they do not benefit from parallel graph execution.                                                                                                                                                                                         |

## Concurrency & CPU Bottlenecks

The TTS engine and API are fully thread-safe and natively support concurrent HTTP requests. Two settings control CPU concurrency:

- `OnnxSettings.Cpu.IntraOpNumThreads` controls the thread budget used by one inference request. `0` lets ONNX Runtime choose automatically.
- `HardwareSettings.MaxConcurrentCpuRequests` controls how many CPU generation requests may run at once. A fixed value is an explicit cap. `0` or a negative value lets Tsubaki derive the limit from the available logical processors and the configured `IntraOpNumThreads`; when `IntraOpNumThreads` is `0`, the automatic request limit is conservatively `1`.

The shipped defaults (`IntraOpNumThreads: 4`, `MaxConcurrentCpuRequests: 2`) are intended as a balanced home-use configuration.

**Handling High-Load Environments:**

- **Option A (Lowest Latency, single user):** Keep `MaxConcurrentCpuRequests: 1` and tune `IntraOpNumThreads` for the best single-request latency.
- **Option B (More simultaneous users):** Lower `IntraOpNumThreads` to `1` or `2` and raise `MaxConcurrentCpuRequests`, or set `MaxConcurrentCpuRequests: 0` to let Tsubaki derive a concurrency limit automatically.

Both settings only affect the CPU execution path — GPU threading and concurrency are controlled independently via `OnnxSettings.Gpu` and `HardwareSettings.MaxConcurrentGpuRequests`.

---

# API Endpoints

**OpenAI-compatible:**

| Method | Endpoint          | Description                     |
| ------ | ----------------- | -------------------------------- |
| `POST` | `/v1/audio/speech` | Main TTS endpoint                |
| `GET`  | `/v1/models`       | OpenAI-compatible model listing  |
| `GET`  | `/v1/models/{id}`  | OpenAI-compatible model by ID    |
| `GET`  | `/v1/health`       | Server health check              |

**Tsubaki Endpoint:**

| Method | Endpoint                   | Description                                        |
| ------ | -------------------------- | --------------------------------------------------- |
| `POST` | `/tsbk/audio/speech`       | Main TTS endpoint, full parameter set               |
| `POST` | `/tsbk/audio/phonemize`    | Text phonemization (for diagnostics)                |
| `GET`  | `/tsbk/audio/voices`       | List available voices                               |
| `GET`  | `/tsbk/audio/effects`      | List available DSP effects                          |
| `GET`  | `/tsbk/audio/environments` | List available acoustic environments                |
| `GET`  | `/tsbk/server/status`      | Reports what's enabled and current server defaults  |

`/tsbk/server/status` returns the server's current configuration as JSON — which features are enabled (`voiceCloning.enabled`, `effects.enabled`, `streaming.enabled`), the resolved defaults every request falls back to (`dsp.defaultPitch`, `dsp.defaultVolume`, `effects.defaultEnvironment`, ...), and the exact combined gain `VolumeBoosterDb` currently applies (`dsp.volumeBoosterDb`, `dsp.defaultTotalGainDb`). Intended for building a frontend that adapts its own UI to what the server actually supports — e.g. hiding cloning controls entirely when `voiceCloning.enabled` is `false` — and for a client that wants an exact absolute output level to discover and counteract the always-on volume booster instead of guessing.

**Universal:**

| Method | Endpoint  | Description         |
| ------ | --------- | -------------------- |
| `GET`  | `/health` | Server health check  |

## Swagger UI

A detailed Swagger UI with every parameter (Pitch, Volume, NoiseScale, CloneIntensity, etc.) is available at:

```
http://localhost:5045/swagger
```

Swagger is enabled in Development mode.

---

# Open Source Credits & Acknowledgements

Tsubaki TTS Engine stands on the shoulders of giants. A massive thank you to the authors of the original models and open-source libraries that made this possible.

## AI Models & Datasets

- [**Piper TTS**](https://github.com/rhasspy/piper) — The core VITS neural network architecture by Rhasspy.
- [**OpenVoice V2**](https://github.com/myshell-ai/OpenVoice) — The tone-color voice cloning architecture by MyShell.
- [**PHOIBLE 2.0**](https://phoible.org/) — Cross-linguistic phonological data used for fallback phoneme matching. Edited by Steven Moran and Daniel McCloy; CC BY-SA 3.0.

## C# / .NET Libraries

- [**Microsoft.ML.OnnxRuntime**](https://github.com/microsoft/onnxruntime) — CPU and GPU neural network inference.
- [**NAudio & NAudio.Lame**](https://github.com/naudio/NAudio) — Audio processing and the .NET LAME integration.
- [**Concentus**](https://github.com/lostromb/concentus) — Pure C# Opus encoding.
- [**SoundTouch.Net**](https://github.com/owoudenberg/soundtouch.net) — High-quality pitch and tempo shifting (WSOLA algorithm).
- [**SearchPioneer.Lingua**](https://github.com/searchpioneer/lingua-dotnet) — Fast, offline language detection for foreign word pronunciation.

## Native Components

- [**eSpeak NG**](https://github.com/espeak-ng/espeak-ng) — Phonemization and language/dialect pronunciation rules.
- [**LAME**](https://lame.sourceforge.io/) — MP3 encoding backend used through NAudio.Lame.

Additional third-party license and attribution information is listed in `THIRD_PARTY_NOTICES.txt`.

## Voice Sources & Attribution

The 13 named voice fingerprints (`alloy`, `ash`, `ballad`, `cedar`,
`coral`, `echo`, `fable`, `marin`, `nova`, `onyx`, `sage`, `shimmer`, and `verse`)
come from **LibriTTS-R** recordings. Their original speaker IDs were not
retained, so attribution is given to the corpus:

> Yuma Koizumi, Heiga Zen, Shigeki Karita, Yifan Ding, Kohei Yatabe,
> Nobuyuki Morioka, Michiel Bacchiani, Yu Zhang, Wei Han, Ankur Bapna.
> *"LibriTTS-R: A Restored Multi-Speaker Text-to-Speech Corpus"*, Interspeech 2023.
> Source: http://www.openslr.org/141/
> License: [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)

The remaining two bundled voices use CC0 recordings: `female` from
[vero.marengere](https://freesound.org/people/vero.marengere/sounds/514877/)
and `male` from [aarongbuk](https://freesound.org/people/aarongbuk/sounds/222599/).
The original WAVs and generated `.voice` files are included in `Voices/`.
File hashes and further provenance are in `VOICE_PROVENANCE.txt`.

No audio, model output, or vocal characteristics from a commercial TTS
provider were used to create these fingerprints.

### A note on voice naming

Some bundled voice names (e.g. `alloy`, `echo`, `nova`) intentionally match
names used by OpenAI's text-to-speech API. This is purely a **compatibility
convenience** for clients that only accept a fixed voice list and don't
allow a custom voice name to be entered — not a claim that these are
OpenAI's voices, or that Tsubaki is affiliated with, endorsed by, or
sponsored by OpenAI. The actual vocal characteristics behind each of these
names come entirely from LibriTTS-R speakers (see Attribution above) and
will sound different from OpenAI's official voices.

---

# License & Usage

Tsubaki TTS Engine's original code is licensed under **GPL-3.0-or-later**;
see `LICENSE`. Bundled components and voices retain their own licenses and
attribution, listed in `THIRD_PARTY_NOTICES.txt` and `VOICE_PROVENANCE.txt`.
For binary releases, use the [packaging helper](#packaging-a-binary-release)
to include notices and make the corresponding source available separately.

A link back to this repository in your credits is appreciated.
