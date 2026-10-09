# Text analysis allocation measurements

Measured before and after the second text-analysis pass on .NET 10.0.0, SDK 10.0.100,
Linux x64 (Ubuntu 24.04.3), Release. Each scenario used 2,048 warmup calls followed by
10,000 measured synchronous calls. Inputs, delegates, catalogs, punctuation inventories,
and Lingua initialization were created before measurement.

The abbreviation follow-up repeated these measurements on the same runtime and added
a name-binding classification scenario. The existing byte counts remained unchanged.
The new scenario has no recorded before measurement.

`GC.GetAllocatedBytesForCurrentThread()` measures managed bytes allocated by these
calls. It does not report retained memory, native allocations, or complete synthesis cost.
The tokenizer scenarios force a language and bypass statistical inference. Logging uses
a null logger. Timing is reported by the runner for local investigation, but no latency
claim or timing threshold is derived from a shared-host run.

## Results

| Scenario | Before, bytes/call | After, bytes/call | Enforced budget |
| --- | ---: | ---: | ---: |
| Abbreviation period classification | 0 | 0 | 0 |
| Name-binding period classification | Not measured | 0 | 0 |
| Lowercase-letter period classification | 0 | 0 | 0 |
| Inherited forced-language code | 0 | 0 | 0 |
| Unchanged punctuation | 0 | 0 | 0 |
| Punctuation rewriting | 200 | 200 | Reported only |
| One complete plain sentence | 200 | 72 | 96 |
| One complete sentence with titles | 240 | 72 | 96 |
| Early split and multiple sentences | 296 | 296 | Reported only |
| Unicode emergency splitting | 600 | 600 | Reported only |
| Forced tokenizer, plain sentence | 448 | 448 | Reported only |
| Forced tokenizer, technical file name | 728 | 728 | Reported only |

The two whole-input sentence fixtures allocate 64% and 70% fewer bytes respectively.
`TextChunker` reuses the immutable input string when a chunk spans the entire input.
For an ordinary whole-input sentence, the result list reserves exactly one element;
multi-chunk requests keep the established growth behavior. The measured 72 bytes are
the returned list and its backing array, not a hidden temporary string.

Punctuation rewriting and tokenization still allocate output strings, token records,
lists, and string builders. Removing all of those would require an API or buffer-lifetime
change. No pooling, shared mutable buffers, object reuse, or new dependencies were added.
Catalog-loading allocations remain startup work and were not optimized as request cost.

## Run and regression behavior

```powershell
dotnet run --project Tests/TextAnalysisAllocations/TextAnalysisAllocations.csproj -c Release -- --iterations=10000
```

The runner reports every scenario and exits nonzero if any allocation budget fails.
The 96-byte budgets target the current .NET 10 result layout and reject a reintroduced
whole-input string copy or the default four-element list reservation for these fixtures.
They do not claim that chunking can return a mutable result list without allocating.
Compare measurements on the same runtime, architecture, configuration, and inputs.

Functional string-identity checks accompany the budgets. The new emergency tests also
prevent detached combining marks (including a cut moved before a technical token), lost
completion flags after oversized final tokens, and ordinary long words bypassing the size
limit because their terminal punctuation was mistaken for technical syntax.
