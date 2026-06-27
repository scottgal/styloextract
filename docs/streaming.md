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
[alpha.19](../RELEASE_NOTES.txt) (true sliding-window memory contract),
[alpha.21](../RELEASE_NOTES.txt) (partial-tag-only buffer, Markov shingles,
structural-tag filter, depth-aware capture, shared Tick, version chain),
[alpha.23](../RELEASE_NOTES.txt) (structural-only depth tracking,
bytes-since-state-change bailout, Flush latches Continue→Bailout at EOF),
[alpha.24](../RELEASE_NOTES.txt) (Task 4 of Phase 1 — tripwire scanner
replaces MinHash fences with `IdentityClaim`-based matching shared with
the layout extractor).

**Matcher algorithm (alpha.24+).** The scanner watches the tokenizer's
event stream and fires state transitions on EXACT `IdentityClaim` match
against the per-event hash data the tokenizer carries on each
`TagEvent` (tag-name hash + id hash + per-class hashes + data-* / aria-*
hash pairs + role hash). There is no MinHash sketch, no LSH bands, no
sliding event window — the FSM matches once per event in O(claim size).
The same `IdentityClaim` primitive drives the layout-extractor's
selector resolution, so streaming and layout share one identity-matching
contract instead of running on parallel algorithms.

Before alpha.24, the scanner ran MinHash with LSH bands over a sliding
window of structural-tag events — a probabilistic match that gave soft
tolerance across DOM diffs at the cost of unifying with the layout side.
alpha.24 traded that tolerance for exact match on stable-by-construction
identifiers (the identity-aware inducer picks stable claims). Drift now
shows up as a clean miss → refit signal, which is what we wanted anyway.

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
arrive. alpha.21 tightened the buffer contract: `PeakBufferedBytes` now
measures ONLY the longest single tag that straddles a chunk boundary —
typically **low-hundreds-of-bytes, often zero**, regardless of chunk
size or response size. Measured against a synthetic 200 KB page fed in
16 KB chunks: peak = **0 B** (no boundary landed mid-tag). Fed in 1 KB
chunks: peak = **19 B**. The hard cap is 4 KB
(`IncrementalHtmlTokenizer.MaxBufferSize`), repositioned as a safety
stop — under correct input the buffer's residency is bounded by
`O(longest tag)`, not `O(chunk size)`. Pinned by
[`StreamingMemoryBoundTests`](../tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs).

So: low-hundreds-of-bytes memory footprint against arbitrarily large
responses, sub-millisecond per-tag verdict, no DOM, no allocations on
the hot path once the template is warm.

---

## 2. Architecture

Six pieces, each with one job:

- **`MinimalHtmlTokenizer`** — span-based tag tokenizer for whole-buffer
  scans. Yields `TagEvent { TagNameHash, ClassHash, ClassHashes[],
  IdHash, RoleHash, DataAttrHashes[], AriaAttrHashes[], ByteLength,
  IsClose }`. alpha.24 extended the event to carry every identity-
  relevant hash so the tripwire matcher never has to look back at raw
  bytes; per-event attribute extraction is shared with
  `IncrementalHtmlTokenizer` via `TagAttributeParser`.
- **`IncrementalHtmlTokenizer`** — stateful, fed chunk-by-chunk via
  `Feed(ReadOnlySpan<byte>)`. Holds only partial-tag bytes between calls
  (compact-on-emit, not compact-on-next-feed). Exposes
  `PeakBufferedBytes` and `BytesConsumed` for telemetry.
- **`FenceScanner`** — `ref struct`, the hot path. alpha.24 dropped the
  sliding window + sketch storage; on each `Tick(TagEvent)` it evaluates
  the active tripwire (`PrefixTripwire` / `ContentStartTripwire` /
  `ContentEndTripwire`) via `IdentityClaimMatcher.MatchesByHash` and
  returns `ScanVerdict { Continue, Captured, Bailout }`.
- **`IncrementalFenceScanner`** — class-shape wrapper that pairs
  `IncrementalHtmlTokenizer` with the scanner logic over heap-backed
  fields (since `FenceScanner` is a ref struct and can't live as a class
  field or survive an `await`). Same `Tick` logic, hard-pinned to the
  ref-struct path by cross-validation tests. `Feed(chunk)` returns the
  current verdict; `Flush()` is the canonical end-of-stream call — when
  the stream exhausts without matching all tripwires, `Flush()` latches
  the terminal verdict to `Bailout` (alpha.23: previously dangled at
  `Continue`, which was meaningless at EOF).
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

- **`StreamingTemplate`** — `{ TemplateId, Host, PrefixTripwire,
  ContentStartTripwire, ContentEndTripwire, BailoutBytes, MaxCaptureBytes,
  Version }`. alpha.24 replaced the three MinHash `TemplateFence` records
  with three `IdentityClaim` tripwires and dropped the `WindowSize` and
  `MaxEventsWithoutTransition` fields (no sliding window in the tripwire
  model; bytes-based bailout supersedes event-counter bailout).
  `CurrentStoreVersion` bumped from 2 to 3 so alpha.21..23 dogfood DBs
  self-heal cleanly on first open with the new scanner.

The first-pass auto-inducer:

- **`StreamingTemplateInducer`** — parses the page once with AngleSharp,
  picks three target elements (prefix, content-start, content-end) using
  the same shape heuristic as alpha.16..23 (`<header>` / `<nav>` for
  prefix; `<article>` / `<main>` / paragraph-cluster parent for content),
  and calls `IdentityClaimExtractor.Extract` on each. The resulting
  claims are narrowed before being stamped as tripwires: id-only when an
  id is present, tag + first-two-classes otherwise. The ContentEnd
  tripwire collapses further to tag-only since close events carry no
  attributes. Returns `null` when no plausible targets are found.

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
                  └─ partial-tag bytes only            ├─ FSM state + capture depth
                     (compact-on-emit)                 ├─ IdentityClaimMatcher.MatchesByHash
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

