# Changelog

All notable changes to StyloExtract are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versioning
follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.8.0] - 2026-06-24

The ML release. Closes the arbitrary-site coverage gap for hosts whose
HTML the heuristic classifier can't induce a clean template from
(Shopify themes, custom-CSS marketing pages, anything with class names
absent from `framework-content-class-hints.json`). The mechanism is
LLM-driven template induction in a background coordinator: novel
templates enqueue an enrichment job, the coordinator drains it,
calls an LLM with a slim DOM skeleton, and persists the induced YAML
into the operator-template root for the file-watching store to pick up.
**Runtime hot path is unchanged. The LLM never blocks a request.**

Per the design (`docs/ml-classifier-v2-design.md`) the pivot from a
per-element ONNX classifier to LLM-driven wrapper induction is the
2026 state of the art: Co-Scraper, AXE, XPath Agent all converge on
this shape. ONNX runtime + Gemma 4 are documented future work pending
upstream `onnxruntime-genai` support; the inducer is shipped today
over Ollama.

### Added

- **`ILlmTextProvider`** (Abstractions) — minimal LLM-completion
  contract reused across the response-parser family (template
  induction now; PII redaction, content-safety verify, regulatory
  disclosure injection later).
- **`DomSkeletonRenderer`** (Core, AOT-clean) — composes the cleaned
  DOM into a 1–4 KB / ~1.5K-token tree-with-exemplars representation:
  per-line `tag.class#id children=N textLen=N linkDensity=0.NN —
  "excerpt"`. Repeated sibling runs collapse to "N repeated tag
  children (3 exemplars below)". Hash-shaped class tokens (Tailwind
  JIT / CSS-modules) filtered. Tunable via `SkeletonRenderOptions`.
- **`LlmTemplateInducer`** (Core) — skeleton + system/user prompts
  + LLM call + YAML parse via the existing
  `YamlOperatorTemplateLoader`. Output shape is exactly the
  operator-template schema (v1.7); a successful induction is a
  hand-editable operator template the hard-override path picks up.
- **`ITemplateEnrichmentQueue`** (Abstractions) +
  **`InMemoryTemplateEnrichmentQueue`** (Core) — bounded channel
  + per-host cooldown dedup + age-out on dequeue. DropNewest on
  capacity; silent drop on cooldown.
- **`TemplateEnrichmentCoordinator`** (Core, `BackgroundService`) —
  drains the queue, calls the inducer, validates the result has
  at least one MainContent rule, writes through
  `OperatorTemplateYamlEmitter` to the operator-template root.
  Skips hosts that already have a hand-authored template (operator
  wins, always). Global QPS throttle via
  `EnrichmentCoordinatorOptions.MinInterCallInterval`.
- **`OperatorTemplateYamlEmitter`** (Core) — canonical YAML emitter
  for `OperatorTemplate`. Inverse of `YamlOperatorTemplateLoader`.
  Lifted from two prior private copies in `TemplateCommand` and
  `OperatorTemplateEndpoints`; closes the "EmitYaml duplicated"
  drift finding from the v1.7.1 architectural review.
- **`StyloExtract.Llm.Ollama`** (new package, opt-in) —
  `OllamaTextProvider` over `/api/chat` (streaming disabled,
  AOT-clean JSON via source generator) + `AddOllamaTextProvider`
  DI helper. Default model `gemma4:e4b-it-qat` per design.
- **`AddStyloExtractLlmInducer`** (AspNetCore) — single DI call wires
  the entire background stack: renderer + queue + coordinator
  HostedService + inducer. Decoupled from the LLM backend (operators
  wire any `ILlmTextProvider` separately).
- **CLI**: `stylo-extract template dump-skeleton --url … | --file …`
  prints the slim representation the LLM would see;
  `stylo-extract template induce --url … --ollama-url … --model …
  [--write]` runs the LLM and prints/persists the YAML.
- **REST**: `POST /api/styloextract/templates/{host}/induce` body
  `{html, url?}` returns `text/yaml`. Same SSRF guard the existing
  `/test` endpoint shipped with (hostname validation, scheme
  allowlist, IP-range denylist, no auto-redirect).
