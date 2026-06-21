# Mostlylucid.StyloExtract.Templates.Postgres

PostgreSQL-backed template index for StyloExtract. Implements the same `ITemplateIndex` contract as `Mostlylucid.StyloExtract.Templates` (the SQLite provider); swap providers via DI with no change to calling code.

## When to use this instead of SQLite

Choose the Postgres provider when:
- Your deployment already runs PostgreSQL as its operational database (StyloBot commercial, multi-tenant SaaS)
- You need multiple extraction nodes sharing one template store (Npgsql pools connections; Postgres serialises concurrent writes natively)
- You plan to add pgvector cosine-similarity search in a future upgrade (the schema is forward-compatible)

The SQLite provider (`Mostlylucid.StyloExtract.Templates`) is the right choice for single-host or air-gapped deployments, CLI tools, and anywhere you want zero external dependencies.

## Installation

```
dotnet add package Mostlylucid.StyloExtract.Templates.Postgres
```

## Usage

```csharp
// Register the Postgres provider. Call this instead of (or after) AddStyloExtract()
// to replace the SQLite ITemplateIndex with the Postgres one.
services.AddStyloExtractPostgres(o =>
    o.ConnectionString = "Host=localhost;Port=5432;Database=styloextract;Username=se;Password=secret");

// Optional: register drift-triggered refit support (mirrors RefitOrchestrator for SQLite).
services.AddStyloExtractPostgresRefit(
    driftRefitThreshold: 0.35,
    observationsBeforeStable: 5,
    versionHistoryDepth: 3);
```

Schema is applied idempotently on the first operation (`CREATE TABLE IF NOT EXISTS`). No migration tool required.

## Storage model

| Table | Contents |
|---|---|
| `templates` | Template id (bytea), host hash, fingerprint, extractor JSON blob, version, observation count |
| `template_lsh_band_index` | LSH bucket rows for fast-path lookup |
| `template_observations` | Per-request observation vectors (bounded to last 100 per template) |
| `template_version_history` | Past extractor versions retained for diff generation |

Columns that are `BLOB` in SQLite are `bytea` in Postgres. Timestamps are `bigint` Unix milliseconds. No pgvector dependency in v1; vector similarity uses the same CPU-side cosine math as the SQLite provider.

## AOT

This package sets `IsAotCompatible=false` because Npgsql requires runtime reflection for connection-string parsing. It will not break AOT builds in packages that do not reference it (sibling packages such as `StyloExtract.Playwright` remain AOT-safe).

---

[Full documentation and package family](https://github.com/scottgal/styloextract)
