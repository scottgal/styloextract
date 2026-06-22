# StyloExtract Per-Component Benchmark Results

Measured on Apple M-series (arm64), .NET 10, BenchmarkDotNet 0.14.0.
Run: `dotnet run --project bench/StyloExtract.Benchmarks/ -c Release -- --filter '*'`

Input sizes:
- **Small** ~5 KB: single article, minimal chrome, 5 paragraphs
- **Medium** ~50 KB: blog article with comments, sidebar, related-posts grid, 30+ paragraphs
- **Large** ~198 KB: news article, Gutenberg blocks, extensive nav, tables, 100+ paragraphs, 18+ comments

All benchmarks use `[MemoryDiagnoser]`. Times are wall-clock per operation (mean).

---

## Hotspot Ranking (Medium ~50 KB input)

This table ranks pipeline stages by their contribution to total `ExtractAsync` cost on the Medium input.
The baseline full-extract mean for Medium-sized content is approximately **20 ms** (novel path, observed in FullExtractNovelBench).

| Stage                       | Stage Mean  | Alloc/op   | Notes                                      |
|-----------------------------|------------:|-----------:|--------------------------------------------|
| Parse (AngleSharp)          | 407 us      | 497 KB     | Dominant input-size-proportional cost      |
| Classify (full v1.5.x pass) | 1,710 us    | 4,298 KB   | **Highest allocator** — rebuilds lists N times |
| IntraBlockCleaner (inside Classify) | 922 us | 905 KB  | Included in Classify; isolated here for drill-in |
| DomCleaner                  | 715 us      | 522 KB     | Script/style/SVG strip; O(tag count)       |
| Fingerprint                 | 1,188 us    | 614 KB     | Shingle+MinHash+LSH+pq-gram                |
| Segment                     | 75 us       | 201 KB     | DOM walk — cheap                           |
| Applicator (fast-path)      | 378 us      | 1,105 KB   | CSS selector query per rule                |
| RepeatedItemDetector        | 72 us       | 128 KB     | Walk + class-signature check               |
| Markdown render (RagFull)   | 12 us       | 247 KB     | Cheap; scales with block count             |
| JsonLd (with schema)        | 11 us       | 11 KB      | Negligible; JSON parse only on fallback    |
| JsonLd (no schema)          | 10 us       | 5 KB       | Near-free early exit                       |

---

## ParseBench

`IHtmlDomParser.Parse(html)` — AngleSharp HTML parse + tree build only.

| Method | Size   | Mean       | Allocated  |
|--------|--------|------------|------------|
| Parse  | Small  | 53.45 us   | 78.52 KB   |
| Parse  | Medium | 406.89 us  | 497.40 KB  |
| Parse  | Large  | 1,431.15 us | 1,645.75 KB |

---

## DomCleanerBench

`IDomCleaner.Clean(document)` — strips script/style/template/noscript/svg nodes.

| Method | Size   | Mean       | Allocated  |
|--------|--------|------------|------------|
| Clean  | Small  | 89.00 us   | 84.55 KB   |
| Clean  | Medium | 714.75 us  | 522.16 KB  |
| Clean  | Large  | 2,398.55 us | 1,730.29 KB |

---

## SegmenterBench

`IBlockSegmenter.Segment(document)` — DOM walk picking semantic tags and blocky divs.

| Method  | Size   | Mean       | Allocated  |
|---------|--------|------------|------------|
| Segment | Small  | 4.42 us    | 8.48 KB    |
| Segment | Medium | 75.43 us   | 201.34 KB  |
| Segment | Large  | 373.85 us  | 1,507.64 KB |

---

## FingerprintBench

`IStructuralFingerprinter.Compute(document)` — shingle generation, MinHash sketching, LSH banding, anchor-path fingerprinting, pq-gram extraction.

| Method      | Size   | Mean         | Allocated   |
|-------------|--------|-------------|-------------|
| Fingerprint | Small  | 113.5 us    | 103.92 KB   |
| Fingerprint | Medium | 1,188.4 us  | 613.67 KB   |
| Fingerprint | Large  | 14,752.3 us | 1,642.24 KB |