- **Phase 1 + 2 ML training pipeline** (out-of-band Python under
  `training/`) — kept on the shelf for a future ONNX rerank path
  if/when the LLM inducer doesn't suffice. `stylo-extract
  extract-features` CLI command emits the 45-dim feature vector
  per element as JSONL for offline analysis.
- **`MatchStatus.OperatorOverride`** raises a new ephemeral signal
  `StyloExtractSignals.MatchOperatorOverride` (architectural review
  finding from the 2026-06-23 pass — the override branch was
  previously invisible to ephemeral consumers).
- **Real-Ollama integration test** with locally-installed Gemma 4 E2B
  verifies the full chain end-to-end:
  DomCleaner → DomSkeletonRenderer → OllamaTextProvider (HTTP) →
  LlmTemplateInducer YAML extraction → YamlOperatorTemplateLoader.
  On the Acme product fixture the model produces a clean 4-role
  template in ~10 s on M5 CPU. SkippableFact-gated for CI without
  Ollama.

### Operator install (4 DI calls)

```csharp
services.AddStyloExtract();
services.AddStyloExtractOperatorTemplates("config/templates");
services.AddOllamaTextProvider(o => o.Model = "gemma4:12b-it-qat");
services.AddStyloExtractLlmInducer("config/templates");
```

### Changed

- `LayoutExtractor` constructor takes two new OPTIONAL parameters
  (`ITemplateEnrichmentQueue`, `DomSkeletonRenderer`). Consumers
  that don't wire the LLM stack see no behavioural change; the
  skeleton renderer is lazily allocated only when a queue is present.

### Compatibility

Backwards-compatible. Operators who don't add the LLM helper see
v1.7.1 behaviour exactly: heuristic classifier + induced templates +
hand-authored operator templates. The Ollama package is opt-in.

### Suite

475 tests across 10 projects, all green. Includes the new
`StyloExtract.Ml.Tests` (20), `StyloExtract.Llm.Ollama` provider
tests (5), DI-composition tests (3), live-Ollama integration tests
(2), and a SkippableFact end-to-end against actual Gemma 4 E2B.

## [1.7.1] - 2026-06-23

### Fixed

- `DomMarkdownWalker.AppendEscapedInline` no longer preserves leading
  whitespace at line-start. Previously, consecutive text-node visits in
  heavily-indented source HTML (Tailwind / HTMX / framework-generated
  markup) each emitted a single space and accumulated to 4+ spaces at
  the head of the next paragraph or link. CommonMark's indented-code-block
  rule then parsed the entire line as code, so `[text](href)` rendered
  as raw bracket characters instead of a clickable link. The fix primes
  the whitespace-collapse state to "already at whitespace" when
  the output is at line-start, so leading source whitespace is skipped
  entirely; inner-paragraph runs still collapse to a single space.
- Live reproducer for the fix: lucidVIEW loading the mostlylucid.net
  homepage (HTMX-driven blog index). Before 1.7.1 every blog-post card
  after the first collapsed into a code block with raw `[title](/url)`
  text; after 1.7.1 each card renders as a styled link.

## [1.7.0] - 2026-06-23

Structured markdown output. Previously every classified block flattened
to `element.TextContent.Trim()` and the renderer emitted a wall of plain
paragraphs with `# ` collapsing all six heading levels. This release makes
`ExtractedBlock.Markdown` carry a real GFM rendition produced by walking
the block's DOM subtree.

### Added

- `DomMarkdownWalker` (internal to `StyloExtract.Heuristics`) walks each
  content block and emits structured markdown with heading levels (H1-H6
  to one-through-six `#` characters), inline links, emphasis, inline code,
  inline images, `<br>` hard breaks, lists, fenced code blocks, blockquotes,
  figures, and GFM tables. AOT-compatible, allocates a single StringBuilder
  per block.
- GFM table reconstruction via the WHATWG "forming a table" slot-grid
  algorithm: respects `rowspan`/`colspan` (capped at 1000), detects
  complexity (multi-row `<thead>`, nested `<table>`, block content in a
  cell) and falls back to raw HTML (which CommonMark passes through), and
  emits caption, alignment markers (from `align` attribute or
  `style="text-align"`, majority-vote per column), and escaped pipes. Path
  matches the industry consensus across cmark-gfm, Joplin
  turndown-plugin-gfm, Pandoc, and JohannesKaufmann/html-to-markdown.
