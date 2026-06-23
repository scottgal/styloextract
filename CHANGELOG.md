# Changelog

All notable changes to StyloExtract are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versioning
follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