**Note:** The Large fingerprint time (14.7 ms) is the single biggest surprise in the dataset. The pq-gram extractor walks the full element tree and fingerprinting scales super-linearly with DOM node count. This is a strong optimisation candidate (see recommendations below).

---

## ClassifyBench

`IBlockClassifier.Classify(elements)` — full v1.5.x heuristic pass including wrapper-ancestor suppression, RepeatedItemDetector, IntraBlockCleaner, framework content-class-hint lookup, singleton role cap, and DOM-order re-sort.

Note: each iteration re-parses the document because IntraBlockCleaner mutates the DOM. The times below include parse + clean + segment as mandatory setup; the isolated classify cost is the delta from SegmenterBench.

| Method   | Size   | Mean        | Allocated    |
|----------|--------|-------------|--------------|
| Classify | Small  | 209.9 us    | 434.99 KB    |
| Classify | Medium | 1,709.7 us  | 4,298.25 KB  |
| Classify | Large  | 11,162.1 us | 25,951.03 KB |

The Large input allocates **25.9 MB per call** — driven by the O(N^2) greedy non-overlapping selection loop (`accepted.Any(a => IsAncestor(...))`) and list rebuilds in the repeated-item and role-cap passes.

---

## IntraBlockCleanerBench

`IntraBlockCleaner.Clean(element)` — removes nav/toc/toolbar/breadcrumb descendants from the selected content subtree; collapses empty wrappers.

| Method          | Size   | Mean       | Allocated   |
|-----------------|--------|------------|-------------|
| IntraBlockClean | Small  | 135.8 us   | 147.09 KB   |
| IntraBlockClean | Medium | 921.9 us   | 905.19 KB   |
| IntraBlockClean | Large  | 3,766.4 us | 4,011.58 KB |

Included inside ClassifyBench (runs once per accepted content block). On Large pages with complex nested nav, the iterative "collapse empty wrappers" loop (`do { ... } while (changed)`) is the main allocation driver.

---

## RepeatedItemDetectorBench

`RepeatedItemDetector.Detect(body)` — walks the full DOM body looking for homogeneous child patterns (forum posts, listing cards, product tiles).

| Method | Size   | Mean      | Allocated  |
|--------|--------|-----------|------------|
| Detect | Small  | 2.62 us   | 584 B      |
| Detect | Medium | 72.35 us  | 127.74 KB  |
| Detect | Large  | 198.02 us | 325.96 KB  |

Cheap relative to Classify and Fingerprint. The allocation is dominated by the class-signature intersection sets built per candidate group.

---

## JsonLdFallbackNoSchemaBench

`JsonLdContentExtractor.ExtractMainContent(document)` when no `application/ld+json` scripts are present. Exercises the early-exit path (`querySelector` returns empty).

| Method  | Size   | Mean      | Allocated |
|---------|--------|-----------|-----------|
| Extract | Small  | 1.71 us   | 1.41 KB   |
| Extract | Medium | 10.37 us  | 4.98 KB   |
| Extract | Large  | 25.66 us  | 17.03 KB  |

The non-trivial Medium/Large times reflect `QuerySelectorAll("script[type='application/ld+json']")` scanning the full DOM even when no scripts exist. Could be short-circuited with a `document.Scripts` filtered count check.

---

## JsonLdFallbackWithSchemaBench

`JsonLdContentExtractor.ExtractMainContent(document)` when `application/ld+json` scripts are present. Exercises JSON parsing and field extraction.

| Method        | Mean      | Allocated |
|---------------|-----------|-----------|
| ExtractSmall  | 2.83 us   | 7.63 KB   |
| ExtractMedium | 11.22 us  | 10.88 KB  |
| ExtractLarge  | 27.66 us  | 26.39 KB  |

Negligible overhead even with schema. The fallback is only invoked when heuristic content is < 200 chars, so it is rarely exercised on well-structured pages.

---

## MarkdownRenderBench

`IMarkdownRenderer.Render(blocks, profile)` across three extraction profiles.

