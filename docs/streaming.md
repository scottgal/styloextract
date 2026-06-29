# Streaming gateway scanner

`Mostlylucid.StyloExtract.Streaming` is a zero-allocation, bounded-memory
afence scanner that finds the content region of an HTML response **on the
wire**, before the body is fully buffered or any DOM is built. It rides
alongside the byte stream and emits a verdict (`Captured` / `Bailout` /
`NoTemplate` / `Continue`) plus, on `Captured`, the byte offsets
(`CaptureStartByte`, `CaptureEndByte`) of the content region.

The scanner's job is content-boundary detection on the byte stream. What
you do with the captured byte range branches by use case:

- **AI / RAG / embeddings / chunking.** Take the bytes from
  `CaptureStartByte` to `CaptureEndByte` and feed them straight to your
  chunker / embedder / LLM. No DOM, no markdown conversion needed; the
  capture range IS the content. This is the cheapest path: the response
  isn't fully buffered, no AngleSharp parse runs, no
  `LayoutExtractor.ExtractAsync` runs.
- **Structured human-readable markdown.** Hand the captured bytes (or
  the whole response, if your fetcher already buffered it) to
  `LayoutExtractor.ExtractAsync` for headings / lists / tables / code
  fences preserved. This is the path the lucidVIEW reader takes; it's
  what you want when a person is going to read the output.
- **Gateway pass-through.** If the verdict is `Bailout` or `NoTemplate`
  early in the stream, decide on the spot whether to keep buffering
  (so the slow-path inducer can later write a template) or pass the
  response through untouched (it's a redirect / JSON / non-HTML / a
  page you've already classified as not worth extracting).

`LayoutExtractor` is for **structured markdown**, not for "AI use." The
scanner alone is enough for AI use because the byte range it captures
is the content.

Cross-references to the release-notes entries that introduced each piece:
[alpha.16](../RELEASE_NOTES.txt) (package + scanner),
[alpha.17](../RELEASE_NOTES.txt) (host-keyed templates + auto-induction),
[alpha.18](../RELEASE_NOTES.txt) (incremental tokenizer + refit/versioning),
[alpha.19](../RELEASE_NOTES.txt) (true sliding-window memory contract),
[alpha.21](../RELEASE_NOTES.txt) (partial-tag-only buffer, Markov shingles,
structural-tag filter, depth-aware capture, shared Tick, version chain),
[alpha.23](../RELEASE_NOTES.txt) (structural-only depth tracking,
bytes-since-state-change bailout, Flush latches Continue→Bailout at EOF),
[alpha.24](../RELEASE_NOTES.txt) (Task 4 of Phase 1, tripwire scanner
replaces MinHash fences with `IdentityClaim`-based matching shared with
the layout extractor),
[2.0.0](../RELEASE_NOTES.txt) (Task 13 of Phase 1: byte-pattern matcher
replaces the tripwire scanner. No tokenizer on the hot path; matching
runs directly on response bytes).

**Matcher algorithm (2.0.0+).** The scanner walks the response bytes
directly. A `StreamingTemplate` carries three `BytePattern`s: `PrefixPattern`,
`ContentStartPattern`, `ContentEndPattern`. The FSM transitions
AwaitPrefix to AwaitContentStart on `PrefixPattern` match,
AwaitContentStart to Capturing on `ContentStartPattern` match (capture
start snapshot taken), and Capturing to Captured on `ContentEndPattern`
match (with a nested-open counter so an inline same-name element inside
the captured region doesn't close it early). The matcher skips
`<!-- ... -->`, `<script>...</script>`, `<style>...</style>`, and
`<![CDATA[ ... ]]>` regions because they can carry tag-shaped text
that isn't structural HTML. No tokenizer on the hot path, no per-tag
identity-claim evaluation, no DOM depth tracker.

Two prior shapes lived in this slot. alpha.21 used MinHash with LSH bands
over a sliding window of structural-tag events. alpha.24 (Task 4) swapped
that for `IdentityClaim` tripwires evaluated against per-event hash data
the tokenizer carried. Task 13 (shipped in 2.0.0) swapped THAT for direct
byte-pattern matching on the response bytes. Each replacement traded a
mechanism, not the purpose: the scanner identifies where content starts
and ends in the stream. Identity claims still drive the layout-side
applicator; they're no longer how the streaming scanner detects fences.

---

## 1. Why streaming

The scanner answers two questions on the wire:

1. **Where does the content region of this response start and end** (in
   byte offsets)?
2. **Is this stream worth buffering at all** (or is it a redirect, an
   API blob, a non-HTML asset, an anti-bot page)?

Both answers arrive while the body is still in flight. Neither requires
buffering the full response or building a DOM. For AI / RAG / chunking
pipelines, answer 1 is everything you need: the captured byte range is
the content. For human-readable structured markdown, hand the captured
range (or the whole response) to `LayoutExtractor`. For gateway
pass-through, latch on answer 2.

Buffering the whole response just to find the content region is
wasteful. Most pages on most sites aren't worth extracting, the ones
that aren't are usually the largest (script-heavy SPA shells, banner-ad
forests, infinite-scroll feeds), and even on the pages worth extracting
the content region is typically a small fraction of the byte total
(article body inside a chrome-heavy template).

alpha.21 tightened the buffer contract: `PeakBufferedBytes` measures
ONLY the longest single tag that straddles a chunk boundary, typically
low-hundreds-of-bytes, often zero, regardless of chunk size or response
size. Measured against a synthetic 200 KB page fed in 16 KB chunks:
peak = 0 B (no boundary landed mid-tag). Fed in 1 KB chunks: peak = 19 B.
The configurable ceiling is `StreamingTokenizerOptions.MaxPartialTagBytes`
(1 MiB default), and the buffer itself is rented from
`ArrayPool<byte>.Shared`, so growth doesn't churn the GC, and the
ceiling is the operator's sanity stop on truly hostile input rather
than an arbitrary developer-picked number. Under correct input the
buffer's residency is bounded by `O(longest tag)`, not `O(chunk size)`.
Pinned by
[`StreamingMemoryBoundTests`](../tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs).

So: low-hundreds-of-bytes memory footprint against arbitrarily large
responses, sub-millisecond per-tag verdict, no DOM, no allocations on
the hot path once the template is warm.

---

## 2. Architecture

The hot path runs on bytes, not tokens. The tokenizer is still in the
project, used by the inducer and held in reserve for callers that want
TagEvents, but the scanner itself works directly on the response bytes.

Hot-path pieces:

- **`BytePatternScanner`** (`ref struct`, whole-buffer). Drives
  `ScannerCore.Step` over a `ReadOnlySpan<byte>` in one shot. Used by
  `StreamingPathSelector` for synchronous calls where the response is
  already in memory. Stack-allocated state, no heap touches.
- **`IncrementalBytePatternScanner`** (class, chunked, `IDisposable`).
  Same FSM as `BytePatternScanner` but holds carry-over state across
  `Feed(chunk)` calls. Carries a small carry buffer rented from
  `ArrayPool<byte>.Shared` for the trailing fragment of a chunk that
  couldn't be consumed in isolation (a partial open tag, a straddled
  close marker). `Dispose()` returns the rented buffer; call it (or
  wrap in `using`) on long-lived scanners.
