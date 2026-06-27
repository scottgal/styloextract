# Streaming gateway scanner

`Mostlylucid.StyloExtract.Streaming` is a zero-allocation, bounded-memory
fence scanner for the gateway position. It rides alongside the byte stream
of an HTTP response and emits a verdict — `Captured` / `Bailout` /
`NoTemplate` / `Continue` — while the body is still in flight, before the
full extraction pipeline ever has to decide whether to buffer the page.

This guide assumes you already know StyloExtract's `ILayoutExtractor` and
template-induction story. Streaming is a complementary pillar: it does
**not** produce Markdown, build a DOM, or replace `LayoutExtractor`. It
answers a different question — *"is this byte stream worth buffering at
all?"* — at a fraction of the memory and latency cost.

Cross-references to the release-notes entries that introduced each piece:
[alpha.16](../RELEASE_NOTES.txt) (package + scanner),
[alpha.17](../RELEASE_NOTES.txt) (host-keyed templates + auto-induction),
[alpha.18](../RELEASE_NOTES.txt) (incremental tokenizer + refit/versioning),
[alpha.19](../RELEASE_NOTES.txt) (true sliding-window memory contract).

---

## 1. Why streaming

Picture an HTTP reverse proxy, CDN edge, or output-filter middleware that
fronts a content site. For each response it wants to decide: **buffer
this fully and feed it to `LayoutExtractor` for RAG-quality markdown** or
**pass it through untouched** (it's a redirect, an API JSON blob, a
cache hint, a non-HTML asset, or a known-noisy template). Buffering every
byte of every response just to make that choice is wasteful — most pages
on most sites aren't worth extracting, and the ones that aren't worth
extracting are usually the largest.

Streaming gives you that verdict from the bytes themselves, as they
arrive. Concrete numbers from the lucidview FULL `--shot` smoke against
`https://www.mostlylucid.net`:

```
fetch Http+Captured+peak16473B/199506B · 227ms
```

The scanner reached a `Captured` verdict while holding **16,473 bytes**
of in-flight buffer against a **199,506-byte** response — roughly 8% of
the response was ever resident. The 16 KiB headroom is bounded by the
HttpClient chunk size, not by the response size; a 5 MiB response would
hold the same ~16 KiB peak (regression-pinned by
[`StreamingMemoryBoundTests`](../tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs)).

So: 8% memory footprint, sub-millisecond per-tag verdict, no DOM, no
allocations on the hot path once the template is warm.

---

## 2. Architecture

Six pieces, each with one job:

- **`MinimalHtmlTokenizer`** — zero-allocation, span-based tag tokenizer
  for whole-buffer scans. Yields `TagEvent { TagNameHash, ClassHash,
  ByteLength, IsClose }`. No DOM, no attribute parsing beyond `class=`.
- **`IncrementalHtmlTokenizer`** — stateful, fed chunk-by-chunk via
  `Feed(ReadOnlySpan<byte>)`. Holds only partial-tag bytes between calls
  (compact-on-emit, not compact-on-next-feed). Exposes
  `PeakBufferedBytes` and `BytesConsumed` for telemetry.
- **`FenceScanner`** — `ref struct`, the hot path. Maintains a fixed-size
  sliding window of `EventSlot`s + a rolling `RollingSketch` MinHash
  signature. On each `Tick(TagEvent)` returns `ScanVerdict { Continue,
  Captured, Bailout }`.
- **`IncrementalFenceScanner`** — class-shape wrapper that pairs
  `IncrementalHtmlTokenizer` with the scanner logic over heap-backed
  arrays (since `FenceScanner` is a ref struct and can't live as a class
  field or survive an `await`). Same `Tick` logic, hard-pinned to the
  ref-struct path by cross-validation tests.
- **`StreamingPathSelector`** — DI-injected entry point.
  - `Scan(templateId, bytes)` — synchronous whole-buffer scan against a
    specific template id (hot-cache only).
  - `ScanByHost(host, bytes)` — synchronous host-keyed lookup +
    whole-buffer scan.
  - `WarmAsync(templateId)` / `WarmByHostAsync(host)` — bring a template
    into the hot cache from durable storage.
- **`IStreamingTemplateStore`** — pluggable store.
  `InMemoryStreamingTemplateStore` (single-process, ConcurrentDictionary)
  or `SqliteStreamingTemplateStore` (durable, separate table from the
  layout-extractor `ITemplateIndex`).

