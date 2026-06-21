# StyloExtract

Layout-fingerprint matching with template-keyed extractor reuse for .NET 10.

[![NuGet](https://img.shields.io/nuget/v/Mostlylucid.StyloExtract.Core)](https://www.nuget.org/packages/Mostlylucid.StyloExtract.Core)
[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](https://unlicense.org)
[![CI](https://github.com/scottgal/styloextract/actions/workflows/ci.yml/badge.svg)](https://github.com/scottgal/styloextract/actions/workflows/ci.yml)

---

## What StyloExtract is (and is not)

StyloExtract is not an HTML-to-Markdown converter. Generic tools like Trafilatura, Mercury, and Readability parse each page in isolation: they apply heuristics, discard boilerplate, and return text. That works, but it throws away something valuable: the fact that the same site usually produces the same page *shape* over thousands of requests.

StyloExtract treats page structure as a fingerprint. The first time it sees a layout it induces an extractor (a set of CSS selectors and block roles) directly from the DOM. Every subsequent page that matches the same layout fingerprint reuses that extractor rather than re-running heuristics from scratch. The extractor is a learned centroid that drifts and refits as the site evolves. When the centroid drift crosses a threshold, the refit is recorded as a version event, giving you site-template-version monitoring as a side effect of extraction.

Production extraction tools do not do same-template clustering. StyloExtract fills that gap. It is built for RAG pipelines, content monitoring, and any scenario where you hit the same site repeatedly and want consistent, low-latency extraction with observable version history.

---

## What it does

- Fast-path LSH match under 1ms for known templates (measured: ProbeFastPath = 16 µs)
- Slow-path pq-gram cosine fallback for structurally similar but previously unseen layouts
- Novel template induction: first encounter induces an extractor from the DOM automatically
- Centroid-style learned extractors that drift and refit as site templates evolve
- Refit-as-version-event: template version changes emit structured `TemplateVersionDiff` events
- JSON export/import for portable template bundles (migrate templates across environments)
- Site-template-version monitoring via `stylo-extract monitor` with webhook delivery
- StyloFlow signal integration: 11 extraction signals flow into any `TypedSignalSink` consumer
- AOT-compatible core stack (8 packages); Playwright is the explicit opt-in non-AOT path

---

## Quick start

```bash
dotnet add package Mostlylucid.StyloExtract.AspNetCore
```

```csharp
// Program.cs
builder.Services.AddStyloExtract(o =>
{
    o.StorePath = "styloextract-templates.db"; // SQLite store (created on first run)
    o.HostHashKey = null;                       // set a base64 HMAC key for cross-process stability
    o.DefaultProfile = ExtractionProfile.RagFull;
});
```

```csharp
// Inject ILayoutExtractor wherever you need it
var result = await extractor.ExtractAsync(html, new Uri("https://example.com/article"));

Console.WriteLine(result.Markdown);                    // extracted content as Markdown
Console.WriteLine(result.Match.Status);                // FastPathHit | SlowPathMatch | Novel | Refit
Console.WriteLine(result.Match.TemplateId);            // Guid of the matched or inducted template
Console.WriteLine(result.Match.TemplateVersion);       // version counter; increments on refit
```

---

## CLI usage

Install as a .NET global tool or run directly from the built binary:

```bash
dotnet tool install -g Mostlylucid.StyloExtract.Cli   # once available on NuGet
# or build from source:
dotnet run --project src/StyloExtract.Cli -- <subcommand> [options]
```

### Subcommands

| Subcommand | Description |
|---|---|
| `extract` | Extract a single page (file or URL) to Markdown or JSON |
| `install-browsers` | Download Playwright browser binaries |
| `export` | Export a host's learned templates to a portable JSON bundle |
| `import` | Import a JSON template bundle into a store |
| `monitor` | Watch a list of URLs and emit NDJSON version events to stdout |

### Options reference

**`extract <source>`**

| Option | Default | Description |
|---|---|---|
| `<source>` | (required) | Path to an HTML file or an `https://` URL |
| `--profile` | `RagFull` | Extraction profile: `RagFull`, `Title`, `Minimal` |
| `--json` | false | Output JSON instead of Markdown |
| `--store` | `styloextract-templates.db` | Path to the SQLite template store |
| `--host-hash-key` | (none) | Base64 HMAC key for host hashing; required for cross-process template matching |
| `--rendered` / `-r` | false | Fetch via Playwright for JS-rendered pages (auto-installs Chromium on first use) |

**`install-browsers`**

| Option | Default | Description |
|---|---|---|
| `--browser` | `chromium` | Browser to install: `chromium`, `firefox`, `webkit` |

**`export`**

| Option | Required | Description |
|---|---|---|
| `--store <path>` | yes | Path to the SQLite store |
| `--host <name>` | yes | Host display name (e.g. `example.com`) |
| `--out <file>` | yes | Output JSON file path |
| `--host-hash-key <key>` | no | HMAC key for host hashing; must match the key used during import for templates to resolve correctly |

**`import`**

| Option | Required | Description |
|---|---|---|
| `--store <path>` | yes | Path to the SQLite store |
| `--host <name>` | yes | Host display name (e.g. `example.com`) |
| `--in <file>` | yes | Input JSON file path |
| `--host-hash-key <key>` | no | HMAC key; must match the key used during export |

**`monitor`**

| Option | Required | Description |
|---|---|---|
| `--urls <file>` | yes | Path to a newline-delimited file of URLs to watch |
| `--store <path>` | yes | Path to the SQLite template store |
| `--interval <hh:mm:ss>` | no (default `01:00:00`) | Poll interval; press Ctrl-C to stop |
| `--host-hash-key <key>` | no | HMAC key for cross-process template matching |
| `--webhook <url>` | no | URL to POST each NDJSON event to |
| `--pretty` | false | Write indented JSON (one event per call) instead of compact NDJSON |

### Worked examples

```bash
# Extract a local file to Markdown
stylo-extract extract article.html

# Fetch a URL, output JSON, store templates in a named DB
stylo-extract extract https://example.com/blog/post \
  --json \
  --store /var/lib/styloextract/prod.db \
  --host-hash-key "$(cat /etc/styloextract/hmac.key)"

# Fetch a JS-rendered page (auto-installs Chromium if needed)
stylo-extract extract https://spa-example.com/page --rendered

# Export templates for example.com to a JSON bundle for migration
stylo-extract export \
  --store prod.db \
  --host example.com \
  --out example-com-templates.json \
  --host-hash-key "$(cat /etc/styloextract/hmac.key)"

# Import templates into a new environment
stylo-extract import \
  --store new-env.db \
  --host example.com \
  --in example-com-templates.json \
  --host-hash-key "$(cat /etc/styloextract/hmac.key)"

# Monitor a list of URLs, emit version events, send to a webhook
stylo-extract monitor \
  --urls urls.txt \
  --store monitor.db \
  --interval 00:30:00 \
  --webhook https://hooks.example.com/styloextract \
  --host-hash-key "$(cat /etc/styloextract/hmac.key)"
```

**`--host-hash-key` note:** The host dimension of the template store is keyed by an HMAC hash of the hostname. If you omit `--host-hash-key`, a random key is generated at startup and discarded on exit. Templates stored without a key will not be found in a subsequent process invocation. Pass the same stable key to every process that needs to share templates from the same store.

For the full CLI reference including exit codes and edge cases, see [`docs/cli.md`](docs/cli.md).

---

## Package family

| Package | Purpose |
|---|---|
| `Mostlylucid.StyloExtract.Abstractions` | Interfaces, records, signal catalog (zero runtime deps) |
| `Mostlylucid.StyloExtract.Html` | AngleSharp DOM parser and cleaner |
| `Mostlylucid.StyloExtract.Fingerprint` | MinHash, pq-grams, LSH, anchor-path fingerprinting |
| `Mostlylucid.StyloExtract.Templates` | SQLite template store via ephemeral `SqliteSingleWriter` |
| `Mostlylucid.StyloExtract.Heuristics` | YAML-driven block classifier and extractor inducer/applicator |
| `Mostlylucid.StyloExtract.Markdown` | Profile-aware Markdown renderer |
| `Mostlylucid.StyloExtract.Core` | Orchestration: `ILayoutExtractor.ExtractAsync` |
| `Mostlylucid.StyloExtract.AspNetCore` | `AddStyloExtract()` DI extensions |
| `Mostlylucid.StyloExtract.Playwright` | **Optional, non-AOT.** Rendered-DOM fetcher for SPA pages |

Most consumers need only `Mostlylucid.StyloExtract.AspNetCore`, which pulls in Core, Html, Fingerprint, Templates, Heuristics, and Markdown transitively. Add `Mostlylucid.StyloExtract.Playwright` explicitly only when you need JS-rendered HTML fetching.

---

## AOT compatibility

The eight core packages (`Abstractions`, `Html`, `Fingerprint`, `Templates`, `Heuristics`, `Markdown`, `Core`, `AspNetCore`) are marked `IsAotCompatible=true` and are verified by the CI AOT canary step on every push to `main`. No reflection-based code paths exist in any of them.

`Mostlylucid.StyloExtract.Playwright` is the explicit opt-in non-AOT path. Playwright uses reflection extensively. Keeping it in a separate package means the core stack remains fully AOT-publishable for gateway and sidecar scenarios; only the Playwright package brings in the restriction.

---

## StyloFlow signal integration

`Mostlylucid.StyloExtract.Abstractions` defines `StyloExtractSignals` (string key constants) and `StyloExtractSignal` (the typed payload). `TypedSignalSink<StyloExtractSignal>` is wired into the orchestration, so extraction events flow naturally into any StyloFlow consumer that subscribes to `TypedSignalRaised`.

| Signal | Key | Description |
|---|---|---|
| ParseDone | `stylo.extract.parse.done` | DOM parse and clean completed |
| FingerprintComputed | `stylo.extract.fingerprint.computed` | MinHash + LSH computed |
| MatchFastPathHit | `stylo.extract.match.fastpath.hit` | LSH bucket lookup matched a known template |
| MatchFastPathMiss | `stylo.extract.match.fastpath.miss` | LSH miss; escalating to slow path |
| MatchSlowPathMatch | `stylo.extract.match.slowpath.match` | pq-gram cosine matched above threshold |
| MatchSlowPathMiss | `stylo.extract.match.slowpath.miss` | No match; novel template |
| TemplateNovel | `stylo.extract.template.novel` | New template inducted and stored |
| TemplateRefit | `stylo.extract.template.refit` | Existing template centroid drifted and refitted |
| ObservationRecorded | `stylo.extract.observation.recorded` | Observation added to cloud |
| DriftObserved | `stylo.extract.drift.observed` | Cosine drift above threshold detected |
| VersionDetected | `stylo.extract.version.detected` | Site template version change confirmed |

---

## Performance

Targets (from design spec §13):

| Path | Target |
|---|---|
| Fast-path LSH match | < 1 ms |
| Full extract, cache-hit template | < 15 ms |
| Full extract, novel template | < 50 ms |

Measured (ProbeFastPath benchmark, in-process warm cache):

```
ProbeFastPath  16 µs
```

The benchmark project has a `--regression` mode for CI gates:

```bash
dotnet run --project bench/StyloExtract.Benchmarks -c Release -- --regression
```

---

## Configuration

`AddStyloExtract` accepts a configuration action:

```csharp
builder.Services.AddStyloExtract(o =>
{
    // Storage
    o.StorePath    = "styloextract-templates.db"; // SQLite file path
    o.HostHashKey  = null;                         // base64 HMAC key; null = random per process

    // Extraction
    o.DefaultProfile = ExtractionProfile.RagFull;  // RagFull | Title | Minimal

    // Fingerprinting
    o.Fingerprint.MinHashSize    = 128;   // MinHash signature size
    o.Fingerprint.LshBands       = 16;   // LSH band count
    o.Fingerprint.LshRowsPerBand = 8;    // rows per band
    o.Fingerprint.ShingleWidth   = 3;    // DOM shingle width
    o.Fingerprint.AnchorWeight   = 0.4;  // weight of anchor-path dimension vs shingle dimension

    // Matching
    o.Match.FastPathJaccardThreshold = 0.85; // LSH similarity gate
    o.Match.SlowPathCosineThreshold  = 0.75; // pq-gram cosine gate
    o.Match.AgingLambdaObs           = 0.02; // observation aging rate
    o.Match.AgingLambdaRecent        = 0.05; // recency aging rate
    o.Match.AgingTauDays             = 30;   // aging half-life in days

    // Centroid learning
    o.Centroid.DriftRefitThreshold      = 0.35; // cosine drift that triggers a refit
    o.Centroid.ObservationsBeforeStable = 5;    // observations required before centroid is stable
    o.Centroid.ObservationCloudSize     = 100;  // max observations retained per template
    o.Centroid.VersionHistoryDepth      = 3;    // number of past versions retained
});
```

---

## Status

v1.0 - actively developed. APIs may shift before 1.0 release. Built for eventual integration with [StyloBot](https://github.com/scottgal/stylobot).

---

## Contributing

Issues and PRs welcome at [github.com/scottgal/styloextract/issues](https://github.com/scottgal/styloextract/issues).

## License

[Unlicense](LICENSE) - public domain.