- **`ScannerCore`** (internal, pure-static). The FSM both scanner
  shells share. States: AwaitPrefix, AwaitContentStart, Capturing,
  Captured, Bailed. Skips `<!-- ... -->`, `<script>...</script>`,
  `<style>...</style>`, and `<![CDATA[ ... ]]>` regions on the way
  through, because those can carry tag-shaped bytes that aren't
  structural HTML. In Capturing, counts opens and closes of the
  content-start tag name so an inline same-name element inside the
  captured region doesn't terminate it early.

Supporting pieces:

- **`StreamingPathSelector`** (DI-injected entry point).
  - `Scan(templateId, bytes)`: whole-buffer scan against a specific
    template id (hot-cache only).
  - `ScanByHost(host, bytes)`: host-keyed lookup + whole-buffer scan.
  - `WarmAsync(templateId)` / `WarmByHostAsync(host)`: bring a template
    into the hot cache from durable storage.
- **`IStreamingTemplateStore`** (pluggable store).
  `InMemoryStreamingTemplateStore` (single-process, `ConcurrentDictionary`)
  or `SqliteStreamingTemplateStore` (durable, separate table from the
  layout-extractor `ITemplateIndex`).
- **`MinimalHtmlTokenizer`** and **`IncrementalHtmlTokenizer`**. Span-
  based tokenizers, both still in the project. The inducer uses
  `MinimalHtmlTokenizer` to walk a freshly-fetched page once and pick
  the byte patterns to bake into a `StreamingTemplate`. The
  `IncrementalHtmlTokenizer` is the chunked counterpart for callers
  that want a stateful TagEvent stream without the inducer. Neither
  drives the scanner FSM today; that runs on bytes via `ScannerCore`.

