# Tsubaki TTS Engine managed allocation checks

`TextAnalysisAllocations` checks managed allocation budgets in the text preprocessing
used by [Tsubaki TTS Engine](https://github.com/MrHryhorii/SmartStack/tree/main/ONNX_Runner),
a local C#/.NET text-to-speech engine. It helps detect temporary allocations introduced
by changes to sentence splitting, abbreviation classification, language codes, and
punctuation handling.

## Run

Use the .NET 10 SDK and run from the `ONNX_Runner` source directory:

```shell
dotnet run --project Tests/TextAnalysisAllocations/TextAnalysisAllocations.csproj -c Release -- --iterations=10000
```

`--iterations` accepts a positive integer and defaults to `5000` when omitted.
Use Release for measurements. The project restores its NuGet dependencies, including
`SearchPioneer.Lingua`, and runs independently of voice models and native synthesis.

## What is checked

| Scenarios | Allocation contract |
| --- | --- |
| Abbreviation, name-binding, and lowercase-letter period classification | Zero managed bytes allocated during classification. |
| Inherited forced-language code and unchanged punctuation | Zero managed bytes allocated for these unchanged paths. |
| Whole-input sentence chunking, with and without abbreviations | At most 96 managed bytes per call for the returned list and array on .NET 10. The fixtures protect against copying the input string or reserving an unnecessarily large result array. |
| Punctuation rewriting, early and Unicode emergency splitting, forced-language tokenization | Measurements for local investigation; these scenarios have no enforced allocation budget. |

These budgets are contracts for the runner's fixtures. Chunk lists, rewritten strings,
and language tokens can require output allocations in other inputs.

## Measurement and output

Inputs, delegates, rule catalogs, punctuation inventories, and language-detector
initialization are prepared before measurement. Each scenario receives 2048 warmup
calls, then the requested number of synchronous measured calls.

The runner prints the runtime, architecture, build configuration, and scenario rows:

| Column | Meaning |
| --- | --- |
| `B/op` | Managed bytes allocated per operation, measured with `GC.GetAllocatedBytesForCurrentThread()`. |
| `ns/op` | Elapsed nanoseconds per operation for local investigation. |
| `Allocation budget` | `PASS` or `FAIL` for an enforced budget, or `reported` for an informational scenario. |

The exit code is `0` when all allocation budgets pass, `1` when a budget fails,
and `2` when the iteration argument is invalid. Timing has no pass/fail threshold.

Compare measurements using the same runtime, architecture, configuration, and inputs.
The measurements cover current-thread managed allocations; retained memory, native
allocations, and complete speech synthesis cost need separate measurements. Tokenizer
scenarios force a language, so statistical language detection is outside the measured path.

See the [test overview and regression commands](README.md) for functional checks,
and the [main Tsubaki TTS Engine project](https://github.com/MrHryhorii/SmartStack/tree/main/ONNX_Runner)
for engine documentation.
