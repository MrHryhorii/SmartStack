# Server defaults and request overrides

Run from the ONNX_Runner project directory using .NET 10:

```sh
dotnet run --project Tests/ServerDefaultsRegression -c Release -p:CpuOnly=true -- --report defaults-results.json
```

Add `--synthesis` to exercise native CPU inference with original miniature ONNX graphs. The graphs echo the resolved Piper scales, blended voice embedding and tone temperature into audio. They verify that selected values reach inference and output processing; they do not measure speech quality. Native eSpeak NG, the bundled model configuration and phoneme data are required.

Add `--http results-directory` to start isolated production HTTP servers with real bundled Piper/OpenVoice models. The test uses temporary ports, disables browser launch, runs on CPU, disables statistical language detection and raises the request rate limit. A temporary copy of the Piper configuration sets both noise controls to zero for reproducible comparisons. The shipped appsettings.json and model files are not edited. The suite checks shipped/custom defaults, provider priority, request isolation, codecs, streaming, SSE, cloning and switches. Four separate processes exercise rejected unsafe startup configuration. On Windows, child processes are terminated after the checks; on Unix they receive SIGINT.

```sh
dotnet run --project Tests/ServerDefaultsRegression -c Release -p:CpuOnly=true -- --synthesis --http defaults-http --report defaults-results.json
```

The current request policy favors successful speech:

- Missing, null or unusable optional values inherit their applicable defaults. Unusable values include non-finite numbers, incompatible JSON types, unknown effect/environment names and profile lists without a recognized entry.
- Valid finite numbers retain the declared clamps. Finite numeric strings remain supported for float controls; samplerate requires an integer JSON number. A string samplerate is ignored.
- Zero, false, None and an empty equalizer array remain explicit controls. Invalid fields do not discard other valid fields.
- Unsupported output format/framing falls back to MP3/audio. OpenAI requests continue to ignore Tsubaki-only extensions.
- Missing, blank or non-string input, malformed JSON and unsupported media types cannot be synthesized. Model/native availability checks still apply.
- Unsafe global configuration is diagnosed at startup, with the setting path. Global configuration changes require a restart; request overrides remain local to a single synthesis.

Declared class fallbacks and the shipped preset are recorded separately. In particular, the shipped preset deliberately uses 11000 Hz cleanup, Q 0.577, +8 dB baseline gain and tone temperature 0.7; absent configuration uses the class fallbacks. No DSP coefficients or authored profiles are modified by this work.
