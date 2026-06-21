# Mostlylucid.StyloExtract.Core

Extraction orchestration: wires parse, fingerprint, match, induce, apply, and render into `ILayoutExtractor.ExtractAsync`.

## What this package is

`LayoutExtractor` (implements `ILayoutExtractor`) is the single entry point for extraction. It coordinates the full pipeline:

1. Parse HTML via `IHtmlDomParser`
2. Clean DOM via `IDomCleaner`
3. Fingerprint via `IStructuralFingerprinter` (MinHash + LSH + anchor-path + pq-grams)
4. Fast-path LSH match against `ITemplateIndex` (< 1 ms for known templates)
5. If miss: slow-path pq-gram cosine match
6. If novel: segment + classify + induce extractor via `IExtractorInducer`
7. Apply extractor via `IExtractorApplicator` (or heuristic classification on novel)
8. Render to Markdown via `IMarkdownRenderer`
9. Record observation; trigger refit if drift threshold exceeded
10. Emit `StyloExtractSignal` events via `TypedSignalSink`

## When to depend on this directly

Consumed transitively by `Mostlylucid.StyloExtract.AspNetCore`. Take a direct dependency only if you are wiring the DI registrations manually (e.g. in a non-ASP.NET host) or adding the `LayoutExtractor` to a custom container.

## Usage

```csharp
// Standard wiring via AddStyloExtract (preferred)
builder.Services.AddStyloExtract(o => { o.StorePath = "store.db"; });

// Inject and call
var extractor = sp.GetRequiredService<ILayoutExtractor>();
var result = await extractor.ExtractAsync(
    html,
    new Uri("https://example.com/article"),
    new ExtractionOptions { Profile = ExtractionProfile.RagFull });

Console.WriteLine(result.Markdown);
Console.WriteLine(result.Match.Status);        // FastPathHit on repeat visits
Console.WriteLine(result.Match.TemplateVersion);
```

## AOT

This package is `IsAotCompatible=true`.

---

[Full documentation and package family](https://github.com/scottgal/styloextract)
