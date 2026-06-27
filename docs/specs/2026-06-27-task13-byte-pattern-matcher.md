# Task 13 — Streaming Byte-Pattern Matcher

**Status:** spec; pending approval before implementation dispatch.

## Goal

Replace the current `TripwireScanner` (alpha-something + Task 4) with a true byte-level pattern matcher that operates directly on the HTTP response byte stream — no tokeniser, no per-tag identity-claim conjunction evaluation, no depth tracker.

The new scanner walks bytes, hunts for the smallest distinguishing character sequences that mark content open and close, clips the byte span between them, ends the response read. Microsecond-class hot path, zero allocation, drift-resistant because the patterns are LOCAL to the content rather than tied to its ancestor chain.

## Why this beats Task 4's tripwire model

| | Task 4 tripwire | Task 13 byte-pattern |
|---|---|---|
| Hot-path cost | Tokenise every tag + evaluate claim | `IndexOf`-class search over bytes |
| Allocation per tag | Stackalloc claim set | None |
| Depth tracking | Yes (DOM stack) | No |
| Drift sensitivity | Breaks when class changes | Breaks only when the marker bytes themselves change |
| Template induction shape | `IdentityClaim` conjunction | Byte pattern with attribute-order tolerance |
| Surrounding-DOM changes | Doesn't affect match | Doesn't affect match |
| Different content with same anchor structure | Risk of mis-match (same claim set) | Mis-match impossible (different bytes) |

The drift property is the headline win. Today's tripwire breaks when `<main class="article-body">` becomes `<main class="article-body-v2">`. The byte-pattern matcher anchored on `<main ` + nearby `id="post"` doesn't care about the class change at all — it's only watching for the bytes the inducer chose as the minimal distinguishing pattern.

## Pattern shape

A `BytePattern` is a small DFA that matches:

```
<TAGNAME [WS]? [ATTR=VAL]?... >
```

With these tolerances built in:
- **Attribute order** — patterns specify `(attr-name, attr-value)` pairs; the DFA scans within the tag for each pair regardless of order
- **Whitespace** — `\s+` between tag name and attrs; `\s*` around `=`; `\s*` before `>`
- **Quote style** — `"`, `'`, or unquoted (HTML5 allows unquoted attribute values without whitespace/special chars)
- **Self-closing slash** — `<tag ... />` matches the open pattern AND emits an immediate close

A pattern's full shape:

```csharp
public readonly struct BytePattern
{
    public ReadOnlySpan<byte> TagName { get; }       // e.g. "main"u8
    public ReadOnlySpan<AttrConstraint> Attrs { get; }
    public bool IsClose { get; }                     // </main> close-tag form
    public int MaxScanBytes { get; }                 // cap how far past TagName we scan for attrs
}

public readonly struct AttrConstraint
{
    public ReadOnlySpan<byte> Name { get; }          // e.g. "id"u8
    public ReadOnlySpan<byte> Value { get; }         // e.g. "post-content"u8
}
```

Encoded into a compact byte-DFA at template-load time. State table fits in a few hundred bytes per pattern.

## Scanner state machine

`StreamingTemplate` carries three byte patterns:

```csharp
public sealed record StreamingTemplate
{
    public required Guid TemplateId { get; init; }
    public required string Host { get; init; }
    public required int Version { get; init; }

    public required BytePattern PrefixPattern { get; init; }
    public required BytePattern ContentStartPattern { get; init; }
    public required BytePattern ContentEndPattern { get; init; }    // typically close-tag

    public required int BailoutBytes { get; init; }
    public required int MaxCaptureBytes { get; init; }
}
```

Scanner states: `AwaitPrefix → AwaitContentStart → Capturing → Captured/Bailed`.

