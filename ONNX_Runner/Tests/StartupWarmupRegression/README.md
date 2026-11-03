# Two-pass startup warm-up

Run from ONNX_Runner with .NET 10:

```sh
dotnet run --project Tests/StartupWarmupRegression -c Release -p:CpuOnly=true -- --report warmup-results.json
```

Add `--http results-directory` to launch five isolated CPU servers using the bundled real Piper/OpenVoice models and native eSpeak NG/MP3 dependencies. The profiles cover base-only synthesis, enabled cloning/detection, a zero default cloning intensity, disabled processing modules and missing models. Both warm-up passes select the same available cloned voice through Tsubaki with explicit clone_intensity=1; base synthesis is the fallback when cloning or voices are unavailable. Each server permits only one public request per ten-minute window; two internal startup requests must not consume it. Tests verify warm-up IDs -1 and 0, actual clone/effect/environment/pitch/output-rate/delivery plans, two successful syntheses (or two reported model failures), first public request ID 1, normal rate limiting afterward and graceful Unix shutdown. These profiles do not test physical Windows/GPU execution.

The default checks exercise one-use startup markers, forged/stale headers, concurrent claims, skipped/failed startup, positive unique ordinary IDs, cancellation, voice/codec fallback and allocation-free inactive marker matching. They also check that the supplied multilingual/technical text is preserved across two halves, every declared request field is serialized with a non-null value, DSP controls are active and the production reader/adapter retains them. The ready banner follows both warm-up attempts. Startup failure is reported and remains non-fatal; cancellation closes the markers. The synthesis concurrency gate still applies to startup traffic.

The request profiles are:

| ID | Language | Effect / environment | Pitch / volume / speed | Output | Delivery |
| --- | --- | --- | --- | --- | --- |
| -1 | en | Telephone / Room | 1.05 / 0.8 / 1.05 | 24000 Hz | Buffered audio |
| 0 | auto | FmRadio / InnerVoice | 0.95 / 1.1 / 0.95 | 32000 Hz | Streaming SSE |

Both include two supported equalizer profiles, nonzero effect/environment intensities, cloning intensity 1, explicit noise/tone/filter controls and MP3 (PCM when native MP3 is unavailable). The first enables early splitting and spatial-tail extension; the second disables them. The text includes filenames, a URL, a version, punctuation and language changes. In the detection-enabled fixture, statistical Lingua diagnostics must occur for request 0 and remain absent from forced-English request -1.

Global detector/cloning/effects/filter switches remain respected. Model and instructions are accepted compatibility fields that the engine intentionally ignores; including them does not create additional inference behavior. Two requests prime selected processing paths. They do not cover every codec, effect/environment variant, tensor shape, language or GPU allocation. The longer passage and active processing increase startup time.