The alpha.24 tripwire model preserves the alpha.21 buffer contract and
collapses the per-tick scratch state. The contract now:

- **Byte buffer.** Only the partial-tag bytes that straddle a chunk
  boundary are retained between `Feed` calls. Each chunk is parsed
  inline; complete-tag bytes are dropped immediately and the chunk
  span is never copied wholesale into the buffer. The hard cap is
  **4 KiB** (`IncrementalHtmlTokenizer.MaxBufferSize`) — under correct
  input the buffer's residency is `O(longest tag)` (typically &lt;500 B,
  often zero when chunk boundaries land in text). The cap is a safety
  stop; if a single tag is genuinely &gt; 4 KiB, `Feed` throws
  `InvalidOperationException` rather than silently dropping bytes.
- **Scanner state.** A small `StreamingTickState` value type
  (FSM state, depth counters, byte counters). No sliding window,
  no MinHash signature — the tripwire matcher consumes the per-event
  hash data the tokenizer already carries on each `TagEvent`.
- **Per-event hash data.** Bounded at parse time:
  `TagEvent.MaxClassesPerEvent = 8` class hashes,
  `TagEvent.MaxAttrPairsPerEvent = 3` data-* and aria-* pairs each.
  Pages with more attributes simply lose the tail; identity claims
  that depend on a tail-class won't fire and the scanner falls through
  to Bailout cleanly.

`PeakBufferedBytes` exposes the in-flight buffer high-watermark for
telemetry: a large gap between `BytesConsumed` (monotonic, tracks every
byte the scanner has seen) and `PeakBufferedBytes` is the proof the
scan held bounded memory.

The regression test that pins this:
[`StreamingMemoryBoundTests`](../tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs)
feeds **5 MiB** of synthetic HTML in 4 KiB chunks and asserts
`PeakBufferedBytes < MaxBufferSize` (4 KiB). In practice the measured
value is in the low hundreds of bytes.

### Honest framing — bounded by longest partial tag

1. **Bounded by longest partial tag at a chunk boundary, not chunk size.**
   alpha.21 changed the parse loop so chunks are scanned in-place
   (no wholesale copy into the tokenizer's buffer). Only the bytes of
   a tag that's mid-parse when the chunk runs out get retained.
   Measured peaks: 0 B for 16 KB chunks over a 200 KB body; 19 B for
   1 KB chunks. "Bounded" here is `O(longest tag)`, not
   `O(chunk + longest tag)` as in alpha.19.
2. **Tripwire matching is O(claim size) per event.** alpha.24 dropped
   the alpha.21..23 per-tick MinHash recompute
   (`O(WindowSize × SignatureSize)` per accepted structural tag). The
   tripwire matcher walks the claim's required class/data/aria hashes
   linearly against the event's hash arrays — both small (typically &le;4
   classes per claim, &le;3 attrs). No per-tick allocation, no signature
   rebuild.

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

## 8. Limitations and follow-ups

Calling these out honestly so operators aren't surprised:

- **Bounded by longest partial tag at chunk boundary, ~hundreds of bytes.**
  Measured: 0 B peak against 200 KB response in 16 KB chunks; 19 B in 1 KB
  chunks. The MaxBufferSize cap is 4 KB; pathological input (a single tag
  &gt; 4 KB) throws rather than silently drop bytes.
- **Tripwire matching is exact.** alpha.24 traded the alpha.21..23
  MinHash bands' soft-tolerance for exact hash equality on
  stable-by-construction identifiers (the identity-aware inducer
  rejects hash-shaped class tokens via `DefaultClassStabilityFilter`).
  Pages whose extracted-template identifiers change between sessions
  (utility-class shuffles, hashed CSS module names that drift, JIT
  class-name churn) generate a clean miss → refit signal rather than
  a probabilistic fuzzy-match that hides the drift.
- **`FenceScanner` + `IncrementalFenceScanner` share a single static
  `Tick`.** alpha.21 extracted the per-tick algorithm into
  `StreamingTick.Step` (over a `StreamingTickState`). alpha.24 kept the
  shared-Tick shape — both scanners call literally the same code.
- **Per-event hash data is capped.** Tags with more than
  `TagEvent.MaxClassesPerEvent = 8` classes or more than
  `TagEvent.MaxAttrPairsPerEvent = 3` data-* / aria-* attributes lose
  the tail. The inducer keeps tripwires narrow (id-only when present;
  otherwise tag + first-two-classes; ContentEnd is tag-only since
  closes have no attributes) so the per-event cap rarely matters in
  practice — but operators on very attribute-heavy markup should
  know.
- **Auto-induction is heuristic.** `StreamingTemplateInducer` picks
  semantic-marker targets (`<header>`, `<article>`, `<main>`,
  paragraph-cluster parent). Pages without semantic markup — a
  `<div>`-soup hand-rolled landing page — get `null` from the
  inducer and stay `NoTemplate` forever for that host.

---

## See also

- [`README.md`](../README.md) — top-level project overview.
- [`RELEASE_NOTES.txt`](../RELEASE_NOTES.txt) — alpha.16 → alpha.19
  entries cover the full streaming evolution.
- [`src/StyloExtract.Streaming/`](../src/StyloExtract.Streaming/) — source.
- [`tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs`](../tests/StyloExtract.Streaming.Tests/StreamingMemoryBoundTests.cs)
  — the bounded-memory regression guard.