The persisted record:

- **`StreamingTemplate`**: `{ TemplateId, Host, PrefixPattern,
  ContentStartPattern, ContentEndPattern, BailoutBytes, MaxCaptureBytes,
  Version }`. Three `BytePattern` records hand-anchored at induction
  time. `BytePattern` is `{ LeftAnchor, RightAnchor, MaxScanBytes }`
  where LeftAnchor and RightAnchor are short byte sequences from the
  target tag (typically the literal `<article` and a class-attr
  fragment, or a stable `data-` attribute). Persisted via
  `SqliteStreamingTemplateStore` under a versioned schema; the
  `PRAGMA user_version` gate drops stale rows when the persisted
  shape changes between major versions, so dogfood DBs self-heal
  on first open.

The first-pass auto-inducer:

- **`StreamingTemplateInducer`**: parses the page once with AngleSharp,
  picks three target elements (prefix, content-start, content-end)
  using the semantic-marker shape heuristic (`<header>` or `<nav>` for
  prefix, `<article>` or `<main>` or paragraph-cluster parent for
  content), and emits a `BytePattern` per target keyed on bytes
  visible in the original response (stable id, stable class fragment,
  or stable `data-` attribute). Returns `null` when no plausible
  targets are found.

Per-host refit / versioning:

- **`StreamingRefitOrchestrator`**: observes captured-scan capture
  ranges, EWMA-tracks the typical length, and fires an off-hot-path
  re-induction when either the EWMA drift exceeds 30% on N consecutive
  scans (default 3) or every 10th captured scan re-induces "just to
  check" and replaces the template if the freshly-induced patterns
  differ. Version bumps on replacement;
  `IStreamingTemplateVersionSink` fires a
  `StreamingTemplateRefitEvent`.

Data flow:

```
chunks ─▶ IncrementalBytePatternScanner ─▶ ScannerCore.Step ─▶ ScanVerdict
                  │                              │
                  └─ ArrayPool-rented carry      ├─ FSM state + capture range
                     buffer (only the trailing   ├─ BytePattern matches against bytes
                     fragment that can't be      └─ skip-regions for comments / script
                     consumed in isolation)         / style / CDATA
```

---

## 3. The auto-induction lifecycle

This is the dogfood story: how a fresh deployment with an empty store
grows a per-host template library on its own, without any operator
configuration.

Step-by-step:

1. **First visit to a host.** `ScanByHost(host, bytes)` looks up the hot
   cache, finds nothing, returns `ScanVerdict.NoTemplate`.
2. **Hot-path consumer hands the bytes to the inducer.**
   `StreamingTemplateInducer.Induce(host, bytes)` walks the HTML once
   via `MinimalHtmlTokenizer`, picks semantic-marker fences, returns a
   `StreamingTemplate` (or `null` on pages with no plausible structure,
   in which case the host stays a permanent `NoTemplate` miss until
   something useful arrives).
3. **Upsert.** `store.UpsertAsync(template)` writes through to durable
   storage and the hot cache, keyed by both `TemplateId` and `Host`.
