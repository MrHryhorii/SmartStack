# Text analysis regression tests

The current corpus contains 901 functional checks: 781 sentence/configuration checks,
45 language-tokenizer/pipeline checks, and 75 punctuation checks. A separate allocation
project enforces seven allocation budgets and reports five additional scenarios.

These console projects test the production text analysis code without running Piper,
OpenVoice, native eSpeak, or audio synthesis. Expected sentence boundaries and punctuation
results are explicit fixtures. Generated cases check source preservation, Unicode boundaries,
progress, size limits, and completion flags with fixed seeds; they do not infer grammar.

## Run

From the project root with the .NET 10 SDK:

```powershell
dotnet run --project Tests/TextChunkerRegression/TextChunkerRegression.csproj -c Release -- --failures-only
dotnet run --project Tests/LanguageSegmentationRegression/LanguageSegmentationRegression.csproj -c Release -- --failures-only
dotnet run --project Tests/PunctuationRegression/PunctuationRegression.csproj -c Release -- --failures-only
dotnet run --project Tests/TextAnalysisAllocations/TextAnalysisAllocations.csproj -c Release -- --iterations=10000
```

Omit `--failures-only` to show individual passing cases. Every project exits with a
nonzero status on failure. `TextChunkerRegression` also accepts a category filter:

```powershell
dotnet run --project Tests/TextChunkerRegression/TextChunkerRegression.csproj -- --category="Ambiguous letter endings"
dotnet run --project Tests/TextChunkerRegression/TextChunkerRegression.csproj -- --category="Name binding boundaries"
```

## Coverage

| Project | Coverage |
| --- | --- |
| `TextChunkerRegression` | Abbreviations before names, references and examples; uppercase and lowercase initials; supplementary and decomposed Unicode letters; dotted initialisms; technical addresses; Unicode periods and script endings; quotations; ellipses; explicit line boundaries; completion flags; ordered lists; early and emergency cuts; optional configuration and concurrent catalog isolation. |
| `LanguageSegmentationRegression` | The tokenizer preserves the same abbreviation periods as the chunker, while retaining real sentence punctuation, references, technical file names, lexical hyphens, apostrophes and decomposed initials. Shared JSON rules are also checked through sentence splitting, tokenization, punctuation mapping and `UnifiedPhonemizer`, including inputs sent to the native bridge. Forced language routes make these checks deterministic. |
| `PunctuationRegression` | Shared punctuation semantics, Thai endings, independent Unicode fixtures, model-native symbols, compound question/exclamation order, quote removal, lexical connectors, optional additions, sparse model inventories, normalization idempotence, model control-token rejection, and unchanged string identity. |
| `TextAnalysisAllocations` | Warm synchronous allocation measurements for boundary classification, inherited language codes, punctuation, sentence chunking, emergency Unicode cuts, and forced-language tokenization. Five pure paths must allocate zero bytes; two whole-input chunking paths must remain within a 96-byte output budget. |

The name-binding follow-up adds 55 sentence checks and eight preprocessing-pipeline checks.
Hungarian `id.`, `ifj.`, and `özv.` stay attached to the following name across lowercase,
titlecase, uppercase, and four period forms. Counterexamples retain ordinary abbreviation
endings, the distinction between a single letter and a longer token, explicit newlines,
and end-of-input completion. A separate zero-byte budget checks name-binding classification.

The additional emergency matrix checks supplementary letters, combining sequences,
emoji modifiers, regional indicator pairs, joined emoji, punctuation/whitespace followed
by combining marks, and a grapheme larger than the limit. It matches output against the
source, allowing only boundary whitespace trimming and an inserted emergency hyphen.
Malformed UTF-16 must remain recoverable; valid UTF-16 must remain valid.

The JSON tests include comments, trailing commas, case-insensitive names, nullable lists,
ignored blank abbreviations, maximum lengths, invalid roots and entries, and the difference
between pause-only marks and clause marks during early and emergency splitting.

`RecordingEspeakWrapper.cs` is a test-only native-boundary spy. The production
`UnifiedPhonemizer` is linked unchanged; the spy records its text/voice calls and returns
the supplied core unchanged so punctuation and spacing remain observable. These tests
verify preprocessing, not IPA pronunciation or native eSpeak behavior.

The first and third projects use only the .NET runtime libraries. The language tokenizer
and allocation projects also restore the existing `SearchPioneer.Lingua` dependency.
The web project excludes all test sources, including the native-boundary spy, from application
build and publish output. Recorded measurements and methodology are in
[AllocationResults.md](AllocationResults.md). Passing counts are not line or branch coverage percentages.

## Deliberate limits

This is structural text analysis, not grammatical parsing. Cases such as `A. Smith`
and `Plan A. Tomorrow` can have the same visible structure. Uppercase initials are
kept together; an unknown lowercase single letter before a period can end a sentence.
Registered abbreviations retain their category-specific protection, and uncased
initials remain conservative. Georgian Mkhedruli is treated separately because
ordinary sentence starts do not use capitalization.

Explicit line boundaries resolve ambiguity. Lowercase personal initials may split;
uppercase letter names may stay attached to the following sentence. The corpus keeps
these expectations visible instead of disguising them as successful grammar analysis.

Ordered-list markers are presentation boundaries: `1. Open the report.` becomes a
completed `1.` chunk followed by the item. Early and emergency continuation chunks
remain unfinished. An indivisible technical address may exceed `MaxChunkLength`
rather than being corrupted or gaining an emergency glue hyphen. The final chunk retains
the sentence completion flag even when an oversized technical token consumes the remainder.
Every emergency cut respects complete text elements; whitespace carrying combining marks
is preserved rather than detached from those marks.
An ordinary word ending in punctuation remains subject to the size limit; a terminal
period or question mark by itself is not evidence of a technical token.

The optional file is tested when absent, containing an empty object, populated, malformed
or inconsistent. An existing zero-byte, whitespace-only, or `null` file is rejected explicitly.
Custom catalogs are immutable and do not modify the shared built-in default.
The experimental technical-symbol pronunciation and raw-phoneme parser tests are
outside this stage; they are not included in these verification totals.
