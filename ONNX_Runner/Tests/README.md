# Tsubaki TTS Engine text analysis tests

[Tsubaki TTS Engine](https://github.com/MrHryhorii/SmartStack/tree/main/ONNX_Runner)
is a local text-to-speech engine built with C# (.NET 10), Piper voice models, and
ONNX Runtime. These standalone console projects check the text preprocessing
used before speech synthesis: multilingual sentence boundaries, abbreviations,
initials, language segmentation, punctuation normalization, and managed allocations.

## What the tests check

| Project | Purpose |
| --- | --- |
| [TextChunkerRegression](TextChunkerRegression/) | Check sentence boundaries around abbreviations, initials, names, references, technical addresses, and Unicode punctuation. Verify line boundaries, list markers, chunk limits, completion flags, Unicode text integrity, and optional custom rules. |
| [LanguageSegmentationRegression](LanguageSegmentationRegression/) | Check that tokenization preserves abbreviations, initials, file names, and lexical connectors. Verify that shared rules remain consistent through chunking, language segmentation, punctuation mapping, and phonemizer preprocessing. |
| [PunctuationRegression](PunctuationRegression/) | Check Unicode punctuation, including Thai sentence endings, against the symbols supported by a Piper model. Verify custom punctuation rules, compound marks, lexical connectors, normalization idempotence, and model control-token handling. |
| [TextAnalysisAllocations](TextAnalysisAllocations/) | Detect unexpected managed allocations in text analysis using explicit budgets, and report other scenarios for local investigation. See [Managed allocation checks](AllocationResults.md) for the method and output format. |

Expected boundaries and punctuation are defined by explicit fixtures. Fixed-seed
generated cases check source preservation, Unicode text elements, splitting progress,
size limits, and completion flags. Ambiguous initials and abbreviations have documented
heuristic expectations; grammatical understanding requires a different kind of analysis.

Pipeline checks use a test double at the native eSpeak boundary to inspect the text
sent for phonemization. Speech quality, IPA accuracy, and native synthesis behavior
require separate tests.

## Requirements

Use the .NET 10 SDK and the complete `ONNX_Runner` source tree. The test projects
compile the production text analysis sources directly and run independently of the
TTS server, voice models, and native speech synthesis binaries.

`dotnet run` builds each project and restores its dependencies. The language
segmentation and allocation projects use `SearchPioneer.Lingua` from NuGet.

## Run the tests

Run these commands from the `ONNX_Runner` directory containing `ONNX_Runner.csproj`:

```shell
dotnet run --project Tests/TextChunkerRegression/TextChunkerRegression.csproj -c Release -- --failures-only
dotnet run --project Tests/LanguageSegmentationRegression/LanguageSegmentationRegression.csproj -c Release -- --failures-only
dotnet run --project Tests/PunctuationRegression/PunctuationRegression.csproj -c Release -- --failures-only
dotnet run --project Tests/TextAnalysisAllocations/TextAnalysisAllocations.csproj -c Release -- --iterations=10000
```

The regression runners print failures and a summary. Omit `--failures-only` to
display passing case messages. Each project returns a nonzero exit code when a
check or allocation budget fails, so the commands can also be used in CI.

`TextChunkerRegression` accepts a category filter for focused checks:

```shell
dotnet run --project Tests/TextChunkerRegression/TextChunkerRegression.csproj -c Release -- --failures-only --category="Ambiguous letter endings"
dotnet run --project Tests/TextChunkerRegression/TextChunkerRegression.csproj -c Release -- --failures-only --category="Name binding boundaries"
```

For engine setup, APIs, and text processing configuration, see the
[main Tsubaki TTS Engine documentation](https://github.com/MrHryhorii/SmartStack/tree/main/ONNX_Runner).
