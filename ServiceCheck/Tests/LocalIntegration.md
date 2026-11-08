# Local Tsubaki API Contract Check

Date: 2026-11-08  
Target: `http://127.0.0.1:5045/`  
Method: manual PowerShell requests, header inspection, and audio playback.  
Result: Passed.

## Health response

`GET /health` returned these observed fields:

| Field | Value |
| --- | --- |
| status | ok |
| service | Tsubaki TTS Engine |
| version | 1.0.10 |
| synthesisReady | true |

## Speech request

`POST /v1/audio/speech` was called with:

```json
{
  "model": "tts-1",
  "input": "This is a Tsubaki service check.",
  "voice": "piper_base",
  "response_format": "wav",
  "speed": 1.0,
  "stream": false
}
```

The request completed successfully and saved the response to a file.

## Audio observations

| Property | Observed value |
| --- | --- |
| Container | RIFF/WAVE |
| Encoding | PCM, format code 1 |
| Channels | 1 |
| Sample rate | 22050 Hz |
| Bits per sample | 16 |
| Block alignment | 2 bytes |
| Byte rate | 44100 bytes/s |
| Payload size | 105434 bytes |
| Audio data size | 105390 bytes |
| Frame count | 52695 |
| Calculated duration | Approximately 2.39 seconds |

The declared RIFF and data lengths match the received file size.
The first inspected audio samples are nonzero.
Manual playback confirmed the requested sentence.

## Coverage

This record covers the server API through a local loopback connection.
Requests were executed manually in PowerShell.
The ServiceCheck CLI speech command and automated WAV inspection remain
part of the next implementation phase.