| Method                   | Mean        | Allocated   |
|--------------------------|-------------|-------------|
| RenderSmall_MainContent  | 1,684 ns    | 28.05 KB    |
| RenderSmall_RagFull      | 1,423 ns    | 28.05 KB    |
| RenderSmall_AgentNav     | 207 ns      | 1.92 KB     |
| RenderMedium_MainContent | 10,948 ns   | 241.84 KB   |
| RenderMedium_RagFull     | 11,705 ns   | 247.01 KB   |
| RenderMedium_AgentNav    | 1,015 ns    | 17.07 KB    |
| RenderLarge_MainContent  | 158,987 ns  | 1,888.88 KB |
| RenderLarge_RagFull      | 151,524 ns  | 1,888.88 KB |
| RenderLarge_AgentNav     | 973 ns      | 11.32 KB    |

AgentNavigation is dramatically cheaper than the content-bearing profiles because it emits only link lists rather than full prose markdown. The Large RagFull allocation (1.9 MB) comes from `StringBuilder` growth across 100+ blocks.

---

## ApplicatorBench

`IExtractorApplicator.Apply(document, extractor)` — fast-path CSS selector queries against a pre-parsed document using a learned extractor with 4-8 rules.

| Method | Size   | Mean       | Allocated   |
|--------|--------|------------|-------------|
| Apply  | Small  | 52.30 us   | 134.41 KB   |
| Apply  | Medium | 377.55 us  | 1,104.82 KB |
| Apply  | Large  | 4,025.55 us | 9,094.37 KB |

The Large applicator (4 ms, 9 MB) is expensive because `QuerySelectorAll` on AngleSharp scans the full tree for each selector. With 8 rules and a 1500+ node DOM, this is 8 full tree traversals. The allocation spike at Large is the dominant fast-path cost for large pages.

---

## Reference: Full Extract Benchmarks (Existing)

| Method               | Mean      | Allocated |
|----------------------|-----------|-----------|
| FullExtract_CacheHit | 15.65 ms  | 5.13 MB   |
| FullExtract_SlowPath | 15.66 ms  | 5.15 MB   |
| FullExtract_Novel    | 20.45 ms  | 14.13 MB  |

These use the existing `article.html` test fixture (medium-complexity single article).

---

## Optimisation Recommendations

Ranked by potential impact based on the data above:

### 1. Fingerprint scaling on Large pages (14.7 ms, highest priority)

The pq-gram extractor walks the entire element tree and MinHash operates over all shingles. On a 198 KB page the fingerprinter costs 14.7 ms — more than the full novel extract on a medium page. Two directions: (a) limit pq-gram depth to 4-5 levels rather than the full tree, or (b) sample shingles rather than using all of them. Either change needs WCXB regression testing to verify match quality is not degraded.

### 2. Classifier greedy-selection O(N^2) loop

The `accepted.Any(a => IsAncestor(a.Element, element) || IsAncestor(element, a.Element))` check in Step 3 is O(candidates x accepted x depth). On Large pages (25.9 MB alloc) this dominates. Replace with a pre-built ancestor-set per candidate (computed once during the initial walk) and an O(1) HashSet lookup. This is safe to do without changing observable behaviour.

### 3. Applicator `QuerySelectorAll` repeated tree walks

Eight rules on a 1500-node DOM = 8 full scans. The applicator could batch queries using `:is()` selectors or maintain a pre-indexed element map keyed by tag/class after parse, then look up candidates in O(1) per selector instead of O(N).

### 4. DomCleaner on Large pages (2.4 ms)

`QuerySelectorAll("script"), QuerySelectorAll("style")` ... per tag is 5 separate full-tree scans. A single `querySelectorAll("script, style, template, noscript, svg")` pass halves the traversal count.

### 5. IntraBlockCleaner iterative empty-wrapper collapse

The `do { ... } while (changed)` loop re-scans descendants from scratch after each mutation pass. Replacing with a single bottom-up traversal (process leaves first, remove if empty, skip already-removed ancestors) would convert this from O(depth x nodes) to O(nodes).
