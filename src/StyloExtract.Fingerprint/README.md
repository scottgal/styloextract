# Mostlylucid.StyloExtract.Fingerprint

MinHash, pq-grams, LSH banding, and anchor-path fingerprinting for StyloExtract.

## What this package is

Implements structural DOM fingerprinting via multiple complementary techniques:

- `MinHashSketcher` - produces a 128-dimensional MinHash signature from DOM shingles
- `ShingleGenerator` - generates character/token shingles from cleaned DOM text with noise filtering
- `LshBander` - divides a MinHash signature into bands for locality-sensitive hashing (fast approximate nearest-neighbour)
- `AnchorPathFingerprinter` - MinHash-based fingerprint of XPath anchor paths, providing structural identity independent of text content
- `PqGramExtractor` - extracts pq-gram multisets for the slow-path cosine similarity score
- `StructuralFingerprinter` (implements `IStructuralFingerprinter`) - combines all of the above into a single `StructuralFingerprint`

The two-pass matching strategy:
1. Fast path: LSH band lookup, O(1) per known template, measured at 16 µs
2. Slow path: pq-gram cosine similarity against candidate templates when LSH misses

## When to depend on this directly

Consumed transitively by `Mostlylucid.StyloExtract.Core`. Take a direct dependency only for custom fingerprinting work, benchmarking, or standalone LSH experiments.

## Usage

```csharp
// Standard wiring (handled by AddStyloExtract)
services.AddSingleton<MinHashSketcher>(sp => new MinHashSketcher(128));
services.AddSingleton<IStructuralFingerprinter, StructuralFingerprinter>();
```

```csharp
// Standalone fingerprint
var sketcher = new MinHashSketcher(128);
var noise = ClassNoiseFilter.LoadFromEmbeddedResource(); // from Heuristics
var fingerprinter = new StructuralFingerprinter(
    new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
    new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());

var fp = fingerprinter.Compute(cleanedDocument);
Console.WriteLine(fp.LshBands.Length);   // number of LSH buckets
```

## Performance

ProbeFastPath (LSH bucket lookup in SQLite): 16 µs mean, measured by the benchmark project.

## AOT

This package is `IsAotCompatible=true`.

---

[Full documentation and package family](https://github.com/scottgal/styloextract)
