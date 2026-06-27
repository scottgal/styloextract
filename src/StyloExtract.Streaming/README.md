# Mostlylucid.StyloExtract.Streaming

Zero-allocation, bounded-memory **gateway fence scanner** for the
StyloExtract family. Rides alongside the byte stream of an HTTP response
and emits a verdict — `Captured` / `Bailout` / `NoTemplate` / `Continue`
— while the body is still in flight, without ever building a DOM or
buffering the full page.

Designed for the gateway position: HTTP reverse proxies, CDN edges,
ASP.NET output filters, and Stylobot's response pipeline. Use it to
decide whether a response is worth feeding to the full
`LayoutExtractor` extraction pipeline, before you commit to buffering
it.

## Memory contract

A sliding-window tokenizer holds ONLY the partial tag bytes that
straddle a chunk boundary — typically &lt;500 B, often zero. Each chunk
is parsed inline; complete-tag bytes are dropped immediately, never
copied into a holding buffer. The hard cap is 4 KiB
(`IncrementalHtmlTokenizer.MaxBufferSize`); a single tag larger than
that throws `InvalidOperationException` rather than silently dropping
bytes. Measured peak: 0 B for 16 KB chunks over a 200 KB body; 19 B for
1 KB chunks. Pinned by the `StreamingMemoryBoundTests` regression suite.

## Wire-up

```csharp
// Singletons — the scanner and store are thread-safe.
services.AddSingleton<IStreamingTemplateStore, InMemoryStreamingTemplateStore>();
// Or durable: new SqliteStreamingTemplateStore("streaming-templates.db")
services.AddSingleton<StreamingPathSelector>();
services.AddSingleton<StreamingTemplateInducer>();
services.AddSingleton<StreamingRefitOrchestrator>();
```

## Hot path

```csharp
var selector = sp.GetRequiredService<StreamingPathSelector>();
var inducer  = sp.GetRequiredService<StreamingTemplateInducer>();
var store    = sp.GetRequiredService<IStreamingTemplateStore>();

await selector.WarmByHostAsync(host);
var verdict = selector.ScanByHost(host, bodyBytes);

if (verdict == ScanVerdict.NoTemplate)
{
    // First visit to this host — induce a template heuristically.
    var induced = inducer.Induce(host, bodyBytes);
    if (induced is not null)
        await store.UpsertAsync(induced);
}
```

For chunked (streaming) inputs, use `IncrementalFenceScanner.Create(template)`
and call `Feed(chunk)` per chunk. The verdict latches on the first
terminal result.

## See also

- **Full guide:** [`docs/streaming.md`](https://github.com/scottgal/styloextract/blob/main/docs/streaming.md)
  covers the auto-induction lifecycle, bounded-memory proof, refit /
  versioning, and a comparison table for streaming vs `LayoutExtractor`.
- **Top-level README:** [`README.md`](https://github.com/scottgal/styloextract/blob/main/README.md)
- Pairs with `Mostlylucid.StyloExtract.Fingerprint` (layout learning)
  and the existing `ITemplateIndex` template store; the streaming
  template format is its own shape (`StreamingTemplate` with
  `TemplateFence` MinHash sketches), not an LLM template or operator
  template.