4. **Second visit.** `WarmByHostAsync(host)` pulls from durable into hot
   (if the process restarted); `ScanByHost(host, bytes)` now returns
   `Captured` (or `Bailout` if the page genuinely doesn't fit the
   inducer's fences).
5. **Drift detection (alpha.18).** After each captured scan, the
   consumer optionally feeds the capture range +
   `RecordCaptured(host, captureStart, captureEnd, latestBytes)` to
   `StreamingRefitOrchestrator`. Drift / cadence rules above kick the
   re-induction asynchronously on a background task; the hot path is
   never blocked.

End-to-end code:

```csharp
var selector  = sp.GetRequiredService<StreamingPathSelector>();
var inducer   = sp.GetRequiredService<StreamingTemplateInducer>();
var store     = sp.GetRequiredService<IStreamingTemplateStore>();
var refit     = sp.GetRequiredService<StreamingRefitOrchestrator>();

// Cold path: warm from durable, scan, induce on miss, retry-on-next-visit.
await selector.WarmByHostAsync(host);
var verdict = selector.ScanByHost(host, html);

if (verdict == ScanVerdict.NoTemplate)
{
    var induced = inducer.Induce(host, html);
    if (induced is not null)
        await store.UpsertAsync(induced);
    // Next request for this host will hit the warmed template.
}
else if (verdict == ScanVerdict.Captured)
{
    // (Optional) record drift telemetry; fires refit asynchronously.
    refit.RecordCaptured(host,
        captureStartByte: /* from scanner */ 0,
        captureEndByte:   /* from scanner */ html.Length,
        latestBytes:      html);
}
```

The selector and store are thread-safe and DI-friendly singletons. The
inducer is stateless. The refit orchestrator holds per-host EWMA state
under a `ConcurrentDictionary` and is safe to share.

DI wire-up is one call (alpha.20+):

```csharp
// In-memory store (default, no persistence)
services.AddStyloExtractStreaming();

// SQLite store with persistence path
services.AddStyloExtractStreaming(o =>
{
    o.SqlitePath = Path.Combine(AppPaths.LocalState, "streaming-templates.db");
});
```

`AddStyloExtractStreaming` registers `IStreamingTemplateStore` (Sqlite
or InMemory based on `StreamingOptions.SqlitePath`),
`StreamingPathSelector`, `StreamingTemplateInducer`, and
`StreamingRefitOrchestrator`. All use `TryAddSingleton` so a
consumer-supplied `IStreamingTemplateVersionSink` registered before or
after wins over the default no-op sink:

```csharp
services.AddSingleton<IStreamingTemplateVersionSink, MyTelemetrySink>();
services.AddStyloExtractStreaming();
```

The orchestrator's drift / cadence knobs also surface through
`StreamingOptions` (`RelativeDriftThreshold`, `DriftBailoutCount`,
`ScansPerForcedRefit`) for consumers who want to tune the refit cadence.

**Advanced / low-level form.** If you need full control of the store
construction or want to register each piece individually, the manual
form still works:

```csharp
services.AddSingleton<IStreamingTemplateStore, InMemoryStreamingTemplateStore>();
// or: services.AddSingleton<IStreamingTemplateStore>(_ =>
//         new SqliteStreamingTemplateStore("Data Source=streaming.db"));
services.AddSingleton<StreamingPathSelector>();
services.AddSingleton<StreamingTemplateInducer>();
services.AddSingleton<StreamingRefitOrchestrator>();
```

---

## 4. Bounded memory, the headline property

The 2.0.0 byte-pattern model preserves the alpha.21 buffer contract and
collapses the per-tick scratch state. The contract now:

- **Byte buffer.** Only the trailing fragment of a chunk that can't be
  consumed in isolation is retained between `Feed` calls. Each chunk
  is scanned in place; consumed bytes are released immediately and the
  chunk span is never copied wholesale into the buffer. The buffer is
  rented from `ArrayPool<byte>.Shared` and doubles on demand up to the
  configurable ceiling `StreamingTokenizerOptions.MaxCarryBufferBytes`
  for the byte-pattern scanner (and `MaxPartialTagBytes` for the
  tokenizer, when a caller uses it). Default ceiling is 1 MiB on
  both. Under correct input the buffer's residency is `O(longest
  partial tag)` (typically &lt;500 B, often zero when chunk boundaries
  land in text). The ceiling exists only to fail fast on truly hostile
  input; above it, `Feed` throws `InvalidOperationException` rather
  than silently dropping bytes.
- **Scanner state.** A `ScannerCore.State` value type (FSM state,
  byte counters, capture range, nested-open counter). No sliding
  window, no signature buffer, no TagEvent queue, since the scanner
  walks bytes directly.
- **Per-event hash data** (tokenizer side, only when callers use it).
  Bounded at parse time via `TagAttrLimits` (defaults: 32 class
  hashes, 16 data-* and 16 aria-* pairs each, configurable up to
  256/128 via `StreamingTokenizerOptions`). The buffers are
  stackalloc-sized at the configured limit. Real pages well within
  the defaults; bump if a host ships &gt;32 utility classes on a single
  element and you need every one to flow into identity-claim matching
  on the layout side.

`PeakBufferedBytes` exposes the in-flight buffer high-watermark for
telemetry: a large gap between `BytesConsumed` (monotonic, tracks every
byte the scanner has seen) and `PeakBufferedBytes` is the proof the
scan held bounded memory.

The regression test that pins this:
[`StreamingMemoryBoundTests`](../tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs)
feeds **5 MiB** of synthetic HTML in 4 KiB chunks and asserts
`PeakBufferedBytes < tok.MaxPartialTagBytes` (the configured ceiling).
In practice the measured value is in the low hundreds of bytes.

### What "bounded" actually means here

1. **Bounded by longest partial tag at a chunk boundary, not chunk size.**
   alpha.21 changed the parse loop so chunks are scanned in-place
   (no wholesale copy into the buffer). Only the bytes of a tag that's
   mid-parse when the chunk runs out get retained. Measured peaks:
   0 B for 16 KB chunks over a 200 KB body, 19 B for 1 KB chunks.
   "Bounded" here is `O(longest tag)`, not `O(chunk + longest tag)`
   as in alpha.19.
2. **Byte-pattern matching is O(pattern length) per match attempt.**
   The 2.0.0 scanner walks the response bytes directly and tries the
   active pattern at every `<` it encounters (after the skip-region
   filter). Each match attempt is a literal byte-equality check
   against the pattern's anchors; no hash recompute, no event stream,
   no sliding window. The earlier alpha.21..23 pipeline ran MinHash +
   LSH over a sliding window of structural events at
   `O(WindowSize x SignatureSize)` per accepted structural tag;
   alpha.24 dropped that to `O(claim size)` per tag; 2.0.0 dropped
   the tokenizer from the hot path entirely.

The streaming gateway holds at most a few hundred bytes against
arbitrarily large responses; in many realistic chunk-alignments the
buffer is literally empty between `Feed` calls.

---

## 5. Refit + versioning

Alpha.18 added per-host drift tracking to mirror the layout-extractor's
`RefitOrchestrator`. After every `ScanVerdict.Captured`, the consumer
can call `StreamingRefitOrchestrator.RecordCaptured(...)`. Two signals
are tracked per host:

- **Capture-range EWMA.** A smoothed mean of `captureEnd - captureStart`.
  When the new captured length differs from the EWMA by more than
  `RelativeDriftThreshold` (default 30%) on
  `DriftBailoutCount` consecutive scans (default 3), a refit fires.
- **Cadence.** Every `ScansPerForcedRefit` captured scans (default 10),
  the inducer is re-run "just to check." If the freshly-induced fences
  differ from the current template's, a refit fires; otherwise no-op.

A refit:

1. Re-induces the template from the latest bytes.
2. If different from the current template, **bumps `Version`**
   (`current.Version + 1`), assigns a new `TemplateId`, upserts.
3. Fires `IStreamingTemplateVersionSink.OnRefittedAsync(StreamingTemplateRefitEvent)`
   carrying `{ Host, OldTemplateId, NewTemplateId, OldVersion,
   NewVersion, Reason ("drift" | "cadence"), DetectedAt }`.

Refits run on `Task.Run`, fire-and-forget, so the synchronous hot path
is never blocked. Exceptions are swallowed (v1 logs to
`Console.WriteLine`; wire to `ILogger` in your version sink if you
need observability).

The default `IStreamingTemplateVersionSink` is a no-op. Implement and
register your own for telemetry, alerting, or downstream consumers
(e.g. invalidating a downstream cache when a host's streaming template
shape changes).

---

## 6. When to use streaming vs LayoutExtractor

| Use case | Choose |
|---|---|
| AI / RAG / embeddings / chunking, raw text from the content region | Streaming scan, use the captured byte range directly |
| Structured human-readable Markdown (headings, lists, tables, code fences preserved) | `ILayoutExtractor` |
| Per-block role classification + profile filtering | `ILayoutExtractor` (Sitemap / RagFull / MainContentOnly profiles) |
| "Is this response worth buffering at all" gateway gate | Streaming scan |
| Content-region boundary detection on the byte stream | Streaming scan |
| Sub-millisecond verdict at ~8% memory footprint | Streaming scan |
| Per-host template learning + drift / refit | Both. `ILayoutExtractor` learns layout templates for extraction; streaming learns byte-pattern templates for the gateway gate |
| Site-template-version monitoring | Both. `TemplateVersionDiff` events from the layout side, `StreamingTemplateRefitEvent` from the streaming side |

The two pipelines are independent. You can run streaming alone (gateway
filter, no extraction), `LayoutExtractor` alone (background batch
crawler, no streaming gate), or both together (gateway streaming
verdict decides whether to fan-out to the extractor).

---

## 7. Version chain

alpha.21 made the streaming-template store version-chain-aware. Each
host can hold multiple template versions (1, 2, 3, ...). `UpsertAsync`
APPENDS a new (host, version) row rather than replacing the previous
template. `StreamingRefitOrchestrator` bumps `Version` on every refit
and calls `UpsertAsync`, so the version chain grows over time.

Query surface on `IStreamingTemplateStore`:

- `GetByHostAsync(host)` → latest version (hot path).
- `GetByHostAtVersionAsync(host, version)` → specific version
  (rollback / A/B / audit).
- `ListVersionsByHostAsync(host)` → ascending list of all known
  versions for a host.

The SQLite store's primary key is `(host, version)`. The schema migration
on first open of a pre-alpha.21 DB auto-renames the old table, recreates
the new shape, and copies rows into version 1 (existing single-template-
per-host rows become the v1 baseline).

The store also tracks a `PRAGMA user_version` for algorithm-compat. When
the scanner-side scoring rules change, bumping
`SqliteStreamingTemplateStore.CurrentStoreVersion` causes existing DBs to
drop their now-incompatible templates on next open, forcing clean
re-induction. Alpha.22 introduced the constant at value `2`; alpha.24
bumped to `3` for the tripwire rewrite since the serialised
`StreamingTemplate` shape changed outright (three `IdentityClaim`s
instead of three `TemplateFence`s). The schema migration runs first
(unchanged) and the `user_version` gate runs immediately after, both
inside the same connection.

## 8. Design choices and known limitations

Two genuine limitations remain, both algorithmic. The previous version of
this section listed four bullets about fixed buffer caps and per-event
data caps; those were arbitrary numbers that have since been replaced by
`StreamingTokenizerOptions` (1 MiB default carry-buffer ceiling,
`ArrayPool<byte>` rented growth, 32-class / 16-attr default per-event
limits, all configurable).

- **Byte-pattern matching is exact.** The scanner runs literal byte
  comparisons against the anchors baked into the template at induction
  time (the identity-aware inducer rejects hash-shaped class tokens
  via `DefaultClassStabilityFilter` before they ever reach a pattern).
  Pages whose chosen anchor bytes change between sessions, such as
  utility-class shuffles, hashed CSS-module names that drift, or JIT
  class-name churn, generate a clean miss then refit signal rather
  than a probabilistic fuzzy-match that hides the drift. This is a
  design choice, not a deficiency; the trade-off is no soft tolerance
  for legitimate small DOM diffs.
- **Auto-induction is heuristic.** `StreamingTemplateInducer` picks
  semantic-marker targets (`<header>`, `<article>`, `<main>`,
  paragraph-cluster parent). Pages without semantic markup, a
  `<div>`-soup hand-rolled landing page for example, get `null` from
  the inducer and stay `NoTemplate` until the layout-side LLM inducer
  runs and writes an operator-template the next visit can pick up.

### What was previously listed and has since been fixed

- The 16 KiB / 4 KiB hard `MaxBufferSize` consts on
  `IncrementalHtmlTokenizer` and `IncrementalBytePatternScanner` were
  arbitrary numbers that threw on legitimate JSON-LD or OpenGraph blobs.
  Replaced by `StreamingTokenizerOptions.MaxPartialTagBytes` /
  `MaxCarryBufferBytes` (1 MiB default, configurable, rented from
  `ArrayPool<byte>.Shared`).
- The 8-class / 3-attr-pair internal caps on `TagEvent` silently dropped
  the tail. Replaced by `TagAttrLimits` threaded from
  `StreamingTokenizerOptions` (32 / 16 defaults, validated at
  construction up to 256 / 128 stackalloc ceilings).

---

## See also

- [`README.md`](../README.md): top-level project overview.
- [`RELEASE_NOTES.txt`](../RELEASE_NOTES.txt): alpha.16 onward,
  covering the full streaming evolution.
- [`src/StyloExtract.Streaming/`](../src/StyloExtract.Streaming/): source.
- [`tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs`](../tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs):
  the bounded-memory regression guard.
