# Operator-edited templates: design

**Status**: design, not yet implemented.
**Confirmed shape**: hard host override, YAML authority + SQLite cache, surfaces = CLI + REST + test/preview tool.

## Why

Today every template is induced from observation. When the classifier gets it
wrong on a specific host — say it misses an article body that uses an unusual
wrapper class — the operator has no way to fix it short of patching code.
This adds a per-host override surface so an operator can ship a YAML file that
the runtime treats as the ground-truth extraction template for that host.

## Storage layer

YAML on disk is the source of truth. SQLite holds a parsed cache for fast
lookup. Edit the YAML, the runtime reloads, the cache is rebuilt. CLI never
writes SQLite directly; it writes YAML and asks the runtime to reload.

**On-disk layout** (root configurable via `StyloExtract:OperatorTemplates:Root`,
default `config/templates/`):

    config/templates/
      example.com.yaml
      docs.example.com.yaml
      another-host.com.yaml

**One file per host.** The filename's stem is the host the template applies
to (exact match; subdomain matching is a separate concern handled by separate
files).

**Schema** (each file is one `OperatorTemplate`):

```yaml
host: example.com
description: Hand-tuned docs template — corrects the classifier's miss on the
             custom .docs-body wrapper.
version: 1
rules:
  - role: MainContent
    selectors:
      - main.docs-body
      - article.markdown-body
    confidence: 0.95
  - role: Heading
    selectors:
      - .docs-body h1.page-title
    confidence: 0.92
  - role: PrimaryNavigation
    selectors:
      - nav.sidebar-toc
    confidence: 0.85
  - role: Boilerplate
    selectors:
      - footer.site-footer
      - .cookie-banner
    confidence: 0.9
```

`role` is a `BlockRole` enum value (parsed case-insensitively).
`selectors` is a list of CSS selectors; ordered, first-match-wins per role.
`confidence` is 0.0-1.0; surfaces in `ExtractedBlock.Confidence`.

## Runtime flow

`LayoutExtractor.ExtractAsync` resolves the host (from `sourceUri.Host` or
`options.HostOverride`). Before any fingerprinting or template-index lookup,
consult `IOperatorTemplateStore.GetForHost(host)`:

- **Hit**: convert the `OperatorTemplate` into a synthetic `LearnedExtractor`,
  apply via the existing `ExtractorApplicator`, return. No fingerprint, no
  classifier, no induction. The result's `MatchStatus` is `OperatorOverride`
  (new enum value).
- **Miss**: fall through to the existing fast-path / slow-path / novel-
  ephemeral flow unchanged.

This is the **hard override** semantics confirmed in design: the operator
template fully replaces the induction path for that host.

## Components

| Layer | Type | Lives in |
|---|---|---|
| Contract | `OperatorTemplate` record + `OperatorTemplateRule` record | StyloExtract.Abstractions |
| Contract | `IOperatorTemplateStore` interface | StyloExtract.Abstractions |
| YAML parser | `YamlOperatorTemplateLoader` | StyloExtract.Core (new file) |
| File-watched authority | `YamlFileOperatorTemplateStore` | StyloExtract.Core |
| SQLite cache | `SqliteOperatorTemplateCache` + schema | StyloExtract.Templates |
| Wiring | `LayoutExtractor` consults store first | StyloExtract.Core |
| CLI commands | `template add/list/remove/show/test` | StyloExtract.Cli.Shared |
| REST endpoints | `MapOperatorTemplateEndpoints()` | StyloExtract.AspNetCore |

`MatchStatus` gets a new value: `OperatorOverride`.

## YAML parser choice

`YamlDotNet` is the most popular .NET YAML library but uses reflection
heavily — not AOT-clean as-is. Options:

1. **Hand-written micro-parser**: the schema is small (host, description,
   version, rules → role + selectors + confidence). Maybe 80 lines, fully
   AOT-clean. Best choice for v1.
2. **`YamlDotNet` with explicit type resolvers**: works in AOT but every
   record needs annotation. More code than option 1.

Going with option 1 unless the schema balloons.

## CLI surface

```
stylo-extract template list
  → table of (host, rules, file path, last modified)

stylo-extract template show example.com
  → prints the YAML

stylo-extract template add example.com --role MainContent --selector "main.docs-body" --confidence 0.95
  → appends a rule to example.com.yaml (creates if missing)

stylo-extract template remove example.com [--rule-index N]
  → removes one rule or the whole file

stylo-extract template test --url https://example.com/some-page
  → fetches the URL, runs extraction with the operator template,
    dumps the resulting markdown. Use --no-override to compare against
    the induced path.
```

## REST surface

`StyloExtract.AspNetCore` exposes:

```
GET    /api/styloextract/templates           list
GET    /api/styloextract/templates/{host}    show
PUT    /api/styloextract/templates/{host}    upsert (body is YAML)
DELETE /api/styloextract/templates/{host}    remove
POST   /api/styloextract/templates/{host}/test  body is { url } or { html }
```

Off by default. Operators opt in via `MapOperatorTemplateEndpoints()`. Auth
is the host app's concern (not in scope here).

## Test/preview tool

Both CLI (`stylo-extract template test --url ...`) and REST
(`POST /api/styloextract/templates/{host}/test`) share the same internal
helper: take HTML + URL + template, run extraction, return the markdown.
The CLI fetches the URL via `HttpClient`; the REST endpoint accepts the HTML
in the body to keep the gateway in control of network egress.

## File-watcher behaviour

`YamlFileOperatorTemplateStore` watches the root via `IFileProvider` /
`FileSystemWatcher`. On change:

1. Parse the changed YAML.
2. On success, atomically swap the in-memory map and update SQLite cache.
3. On parse failure, log a warning and KEEP the previous in-memory entry.
   A broken edit must never leave the runtime in a no-template state.

## Tests

- YAML parser unit tests (happy path + every error mode).
- `IOperatorTemplateStore` contract tests (add, lookup, remove, watch).
- Integration test: `LayoutExtractor.ExtractAsync` with an operator template
  bypasses the classifier and emits markdown matching the override.
- Integration test: missing host falls through to the induced path.
- CLI snapshot tests (golden output for add/list/show).
- REST endpoint tests (WebApplicationFactory).

## Out of scope for v1

- Subdomain wildcards (`*.example.com`)
- Per-path overrides within a host
- Operator-template export/import (it's a directory of YAML — already that)
- Versioning beyond a single `version: N` integer
- Conflict resolution if two YAML files claim the same host