Each `Feed(ReadOnlySpan<byte> chunk)` call:
1. While in `AwaitPrefix` and the prefix DFA hasn't matched: feed bytes through the prefix DFA. On match → transition to `AwaitContentStart`. Set `_capturedBytes` to bytes consumed so far.
2. While in `AwaitContentStart`: feed bytes through the content-start DFA. On match → transition to `Capturing`. Snapshot `_captureStart = totalBytesConsumed - matchByteLength`. The captured byte span starts AT the start of the matched open tag (so the content includes its container element's opening).
3. While in `Capturing`: feed bytes through the content-end DFA. On match → `_captureEnd = totalBytesConsumed` (inclusive of the end pattern). Verdict = `Captured`, return.
4. Byte-budget exceeded in any pre-`Capturing` state → `Bailed`.
5. `MaxCaptureBytes` exceeded in `Capturing` → `Bailed`.
6. End of stream without `Captured` → `Bailed` (existing alpha.23 Flush() behaviour preserved).

The captured byte span (start..end) is what the layout extractor receives. No DOM materialisation in the streaming path; the consumer chooses what to do with it.

## Quoted-string / comment / script awareness

The byte matcher must skip:
- `<!-- ... -->` HTML comments
- `<script ...>` body up to `</script>`
- `<style ...>` body up to `</style>`
- `<![CDATA[ ... ]]>` (rare in HTML but exists)

These are the same skip cases the current `IncrementalHtmlTokenizer` handles. The byte matcher implements its own lightweight version: a one-byte lookahead state that says "I'm inside a script body, suspend pattern matching until I see `</script>`".

Quoted-attribute-value-containing-`<` is harder. `<div title="x<main>">` could trigger a false `<main>` match. The pattern DFA, when scanning attribute values, tracks quote state — open quote → don't match `<` until close quote. Same shape as a YAML parser's flow-scalar handling. Adds ~10 LOC of state per pattern.

## Pattern induction

`StreamingTemplateInducer.Induce(host, html)` — input: example page bytes. Output: a `StreamingTemplate` with three byte patterns.

Algorithm:
1. Parse the example page once (use existing `IncrementalHtmlTokenizer` + a depth tracker) to find:
   - The element that the layout heuristic classified as `MainContent` (use `ExtractorInducer`'s output)
   - That element's full opening tag bytes (e.g. `<main id="post-content" class="article">`)
   - That element's matching close tag bytes (e.g. `</main>` — or, if there's a sibling matching `</main>` earlier, use a depth-aware close)
2. **Pick the minimal distinguishing prefix pattern**: the smallest substring of `<header>...<main` that uniquely appears once before the content. Usually `<main ` is enough if there's only one `<main>` on the page. If multiple `<main>` tags exist, fall back to `<header ` + the prefix anchor's attrs.
3. **Pick the minimal distinguishing content-start pattern**: the content element's open tag with just the minimum attributes needed to disambiguate from other tags of the same type on the page. Use `IdentityClaimSelectorBuilder`'s logic (already exists from Task 2) but emit the result as a `BytePattern` shape.
4. **Pick the content-end pattern**: the matching close tag. Depth-aware close is tricky from a pure byte matcher — use a counter for the same tag name and require N+1 occurrences of close where N opens were seen between content-start and now. (This is a small concession back to per-tag awareness — pure byte matching can't easily count nested tags. The counter is one int of state.)

Re-use `DefaultClassStabilityFilter` from Task 51 when picking which classes/ids count as "stable enough to anchor on". Hash-shaped tokens get rejected from the pattern.

## Corpus mining (Phase 2 implication)

The corpus mining we're building (Tasks 6-11) currently mines `IdentityClaim` chains. With Task 13 there are TWO units of mining:
- Layout-side: `IdentityClaim` chains (existing)
- Streaming-side: `BytePattern` shapes

Same `WriteBehindLfuStore` infrastructure, different unit. The streaming-side miner finds the byte patterns common to `MainContent` across hosts in a cluster — e.g., most blog hosts have `<article ` somewhere before the post body; common substring mining surfaces that as a cluster-wide prefix pattern.

Defer the streaming-side corpus integration to a separate task (call it Task 14). Task 13 just lands the matcher + inducer.

## Replaces / preserves

**Replaces:**
- `TripwireScanner` / `IncrementalTripwireScanner` (Task 4 deliverables) → renamed and rewritten as `BytePatternScanner` / `IncrementalBytePatternScanner`
- `IdentityClaim`-based fence model in `StreamingTemplate` → `BytePattern` model
- The Resolve* hash methods on `IncrementalHtmlTokenizer` (Task 4 added) → no longer needed by streaming path; can be removed if no other consumer
- `IdentityClaimMatcher.MatchesByHash` overload (Task 4 added in Abstractions) → can stay; layout side might use it

**Preserves:**
- `IncrementalHtmlTokenizer` itself — still useful for streaming-side INDUCTION (the inducer needs to parse a sample page to pick patterns). Just not used by the hot-path SCAN anymore.
- Partial-tag tokenizer buffer (the bounded-memory contract from alpha.21+) — same shape; the byte matcher also benefits from compact-on-emit.
- Depth tracker for the inducer's close-tag-counting trick (not in the scanner).
- Version chain + `PRAGMA user_version` self-heal — bump `CurrentStoreVersion` from 3 to 4 so existing alpha.21..23 + Phase-1 Task-4 dogfood DBs self-heal.
- End-of-stream Bailout latch (Flush behaviour).
- The DI extension `AddStyloExtractStreaming`.

## Risks + open questions

1. **HTML5 quirks**: weird attribute syntax, unclosed tags, parser-quirks-mode pages. The pure tokeniser handles these via AngleSharp; a hand-rolled byte matcher won't. Mitigation: target standards-conforming pages, fall back to `IncrementalHtmlTokenizer` on parse anomalies. (Or: ship the byte matcher as opt-in initially, gate behind a flag, dogfood against real pages before defaulting.)
2. **The close-tag counter** is a small but real concession back to structure-awareness. If we want PURE byte matching with no counter, the end pattern has to be unique-enough that the FIRST occurrence after content-start is reliably the right close. For typical templates (`</article>` after a long unique article body) this works. For nested cases (e.g., article with quoted inline `<article>` example code) the counter wins.
3. **Compressed responses**: HTTP responses may be gzip/brotli/zstd. The byte matcher operates on UNCOMPRESSED bytes. The consumer threads a decompressor before the matcher. HttpClient does this automatically with `DecompressionMethods.All`; lucidview FULL already uses that.
4. **Encoding**: assume UTF-8. Pages declaring other encodings via meta charset are rare and the byte matcher's literal patterns assume UTF-8 byte sequences. Note as a known limitation.

## Test surface

`tests/StyloExtract.Streaming.Tests/BytePatternMatcherTests.cs`:
- Attribute order tolerance: pattern `<main id="post">` matches `<main id="post" class="x">` AND `<main class="x" id="post">`.
- Quote style tolerance: pattern matches `id="post"`, `id='post'`, and `id=post`.
- Whitespace tolerance: `<main  id="post" >` matches.
- Script body skip: `<script>var x = '<main id="post">';</script><main id="post">content</main>` matches the SECOND `<main>`, not the one inside the script string.
- Comment body skip: `<!-- <main id="post"> --><main id="post">content</main>` matches the second.
- Close-tag counter: nested `<article>...<article>inline</article>...</article>` correctly identifies the OUTER close.
- Bailout on byte budget.
- End-of-stream Bailout latch.

`tests/StyloExtract.Streaming.Tests/StreamingTemplateInducerBytePatternTests.cs`:
- Induce from a small realistic page; assert pattern picks the MainContent open tag minimally (just enough attrs to disambiguate).
- Hash-shaped classes filtered out at induction (re-use `DefaultClassStabilityFilter`).
- Multiple candidate patterns → picks the shortest distinguishing one.

Integration test: induce-then-scan round-trip against a real-page fixture. Capture must equal the inducer's chosen byte span. Headline measurement: peak buffered bytes; expect this to be tiny (just the longest pattern's match buffer + chunk slack).

## Migration

`SqliteStreamingTemplateStore.CurrentStoreVersion`: 3 → 4. Existing dogfood DBs (alpha.21..23 + Phase-1 Task-4) drop their now-incompatible templates on first open via the existing self-heal path. Fresh installs: no-op + stamp.

`StreamingTemplate` JSON serialisation changes: `PrefixTripwire` / `ContentStartTripwire` / `ContentEndTripwire` → `PrefixPattern` / `ContentStartPattern` / `ContentEndPattern`. Old JSON blobs fail to deserialise → caught by the user_version drop. No legacy-row recovery needed.

## Approval gate

Before dispatching the implementer:
1. Confirm: replace tripwire scanner entirely, or sit beside as opt-in?
2. Confirm: counter-based close-tag awareness OK, or pursue counter-free design?
3. Confirm: defer streaming-side corpus mining (Task 14) until after 13 lands?

If all three are yes, dispatch.