The persisted record:

- **`StreamingTemplate`** — `{ TemplateId, Host, PrefixFence,
  ContentStartFence, ContentEndFence, MinContentDepth, BailoutBytes,
  MaxCaptureBytes, WindowSize, MaxEventsWithoutTransition, Version }`.

The first-pass auto-inducer:

- **`StreamingTemplateInducer`** — single tokenizer pass over the bytes,
  picks semantic-marker fences (`<header>…</header>`, paragraph cluster
  `<p>…</p><p>…</p>`, `<footer>` / `</main>` / `</article>` / `</body>`),
  produces a ready-to-upsert `StreamingTemplate`. Returns `null` when no
  plausible structural fences are found.

Per-host refit / versioning:

- **`StreamingRefitOrchestrator`** — observes captured-scan capture
  ranges, EWMA-tracks the typical length, and fires an off-hot-path
  re-induction when either the EWMA drift exceeds 30% on N consecutive
  scans (default 3) **or** every 10th captured scan re-induces "just to
  check" and replaces the template if the freshly-induced fences differ.
  Version bumps on replacement; `IStreamingTemplateVersionSink` fires a
  `StreamingTemplateRefitEvent`.

Data flow:

```
chunks ─▶ IncrementalHtmlTokenizer ─▶ TagEvent ─▶ IncrementalFenceScanner
                  │                                    │
                  └─ partial-tag bytes only            ├─ sliding window (last N events)
                     (compact-on-emit)                 ├─ MinHash RollingSketch
                                                       └─ Tick → ScanVerdict
```

---

## 3. The auto-induction lifecycle

This is the dogfood story — how a fresh deployment with an empty store
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
    // (Optional) record drift telemetry — fires refit asynchronously.
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
// In-memory store (default — no persistence)
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

## 4. Bounded memory — the headline property

The alpha.19 refactor closed the only "bounded but big" gap in the
streaming pipeline. The contract now:

- **Byte buffer.** Only partial-tag bytes are retained. The moment
  `IncrementalHtmlTokenizer.TryReadTag` emits a `TagEvent`, the bytes
  that produced it are dropped (compact-on-emit). The hard cap on the
  internal buffer is **64 KiB** (`IncrementalHtmlTokenizer.MaxBufferSize`),
  positioned as a safety stop — under correct input the buffer's
  valid-byte count post-emit is `O(longest tag)`, so the cap should
  never fire. When it does, `Feed` throws `InvalidOperationException`
  rather than silently dropping bytes.
- **Event window.** Fixed-size sliding ring of the last `WindowSize`
  events (default 8). Push new, pop oldest. No growth.
- **MinHash signature.** Fixed at `RollingSketch.SignatureSize`
  `uint`s per scanner (currently 128 → 512 bytes of stack-allocated
  signature on the ref-struct path, the same arity heap-allocated on
  the incremental path). No growth.

`PeakBufferedBytes` exposes the in-flight buffer high-watermark for
telemetry: a large gap between `BytesConsumed` (monotonic, tracks every
byte the scanner has seen) and `PeakBufferedBytes` is the proof the
scan held bounded memory.

The regression test that pins this:
[`StreamingMemoryBoundTests`](../tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs)
feeds **5 MiB** of synthetic HTML in 4 KiB chunks and asserts
`PeakBufferedBytes < 16 KiB`. That's a 320× consumed-vs-resident ratio,
sustained.

### Honest framing — bounded, not "tiny-constant"

Two things to be straight about:

1. **Bounded by HttpClient chunk size, not "hundreds of bytes."**
   In production, the 4 KiB lower bound in the synthetic test
   corresponds to `HttpResponseStream`'s natural chunk size; real
   responses give you 8–16 KiB chunks, which is exactly what the
   lucidview smoke (`peak16473B`) shows. "Bounded" here means
   `O(chunk + longest tag)`, not `O(1)`.
2. **MinHash sketch isn't reversibly rollable.** Min-pool MinHash
   doesn't support subtraction — when an event leaves the window, its
   contribution to `min(...)` can't be removed in O(1). The sketch
   therefore **rebuilds** from the current `WindowSize` events after
   each accepted push (`O(WindowSize × SignatureSize)` per accepted
   tag, gated by the per-template Bloom allowlist so the vast majority
   of inbound tags skip the recompute entirely). The bounded-buffer
   property — the user's actual concern — is satisfied by the
   tokenizer; the sketch's per-tick recompute is the price MinHash
   charges for the LSH-band locality the matcher relies on.

