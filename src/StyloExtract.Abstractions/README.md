# Mostlylucid.StyloExtract.Abstractions

Core interfaces, records, and signal catalog for StyloExtract. Zero runtime dependencies beyond the `mostlylucid.ephemeral` signal sink.

## What this package is

`Abstractions` defines the entire public contract of StyloExtract:

- `ILayoutExtractor` - the main extraction entry point
- `IHtmlDomParser`, `IDomCleaner`, `IBlockSegmenter`, `IBlockClassifier` - parse/clean/segment/classify pipeline interfaces
- `IStructuralFingerprinter`, `IMarkdownRenderer`, `IExtractorInducer`, `IExtractorApplicator` - fingerprint and rendering interfaces
- `ITemplateIndex` - template store interface
- `IRenderedHtmlFetcher` - Playwright abstraction
- `ITemplateVersionEventSink` - version change event consumer interface
- `ExtractionResult`, `ExtractionOptions`, `LayoutMatch`, `MatchStatus`, `ExtractionStats` - result records
- `StyloExtractSignals` - string constants for all 11 extraction signals
- `StyloExtractSignal` - typed signal payload for `TypedSignalSink<StyloExtractSignal>`

## When to depend on this directly

Take a direct dependency on `Abstractions` when you are:
- Writing a custom implementation of any StyloExtract interface
- Building a consumer that reads `ExtractionResult` records but does not perform extraction
- Subscribing to `TypedSignalSink<StyloExtractSignal>` signals in a StyloFlow pipeline
- Writing tests that mock `ILayoutExtractor` or `ITemplateIndex`

Normal application code should depend on `Mostlylucid.StyloExtract.AspNetCore` instead, which pulls this package transitively.

## Key types

```csharp
// The extraction result
var result = await extractor.ExtractAsync(html, sourceUri);
result.Markdown        // extracted content
result.Match.Status    // FastPathHit | SlowPathMatch | Novel | Refit
result.Match.TemplateId
result.Match.TemplateVersion

// Signal catalog
StyloExtractSignals.MatchFastPathHit   // "stylo.extract.match.fastpath.hit"
StyloExtractSignals.TemplateRefit      // "stylo.extract.template.refit"
StyloExtractSignals.VersionDetected    // "stylo.extract.version.detected"
// ... 11 signals total
```

## AOT

This package is `IsAotCompatible=true`. No reflection, no dynamic codegen.

---

[Full documentation and package family](https://github.com/scottgal/styloextract)