- BenchmarkDotNet harness at `tests/StyloExtract.Performance.Benchmarks/`
  with walker-only and full-pipeline scenarios across four real fixtures
  (small / medium / large article + table-heavy). Includes a `--dump`
  smoke runner that prints the rendered markdown so quality issues
  surface before they hit production output.

### Changed

- `HeuristicBlockClassifier` and `ExtractorApplicator` now populate
  `ExtractedBlock.Markdown` for content roles (MainContent, Article,
  RepeatedItem, Summary, Heading, Table, CodeBlock, Sidebar, RelatedLinks).
- `TypedMarkdownRenderer` (via `BlockRoleRenderers`) prefers
  `block.Markdown` when non-empty and falls back to the legacy role-
  specific projection otherwise. Navigation, breadcrumb, footer, and form
  blocks keep their existing projections.
- Sidebar and RelatedLinks roles now render via the DOM walker. The
  classic "on this page" TOC pattern (`<aside class="toc"><ul><li><a>`)
  was flattening to indented plain text; it now renders as a proper
  markdown list with anchor links.
- Multi-paragraph blockquotes follow the GFM `> body\n>\n> body`
  convention instead of running paragraphs together.

### Performance

Walker times and allocations on Apple M5 / .NET 10, measured against
v1.6.2's flat-text path:

  | Scenario      | Time          | Allocation    |
  |---------------|---------------|---------------|
  | Small article |   1.3 us      |   8 KB        |
  | Medium doc    |  25.2 us      |  72 KB        |
  | Large doc     |  34.1 us      | 114 KB        |
  | Table-heavy   |  69.2 us      | 165 KB        |

Walker share of `ExtractAsync` total time fell from 25-55% to 5-11%
across the four scenarios. `ExtractAsync` continues to sit well under the
spec's 15ms p99 budget on a cache hit. Mechanics: inline helpers thread a
destination `StringBuilder` parameter so per-cell, per-list-item, and
per-blockquote-line sub-walkers reuse one scratch buffer; per-text-node
escape streams directly into the destination instead of round-tripping
through a transient StringBuilder; alignment vote-counting moves from a
per-column `Dictionary<ColAlign, int>` to a single `int[]` keyed by
`col * 4 + alignment`.

### Tests

- 51 unit tests for `DomMarkdownWalker` covering inline composition,
  block spacing, lists, fenced code, GFM tables (caption, colspan,
  rowspan, alignment, escaped pipes, th-first-row, block-content-in-cell
  HTML fallback, nested-table HTML fallback, multi-row-thead HTML
  fallback), figures, and script/style stripping.
- 5 classifier-level tests for the markdown-population gate by role.
- 6 renderer-level tests for the prefers-block.Markdown branch.
- 2 applicator-level tests for the markdown gate.
- 4 end-to-end pipeline tests asserting the spec's headline gaps
  (heading levels, inline links, lists, GFM tables) survive parse →
  clean → segment → classify → render → SQLite.

Suite totals: 329 tests across 7 projects, all green.

### Compatibility

Backwards-compatible. Consumers reading `ExtractedBlock.Text` continue
to receive the flattened plain-text projection unchanged; the new
markdown rendition is read via `ExtractedBlock.Markdown`. Existing
profiles (`MainContentOnly`, `RagFull`, `AgentNavigation`, `DebugFull`)
behave identically; the only observable change is that the markdown
emitted by `TypedMarkdownRenderer` is now reader-grade rather than flat
prose.

## [1.6.2] - 2026-06-22

- Removed `Mostlylucid.StyloExtract.StyloBot`; the StyloBot action-policy
  bridge has moved to the stylobot repo as
  `Mostlylucid.BotDetection.StyloExtract` to eliminate the assembly-
  version mismatch that caused `FileNotFoundException` at runtime when
  StyloBot was source-built locally.

## [1.6.1] - earlier

- See git tag `v1.6.1` for the predecessor release.