The framing the team uses internally: *bounded-not-tiny*. The streaming
gateway holds tens of kilobytes against arbitrarily large responses;
calling it "constant memory" would be technically wrong and would
mislead the operator when they see `peak16473B` in their telemetry.

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

Refits run on `Task.Run` — fire-and-forget — so the synchronous hot
path is never blocked. Exceptions are swallowed (v1 logs to
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
| RAG content extraction with full Markdown | `ILayoutExtractor` |
| Per-block role classification + profile filtering | `ILayoutExtractor` (Sitemap / RagFull / MainContentOnly profiles) |
| "Is this response worth buffering at all" gateway gate | Streaming scan |
| Header → content → footer detection without DOM parse | Streaming scan |
| Sub-millisecond verdict at ~8% memory footprint | Streaming scan |
| Per-host template learning + drift / refit | Both — `ILayoutExtractor` learns layout templates for extraction; streaming learns fence templates for the gateway gate |
| Site-template-version monitoring | Both — `TemplateVersionDiff` events from the layout side, `StreamingTemplateRefitEvent` from the streaming side |

The two pipelines are independent. You can run streaming alone (gateway
filter, no extraction), `LayoutExtractor` alone (background batch
crawler, no streaming gate), or both together (gateway streaming
verdict decides whether to fan-out to the extractor).

---

## 7. Limitations and follow-ups

Calling these out honestly so operators aren't surprised:

- **Bounded by HttpClient chunk size, not "a handful of bytes."**
  Measured: 16,473 B peak against 199,506 B response (~8%). For a 4 KiB
  chunk synthetic feed, peak stays under 16 KiB across 5 MiB consumed.
  Bounded-not-tiny; that's the headline you should quote.
- **MinHash sketch isn't CRC-rolled.** Per-tick `O(WindowSize ×
  SignatureSize)` recompute over the current event window. The
  per-template Bloom allowlist (`TagAllowlistBloom`) skips the
  recompute for inbound tags that can't possibly affect any fence —
  on real pages this is the vast majority. True XOR-rolling
  isn't possible for min-pool MinHash; we'd have to swap to a
  CountSketch / SimHash variant to get it.
- **`IncrementalFenceScanner` duplicates `FenceScanner.Tick` logic.**
  `FenceScanner` is a `ref struct` so its sketch state can be span-backed
  by the call-site stack; that shape can't survive an `await` or live as
  a class field. `IncrementalFenceScanner` therefore re-implements the
  same tick logic over heap arrays. Cross-validation tests
  ([`IncrementalFenceScannerTests`](../tests/StyloExtract.Streaming.Tests/))
  pin the two implementations in lockstep — any drift is a correctness
  bug, but the code-duplication itself is intentional.
- **One template per host; no formal version chain on streaming yet.**
  Alpha.18 added drift detection and version-bump-on-refit, but the
  store keeps only the latest template per host. There is no
  `GetByHostAtVersion(host, version)` API — `Version` is monotonic
  metadata, not a query dimension. If you need rollback or A/B template
  comparison, implement it in your own version sink.
- **Auto-induction is heuristic.** `StreamingTemplateInducer` picks
  semantic-marker fences (`<header>`, `<footer>`, `<article>`,
  paragraph clusters). Pages without semantic markup — a `<div>`-soup
  hand-rolled landing page — get `null` from the inducer and stay
  `NoTemplate` forever for that host. The LLM-induced layout templates
  in `StyloExtract.Llm.Ollama` / `StyloExtract.Llm.LlamaSharp` are
  smarter but don't currently emit streaming-template fences (open
  follow-up: derive fence templates from an LLM-induced layout
  template's selector hints).

---

## See also

- [`README.md`](../README.md) — top-level project overview.
- [`RELEASE_NOTES.txt`](../RELEASE_NOTES.txt) — alpha.16 → alpha.19
  entries cover the full streaming evolution.
- [`src/StyloExtract.Streaming/`](../src/StyloExtract.Streaming/) — source.
- [`tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs`](../tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs)
  — the bounded-memory regression guard.
