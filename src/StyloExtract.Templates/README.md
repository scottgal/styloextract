# Mostlylucid.StyloExtract.Templates

SQLite-backed template store for StyloExtract, using `mostlylucid.ephemeral.sqlite.singlewriter` for write serialisation.

## What this package is

Provides persistence for learned page layout templates:

- `SqliteTemplateIndex` (implements `ITemplateIndex`) - stores template fingerprints, extractor centroids, observation clouds, and version history in SQLite
- `SqliteSchema` - schema creation and migration helpers
- `HostHasher` - HMAC-based host name hashing so raw hostnames are never stored
- `TemplateExporter` / `TemplateImporter` - JSON serialisation of template bundles for cross-environment migration
- `RefitOrchestrator` - manages the drift-detection and centroid-refit cycle

### Storage model

| Table | Contents |
|---|---|
| `templates` | Template id, host hash, fingerprint hex, extractor JSON, version, observation count |
| `lsh_bands` | LSH bucket rows for fast-path lookup |
| `observations` | Per-request observation vectors (bounded by `ObservationCloudSize`) |
| `version_history` | Past extractor versions retained for diff generation |

All writes go through `SqliteSingleWriter` to avoid WAL contention on high-throughput workloads.

## When to depend on this directly

Consumed transitively by `Mostlylucid.StyloExtract.AspNetCore`. Take a direct dependency when you need `TemplateExporter`, `TemplateImporter`, or `SqliteSchema` for standalone migration tooling or tests that exercise the store directly.

## Usage

```csharp
// Standard wiring (handled by AddStyloExtract)
services.AddSingleton<ITemplateIndex>(sp => new SqliteTemplateIndex(
    "Data Source=styloextract-templates.db",
    agingLambdaObs: 0.02, agingLambdaRecent: 0.05, agingTauDays: 30,
    sp.GetRequiredService<TypedSignalSink<StyloExtractSignal>>()));
```

```csharp
// Export templates for a host
using var conn = new SqliteConnection("Data Source=prod.db");
conn.Open();
SqliteSchema.EnsureCreated(conn);
var hasher = HostHasher.FromConfiguredKeyOrRandom(myKey);
await TemplateExporter.ExportHostAsync(conn, hasher.Hash("example.com"), "example.com", outputStream, ct);
```

## AOT

This package is `IsAotCompatible=true`. `Microsoft.Data.Sqlite` is AOT-safe on .NET 10.

---

[Full documentation and package family](https://github.com/scottgal/styloextract)
