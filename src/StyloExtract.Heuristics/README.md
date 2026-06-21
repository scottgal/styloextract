# Mostlylucid.StyloExtract.Heuristics

YAML-driven block classifier and extractor inducer/applicator for StyloExtract.

## What this package is

Provides the heuristic layer that turns a cleaned DOM into structured content blocks:

- `HeuristicBlockClassifier` (implements `IBlockClassifier`) - classifies DOM blocks by role (MainContent, Navigation, Footer, Boilerplate, etc.) using YAML rule sets loaded as embedded resources. No hard-coded lists in C# code; all rules live in `Definitions/*.json`.
- `BlockSegmenter` (implements `IBlockSegmenter`) - segments a cleaned document into candidate content blocks by traversing the DOM tree and applying structural heuristics
- `ClassNoiseFilter` - removes high-frequency, low-information class names from the shingle generator to reduce fingerprint noise
- `ExtractorInducer` (implements `IExtractorInducer`) - induces a template extractor (set of CSS selectors + block roles + weights) from a classified block list; this is the "first encounter" learning step
- `ExtractorApplicator` (implements `IExtractorApplicator`) - applies a stored extractor to a new document using the induced CSS selectors; faster than re-running full heuristic classification

## When to depend on this directly

Consumed transitively by `Mostlylucid.StyloExtract.AspNetCore`. Take a direct dependency when building custom classifiers, extending the YAML rule sets, or writing tests against `HeuristicBlockClassifier` in isolation.

## Usage

```csharp
// Standard wiring (handled by AddStyloExtract)
services.AddSingleton<ClassNoiseFilter>(_ => ClassNoiseFilter.LoadFromEmbeddedResource());
services.AddSingleton<IBlockClassifier>(_ => HeuristicBlockClassifier.LoadFromEmbeddedResources());
services.AddSingleton<IBlockSegmenter, BlockSegmenter>();
services.AddSingleton<IExtractorInducer, ExtractorInducer>();
services.AddSingleton<IExtractorApplicator>(sp =>
    new ExtractorApplicator(sp.GetService<ILogger<ExtractorApplicator>>()));
```

```csharp
// Standalone classification (testing)
var classifier = HeuristicBlockClassifier.LoadFromEmbeddedResources();
var blocks = segmenter.Segment(cleanedDoc);
var classified = classifier.Classify(blocks);
```

## AOT

This package is `IsAotCompatible=true`. Rule definitions are loaded from embedded JSON resources at startup; no runtime code generation.

---

[Full documentation and package family](https://github.com/scottgal/styloextract)
