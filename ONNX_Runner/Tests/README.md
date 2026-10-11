# Tsubaki TTS Engine regression tests

[Tsubaki TTS Engine](https://github.com/MrHryhorii/SmartStack/tree/main/ONNX_Runner)
is a local text-to-speech engine built with C# (.NET 10), Piper voice models, and
ONNX Runtime. These standalone console projects check the text preprocessing
used before speech synthesis: multilingual sentence boundaries, abbreviations,
initials, language segmentation, punctuation normalization, technical-symbol pronunciation,
and managed allocations. Server configuration checks also cover capability reporting
and optional CPU waveform comparisons for DSP controls.

## What the tests check

| Project | Purpose |
| --- | --- |
| [TextChunkerRegression](TextChunkerRegression/) | Check sentence boundaries around abbreviations, initials, names, references, technical addresses, and Unicode punctuation. Verify line boundaries, list markers, chunk limits, completion flags, Unicode text integrity, and optional custom rules. |
| [LanguageSegmentationRegression](LanguageSegmentationRegression/) | Check source preservation, shared rules, abbreviations, initials, and technical phrase boundaries. Exercise real Lingua with competing same-script languages and model/script routing with empty, duplicate, or unmapped candidates. Verify configuration binding, confidence and margin boundaries, technical-context inheritance, dialects, forced languages, and the Latin classification of romaji. |
| [PunctuationRegression](PunctuationRegression/) | Check Unicode punctuation, including Thai sentence endings, against the symbols supported by a Piper model. Verify custom punctuation rules, compound marks, lexical connectors, normalization idempotence, and model control-token handling. |
| [TechnicalSpeechIntegration](TechnicalSpeechIntegration/) | Check complete symbol descriptions against explicitly declared spoken names. Preserve literal punctuation names, localized pronunciation, compatible fullwidth characters, native voice state, callbacks, and cached output under concurrent calls. Verify adapted phoneme coverage and complete technical names in Piper IDs. Compare statistical and model/script routing against explicit-language native references. |
| [TextAnalysisAllocations](TextAnalysisAllocations/) | Detect unexpected managed allocations in text analysis using explicit budgets, and report other scenarios for local investigation. See [Managed allocation checks](#managed-allocation-checks) below. |
| [ServerConfigurationRegression](ServerConfigurationRegression/) | Check version consistency, liveness versus readiness, available capabilities, detector candidates and tuning, configuration defaults, named format validation, JSON ranges and request overrides, and effective emergency chunk limits. Optional CPU waveform checks cover the DSP master switch, character/spatial overrides, tails, volume, and safe voice fallback with cloning enabled. |

Expected boundaries and punctuation are defined by explicit fixtures. Fixed-seed
generated cases check source preservation, Unicode text elements, splitting progress,
size limits, and completion flags. Ambiguous initials and abbreviations have documented
heuristic expectations; grammatical understanding requires a different kind of analysis.

Pipeline checks use a test double at the native eSpeak boundary to inspect the text
sent for phonemization. Forced-language fixtures test structural speech parts;
automatic fixtures use real Lingua models or model/script fallback, with independently
declared language expectations.
Separate speech parts are allowed within one shared language-analysis phrase.
The optional `TechnicalSpeechIntegration` runner uses real eSpeak and a model JSON
inventory. Waveform quality still requires listening tests.

## Requirements

Use the .NET 10 SDK and the complete `ONNX_Runner` source tree. The four managed test projects
compile production sources directly and run independently of the TTS server,
voice models, and native speech synthesis binaries. The optional native integration
runner additionally needs eSpeak, its data, and a Piper model JSON; it does not need
the ONNX weights or start the server.

`ServerConfigurationRegression` references the actual engine assembly; the commands
below select the CPU build. Its
default checks need no model weights or native synthesis library. Optional waveform
checks need a matching Piper `.onnx` / `.onnx.json` pair and the native eSpeak
dependencies used by the engine. They run the public synthesis pipeline without
starting the HTTP server. The two-path mode leaves cloning disabled; adding the
OpenVoice and voice directories enables the optional cloning checks below.

The engine project excludes `Tests/**` from compilation and publishing. Listing
test projects in the solution adds build work when building the whole solution;
it does not load or execute tests in a running TTS server.

`dotnet run` builds each project and restores its dependencies. The language
segmentation and allocation projects use `SearchPioneer.Lingua` from NuGet.

## Run the tests

Run these commands from the `ONNX_Runner` directory containing `ONNX_Runner.csproj`:

```shell
dotnet run --project Tests/TextChunkerRegression/TextChunkerRegression.csproj -c Release -- --failures-only
dotnet run --project Tests/LanguageSegmentationRegression/LanguageSegmentationRegression.csproj -c Release -- --failures-only
dotnet run --project Tests/PunctuationRegression/PunctuationRegression.csproj -c Release -- --failures-only
dotnet run --project Tests/TextAnalysisAllocations/TextAnalysisAllocations.csproj -c Release -- --iterations=10000
dotnet run --project Tests/ServerConfigurationRegression/ServerConfigurationRegression.csproj -c Release -p:CpuOnly=true -- --failures-only
```

The regression runners print failures and a summary. Omit `--failures-only` to
display passing case messages. Each project returns a nonzero exit code when a
check or allocation budget fails, so the commands can also be used in CI.

`TextChunkerRegression` accepts a category filter for focused checks:

```shell
dotnet run --project Tests/TextChunkerRegression/TextChunkerRegression.csproj -c Release -- --failures-only --category="Ambiguous letter endings"
dotnet run --project Tests/TextChunkerRegression/TextChunkerRegression.csproj -c Release -- --failures-only --category="Name binding boundaries"
```

## Native technical-speech integration

From the same `ONNX_Runner` directory, using the native dependencies configured for Tsubaki:

```shell
dotnet run --project Tests/TechnicalSpeechIntegration/TechnicalSpeechIntegration.csproj -c Release -- Model/en_US-hfc_female-medium.onnx.json
```

Use any compatible Piper model JSON to check its actual phoneme inventory. Windows
uses the bundled `PiperNative/espeak-ng.dll`; Linux uses `libespeak-ng.so.1` when no
local library is present. Two optional arguments select an exact native library and
the parent directory containing `espeak-ng-data`. The runner returns a nonzero exit
code on failure and prints the individual checks and a summary. It checks phonemes
and tensor IDs; it does not assert synthesized audio quality.

Symbol checks distinguish ordinary dictionary descriptions from literal character
names. Multiword descriptions such as `not equal to` and `square root` must remain
complete. Independent references use the declared words, including pronunciation
before a following variable; lexical comparisons allow stress differences while
still checking every spoken phoneme. Literal punctuation and existing localized
names are compared with direct native character calls. English fallback checks
apply where the native character path already switches to English. Names absent
from the installed dictionaries can still use eSpeak's Unicode-code fallback.

## Server configuration and CPU synthesis

Run the default `ServerConfigurationRegression` command above for managed status
and configuration checks. To include real waveform checks, pass both model paths:

```shell
dotnet run --project Tests/ServerConfigurationRegression/ServerConfigurationRegression.csproj -c Release -p:CpuOnly=true -- --failures-only Model/en_US-hfc_female-medium.onnx Model/en_US-hfc_female-medium.onnx.json
```

`-p:CpuOnly=true` selects the lightweight CPU engine for this runner. When building
the whole solution, the test reference follows the same build variant as the server.
The runner uses bundled eSpeak data from `PiperNative/` and the engine's native
library resolver. On Linux, install eSpeak NG; `TSUBAKI_ESPEAK_LIBRARY` can select
an explicit compatible library. Keep the working directory at `ONNX_Runner` so
`PiperNative/` and `PHOIBLE/` resolve correctly.

Waveform comparisons use one CPU inference thread, zero synthesis noise, and
the same technical text. Disabled effects must return the same dry PCM and length,
even with configured or requested spatial effects. Enabled spatial processing must
change the output and extend a long reverb tail; volume control must still work
with effects disabled. These checks validate observable behavior, not perceived
voice quality. `--synthesis-only` runs only these optional checks and requires both
model paths. No synthesized files are written by the runner.

To also test voice cloning and its fallback, pass the OpenVoice model directory
and the directory containing `female.voice`:

```shell
dotnet run --project Tests/ServerConfigurationRegression/ServerConfigurationRegression.csproj -c Release -p:CpuOnly=true -- --failures-only Model/en_US-hfc_female-medium.onnx Model/en_US-hfc_female-medium.onnx.json Cloner Voices
```

`Cloner` must contain `tone_extract.onnx`, `tone_color.onnx`, and `tone_config.json`.
The runner generates the model's source fingerprint in memory, loads the target
fingerprint, and checks that an unavailable voice or missing source fingerprint
preserves dry base audio even with the cloning-only low-pass filter enabled.
Zero clone intensity and disabled cloning must also bypass conversion and filtering.
The global DSP switch must still allow actual voice conversion. These optional
checks load real OpenVoice models on CPU; the default managed checks do not.

## Managed allocation checks

`--iterations` accepts a positive integer and defaults to `5000` when omitted.
Use Release for measurements. The allocation runner returns `0` when all budgets
pass, `1` when a budget fails, and `2` when the iteration argument is invalid.

<details>
<summary><strong>Allocation budgets and measurement method</strong></summary>

| Scenarios | Allocation contract |
| --- | --- |
| Technical-token classification and numeric pronunciation exceptions | Zero managed bytes allocated during classification. |
| Abbreviation, name-binding, and lowercase-letter period classification | Zero managed bytes allocated during classification. |
| Inherited forced-language code and unchanged punctuation | Zero managed bytes allocated for these unchanged paths. |
| Whole-input sentence chunking, with and without abbreviations | At most 96 managed bytes per call for the returned list and array on .NET 10. The fixtures protect against copying the input string or reserving an unnecessarily large result array. |
| Punctuation rewriting, early and Unicode emergency splitting, forced and automatic language tokenization | Measurements for local investigation; these scenarios have no enforced allocation budget. |

These budgets apply to the runner's fixtures. Chunk lists, rewritten strings,
and language tokens can require output allocations in other inputs.

Inputs, delegates, rule catalogs, punctuation inventories, and language-detector
initialization are prepared before measurement. Each scenario receives 2048 warmup
calls, then the requested number of synchronous measured calls.

The runner prints the runtime, architecture, build configuration, and scenario rows.
`B/op` is managed bytes allocated per operation, measured with
`GC.GetAllocatedBytesForCurrentThread()`. `ns/op` is elapsed nanoseconds per operation.
`Allocation budget` shows `PASS` or `FAIL` for an enforced budget, or `reported` for
an informational scenario. Timing has no pass/fail threshold.

Compare measurements using the same runtime, architecture, configuration, and inputs.
The measurements cover current-thread managed allocations; retained memory, native
allocations, and complete speech synthesis cost need separate measurements. Forced-language
tokenizer scenarios bypass statistical detection; automatic comparison scenarios include
Lingua after model initialization. Script-only scenarios measure routing without Lingua.
All scenarios use the same per-scenario warmup.

</details>

For engine setup, APIs, and text processing configuration, see the
[main Tsubaki TTS Engine documentation](https://github.com/MrHryhorii/SmartStack/tree/main/ONNX_Runner).
