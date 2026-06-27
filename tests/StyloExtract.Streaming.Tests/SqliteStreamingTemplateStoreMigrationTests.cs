using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace StyloExtract.Streaming.Tests;

/// <summary>
/// alpha.21 follow-up — verifies the SqliteStreamingTemplateStore's schema
/// auto-migration from the alpha.16–alpha.20 shape (single-column PK on
/// <c>template_id</c>) to the alpha.21 composite PK on <c>(host, version)</c>,
/// plus alpha.22's <c>PRAGMA user_version</c> algorithm-compat gate.
///
/// The alpha.22 gate intentionally drops every row in any DB whose
/// <c>user_version</c> is below <c>CurrentStoreVersion</c>. Old MinHash
/// signatures sketched under prior scanner-side scoring rules (alpha.20's
/// frequency shingles, alpha.21's Markov bigrams without the user_version
/// stamp) can never match the alpha.22+ scanner's output for the same byte
/// stream — they always Bailout. Preserving them is functionally dead weight
/// and worse, consumers that don't auto-reinduct on Bailout stay stuck
/// forever pointing at a template that will never accept any response.
///
/// Hence: schema migration runs first (alpha.21 behaviour, unchanged) → rows
/// are temporarily preserved as version 1 → the user_version gate runs
/// inside the same connection → rows get dropped because the seed's
/// user_version was 0 → user_version stamped to current.
/// </summary>
public sealed class SqliteStreamingTemplateStoreMigrationTests
{
    /// <summary>
    /// alpha.22: opening an alpha.20-shaped DB still runs the schema migration
    /// (composite PK), but the alpha.22 user_version gate then drops every
    /// preserved row because their MinHash signatures were sketched under the
    /// alpha.20 shingling algorithm and can never match the current scanner.
    /// </summary>
    [Fact]
    public async Task OpeningAlpha20Db_RunsSchemaMigration_ThenDropsRows_StampsUserVersion()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"sx-migration-{Guid.NewGuid():N}.db");
        try
        {
            // 1. Build an alpha.20-shaped DB by hand: verbatim alpha.20 CREATE TABLE
            //    (single-column PK on template_id, host column added by alpha.17
            //    migration) + insert rows whose JSON blobs match the alpha.20 shape
            //    — i.e. TemplateFence carries TagAllowlistBloom and StreamingTemplate
            //    carries MinContentDepth. user_version is left at its default 0.
            var seeded = new[]
            {
                (Host: "alpha20-a.example", TemplateId: Guid.NewGuid()),
                (Host: "alpha20-b.example", TemplateId: Guid.NewGuid()),
                (Host: "alpha20-c.example", TemplateId: Guid.NewGuid()),
            };

            await using (var conn = new SqliteConnection($"Data Source={tempDb}"))
            {
                await conn.OpenAsync();

                // Verbatim alpha.20 CREATE TABLE — taken from
                // `git show HEAD~1:src/StyloExtract.Streaming/SqliteStreamingTemplateStore.cs`.
                await using (var create = conn.CreateCommand())
                {
                    create.CommandText = """
                        CREATE TABLE streaming_templates (
                            template_id BLOB PRIMARY KEY NOT NULL,
                            template_blob BLOB NOT NULL,
                            created_at INTEGER NOT NULL
                        );
                        """;
                    await create.ExecuteNonQueryAsync();
                }

                // alpha.17 follow-on: ADD COLUMN host
                await using (var addHost = conn.CreateCommand())
                {
                    addHost.CommandText =
                        "ALTER TABLE streaming_templates ADD COLUMN host TEXT NOT NULL DEFAULT '';";
                    await addHost.ExecuteNonQueryAsync();
                }

                // alpha.17 follow-on: helper index on host
                await using (var idx = conn.CreateCommand())
                {
                    idx.CommandText =
                        "CREATE INDEX idx_streaming_templates_host ON streaming_templates(host);";
                    await idx.ExecuteNonQueryAsync();
                }

                // Insert the seed rows with alpha.20-shaped JSON blobs.
                foreach (var (host, id) in seeded)
                {
                    var blob = BuildAlpha20JsonBlob(id, host);
                    await using var insert = conn.CreateCommand();
                    insert.CommandText =
                        "INSERT INTO streaming_templates(template_id, template_blob, host, created_at) " +
                        "VALUES (@id, @blob, @host, @now)";
                    insert.Parameters.AddWithValue("@id", id.ToByteArray());
                    insert.Parameters.AddWithValue("@blob", blob);
                    insert.Parameters.AddWithValue("@host", host);
                    insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await insert.ExecuteNonQueryAsync();
                }
            }

            // 2. Open the same path via the alpha.22 SqliteStreamingTemplateStore.
            //    The ctor calls EnsureSchema which runs the alpha.21 schema
            //    migration first (still unchanged), then the alpha.22 user_version
            //    gate which sees user_version=0 < CurrentStoreVersion=2 and drops
            //    every preserved row.
            await using (var store = new SqliteStreamingTemplateStore($"Data Source={tempDb}"))
            {
                // 3. Every seeded row must be GONE. Old MinHash signatures sketched
                //    under the alpha.20 shingling algorithm can't match the alpha.22
                //    scanner; preserving them would mean the consumer stays stuck on
                //    a template that always Bailouts.
                foreach (var (host, _) in seeded)
                {
                    (await store.GetByHostAsync(host)).Should().BeNull(
                        $"host {host} must not survive the alpha.22 user_version gate");
                    (await store.ListVersionsByHostAsync(host)).Should().BeEmpty(
                        $"version chain for host {host} must be empty after drop");
                }
            }

            // 4. Schema is still the alpha.21 composite-PK shape, and user_version
            //    is now stamped to CurrentStoreVersion (= 2 at alpha.22).
            await using (var conn = new SqliteConnection($"Data Source={tempDb}"))
            {
                await conn.OpenAsync();

                var pkCols = new List<(string Name, int PkOrdinal)>();
                await using (var info = conn.CreateCommand())
                {
                    info.CommandText = "PRAGMA table_info(streaming_templates);";
                    await using var reader = await info.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var name = reader.GetString(1);
                        var pkOrdinal = reader.GetInt32(5);
                        if (pkOrdinal > 0) pkCols.Add((name, pkOrdinal));
                    }
                }
                pkCols.Should().HaveCount(2);
                pkCols.Should().Contain(c => c.Name.Equals("host", StringComparison.OrdinalIgnoreCase));
                pkCols.Should().Contain(c => c.Name.Equals("version", StringComparison.OrdinalIgnoreCase));

                await using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA user_version;";
                var stamped = (long)(await pragma.ExecuteScalarAsync() ?? 0L);
                stamped.Should().Be(CurrentStoreVersion,
                    "alpha.22 store must stamp user_version after schema migration");
            }
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-wal"); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-shm"); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// alpha.22: an alpha.21-shaped DB (composite PK already in place) with
    /// user_version=1 is treated as algorithmically stale — same drop-all
    /// behaviour as the alpha.20 case, just with the schema migration branch
    /// short-circuited. Proves the user_version gate fires on the "schema OK
    /// but algorithm-incompat" path too.
    /// </summary>
    [Fact]
    public async Task OpeningAlpha21Db_WithStaleUserVersion_DropsRows_StampsUserVersion()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"sx-stale-uv-{Guid.NewGuid():N}.db");
        try
        {
            var seeded = new[]
            {
                (Host: "alpha21-a.example", TemplateId: Guid.NewGuid()),
                (Host: "alpha21-b.example", TemplateId: Guid.NewGuid()),
            };

            await using (var conn = new SqliteConnection($"Data Source={tempDb}"))
            {
                await conn.OpenAsync();

                // Alpha.21 schema: composite PK on (host, version).
                await using (var create = conn.CreateCommand())
                {
                    create.CommandText = """
                        CREATE TABLE streaming_templates (
                            host TEXT NOT NULL,
                            version INTEGER NOT NULL,
                            template_id BLOB NOT NULL,
                            template_blob BLOB NOT NULL,
                            created_at INTEGER NOT NULL,
                            PRIMARY KEY (host, version)
                        );
                        CREATE INDEX idx_streaming_templates_host ON streaming_templates(host);
                        CREATE INDEX idx_streaming_templates_template_id ON streaming_templates(template_id);
                        """;
                    await create.ExecuteNonQueryAsync();
                }

                // Simulate "alpha.21 algorithm baseline" — bump user_version to 1.
                // Alpha.22's CurrentStoreVersion is 2 so the gate must still fire.
                await using (var stamp = conn.CreateCommand())
                {
                    stamp.CommandText = "PRAGMA user_version = 1;";
                    await stamp.ExecuteNonQueryAsync();
                }

                foreach (var (host, id) in seeded)
                {
                    var blob = BuildAlpha21JsonBlob(id, host);
                    await using var insert = conn.CreateCommand();
                    insert.CommandText =
                        "INSERT INTO streaming_templates(host, version, template_id, template_blob, created_at) " +
                        "VALUES (@host, 1, @id, @blob, @now)";
                    insert.Parameters.AddWithValue("@host", host);
                    insert.Parameters.AddWithValue("@id", id.ToByteArray());
                    insert.Parameters.AddWithValue("@blob", blob);
                    insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await insert.ExecuteNonQueryAsync();
                }
            }

            await using (var store = new SqliteStreamingTemplateStore($"Data Source={tempDb}"))
            {
                foreach (var (host, _) in seeded)
                {
                    (await store.GetByHostAsync(host)).Should().BeNull(
                        $"host {host} must be dropped when user_version < CurrentStoreVersion");
                }
            }

            await using (var conn = new SqliteConnection($"Data Source={tempDb}"))
            {
                await conn.OpenAsync();
                await using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA user_version;";
                var stamped = (long)(await pragma.ExecuteScalarAsync() ?? 0L);
                stamped.Should().Be(CurrentStoreVersion,
                    "stale-user_version path must stamp user_version after drop");
            }
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-wal"); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-shm"); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// alpha.22: a fresh empty DB has user_version=0; the gate's DELETE is a
    /// no-op (no rows to drop) but the user_version stamp still fires so the
    /// next open short-circuits cleanly.
    /// </summary>
    [Fact]
    public async Task OpeningFreshDb_StampsUserVersion_AndUsesAlpha21Schema()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"sx-fresh-{Guid.NewGuid():N}.db");
        try
        {
            await using (var store = new SqliteStreamingTemplateStore($"Data Source={tempDb}"))
            {
                var template = BuildAlpha21Template("fresh.example", version: 1);
                await store.UpsertAsync(template);
            }

            await using var conn = new SqliteConnection($"Data Source={tempDb}");
            await conn.OpenAsync();

            // Composite PK shape.
            var pkCols = new List<(string Name, int PkOrdinal)>();
            await using (var info = conn.CreateCommand())
            {
                info.CommandText = "PRAGMA table_info(streaming_templates);";
                await using var reader = await info.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(1);
                    var pkOrdinal = reader.GetInt32(5);
                    if (pkOrdinal > 0) pkCols.Add((name, pkOrdinal));
                }
            }
            pkCols.Should().HaveCount(2);
            pkCols.Should().Contain(c => c.Name.Equals("host", StringComparison.OrdinalIgnoreCase));
            pkCols.Should().Contain(c => c.Name.Equals("version", StringComparison.OrdinalIgnoreCase));

            // user_version stamped on first open even for a fresh DB.
            await using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA user_version;";
            var stamped = (long)(await pragma.ExecuteScalarAsync() ?? 0L);
            stamped.Should().Be(CurrentStoreVersion,
                "fresh DB must be stamped with CurrentStoreVersion so the next open short-circuits");
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-wal"); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-shm"); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// alpha.22: a DB stamped with the current user_version must NOT have its
    /// rows touched on open — that would defeat the whole point of the gate.
    /// Proves the "version matches" branch short-circuits cleanly.
    /// </summary>
    [Fact]
    public async Task OpeningDbWithCurrentUserVersion_PreservesRows()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"sx-current-uv-{Guid.NewGuid():N}.db");
        try
        {
            var host = "current.example";

            // 1. Let the alpha.22 store create the DB + stamp user_version,
            //    then upsert a real alpha.22-shaped template.
            await using (var store = new SqliteStreamingTemplateStore($"Data Source={tempDb}"))
            {
                var template = BuildAlpha21Template(host, version: 1);
                await store.UpsertAsync(template);
            }

            // 2. Re-open. user_version is already current, so the gate must
            //    short-circuit and the row must survive.
            await using (var store = new SqliteStreamingTemplateStore($"Data Source={tempDb}"))
            {
                var roundtrip = await store.GetByHostAsync(host);
                roundtrip.Should().NotBeNull(
                    "rows must survive open when user_version == CurrentStoreVersion");
                roundtrip!.Host.Should().Be(host);
                roundtrip.Version.Should().Be(1);
            }
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-wal"); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-shm"); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Task 13 of Phase 1: opening a Task-4 dogfood DB (user_version=3, Task-4
    /// JSON blobs carrying <c>PrefixTripwire</c>/<c>ContentStartTripwire</c>/
    /// <c>ContentEndTripwire</c> as <c>IdentityClaim</c> shapes) must drop
    /// every row on first open. The byte-pattern scanner can't match against
    /// hash-shaped tripwires; preserving them would mean the consumer stays
    /// stuck on a template that always Bailouts.
    /// </summary>
    [Fact]
    public async Task OpeningTask4Db_WithUserVersionThree_DropsRows_StampsToFour()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"sx-task4-uv-{Guid.NewGuid():N}.db");
        try
        {
            var seeded = new[]
            {
                (Host: "task4-a.example", TemplateId: Guid.NewGuid()),
                (Host: "task4-b.example", TemplateId: Guid.NewGuid()),
            };

            await using (var conn = new SqliteConnection($"Data Source={tempDb}"))
            {
                await conn.OpenAsync();
                await using (var create = conn.CreateCommand())
                {
                    create.CommandText = """
                        CREATE TABLE streaming_templates (
                            host TEXT NOT NULL,
                            version INTEGER NOT NULL,
                            template_id BLOB NOT NULL,
                            template_blob BLOB NOT NULL,
                            created_at INTEGER NOT NULL,
                            PRIMARY KEY (host, version)
                        );
                        CREATE INDEX idx_streaming_templates_host ON streaming_templates(host);
                        CREATE INDEX idx_streaming_templates_template_id ON streaming_templates(template_id);
                        """;
                    await create.ExecuteNonQueryAsync();
                }
                await using (var stamp = conn.CreateCommand())
                {
                    stamp.CommandText = "PRAGMA user_version = 3;";
                    await stamp.ExecuteNonQueryAsync();
                }

                foreach (var (host, id) in seeded)
                {
                    var blob = BuildTask4JsonBlob(id, host);
                    await using var insert = conn.CreateCommand();
                    insert.CommandText =
                        "INSERT INTO streaming_templates(host, version, template_id, template_blob, created_at) " +
                        "VALUES (@host, 1, @id, @blob, @now)";
                    insert.Parameters.AddWithValue("@host", host);
                    insert.Parameters.AddWithValue("@id", id.ToByteArray());
                    insert.Parameters.AddWithValue("@blob", blob);
                    insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await insert.ExecuteNonQueryAsync();
                }
            }

            await using (var store = new SqliteStreamingTemplateStore($"Data Source={tempDb}"))
            {
                foreach (var (host, _) in seeded)
                {
                    (await store.GetByHostAsync(host)).Should().BeNull(
                        $"host {host} must drop when user_version=3 (Task 4) < CurrentStoreVersion=4 (Task 13)");
                }
            }

            await using (var conn = new SqliteConnection($"Data Source={tempDb}"))
            {
                await conn.OpenAsync();
                await using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA user_version;";
                var stamped = (long)(await pragma.ExecuteScalarAsync() ?? 0L);
                stamped.Should().Be(CurrentStoreVersion,
                    "Task 13 store must stamp user_version=4 after dropping Task-4 rows");
            }
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-wal"); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-shm"); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Build a JSON blob shaped like a Task-4 (user_version=3) template:
    /// three IdentityClaim tripwires. Task 13 deserialises into a
    /// StreamingTemplate carrying BytePattern shapes; the Task-4 fields
    /// would fail to populate the required new fields, so the gate drops
    /// these rows on open.
    /// </summary>
    private static byte[] BuildTask4JsonBlob(Guid templateId, string host)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"TemplateId\":\"{templateId:D}\",");
        sb.Append($"\"Host\":\"{host}\",");
        sb.Append("\"PrefixTripwire\":{\"Tag\":\"header\",\"TagHash\":12345},");
        sb.Append("\"ContentStartTripwire\":{\"Tag\":\"article\",\"TagHash\":67890},");
        sb.Append("\"ContentEndTripwire\":{\"Tag\":\"article\",\"TagHash\":67890},");
        sb.Append("\"BailoutBytes\":262144,");
        sb.Append("\"MaxCaptureBytes\":1048576,");
        sb.Append("\"Version\":1");
        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // Two rows for the SAME host can't coexist under alpha.20's single-PK schema —
    // alpha.20 UpsertAsync explicitly DELETEd any prior row for the host before
    // inserting a new one (see HEAD~1 source), so the migration only has to handle
    // the one-row-per-host case. No second test needed.

    /// <summary>
    /// Mirror of <c>SqliteStreamingTemplateStore.CurrentStoreVersion</c> — the
    /// test assembly can't see the private const, so we duplicate it. If the
    /// real constant ever moves, these tests will assert against the wrong
    /// value and fail loudly. That's the intent.
    /// </summary>
    private const int CurrentStoreVersion = 4;

    /// <summary>
    /// Builds a JSON blob byte-identical to what alpha.20 wrote: TemplateFence
    /// carries TagAllowlistBloom; StreamingTemplate carries MinContentDepth.
    /// alpha.21 drops both fields and System.Text.Json silently discards them
    /// on deserialise — that's exactly what the migration test is verifying.
    /// </summary>
    private static byte[] BuildAlpha20JsonBlob(Guid templateId, string host)
    {
        // 128-element MinHash + 16-element LshBands match the alpha.20/alpha.21
        // sketcher dimensions; values are deterministic-but-arbitrary.
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"TemplateId\":\"{templateId:D}\",");
        sb.Append($"\"Host\":\"{host}\",");
        sb.Append("\"PrefixFence\":").Append(BuildAlpha20FenceJson(seed: 1, requiredDepth: 1)).Append(',');
        sb.Append("\"ContentStartFence\":").Append(BuildAlpha20FenceJson(seed: 2, requiredDepth: 3)).Append(',');
        sb.Append("\"ContentEndFence\":").Append(BuildAlpha20FenceJson(seed: 3, requiredDepth: 3)).Append(',');
        // alpha.20-only field — alpha.21 drops this. Should be discarded silently.
        sb.Append("\"MinContentDepth\":3,");
        sb.Append("\"BailoutBytes\":262144,");
        sb.Append("\"MaxCaptureBytes\":1048576,");
        sb.Append("\"WindowSize\":8,");
        sb.Append("\"MaxEventsWithoutTransition\":256,");
        sb.Append("\"Version\":1");
        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds an alpha.21-shaped JSON blob — same shape as alpha.20 but without
    /// TagAllowlistBloom + MinContentDepth. Used by the user_version stale-row
    /// test so the seeded rows are individually valid (so we can prove they're
    /// dropped by the gate, not by JSON deserialization failure).
    /// </summary>
    private static byte[] BuildAlpha21JsonBlob(Guid templateId, string host)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"TemplateId\":\"{templateId:D}\",");
        sb.Append($"\"Host\":\"{host}\",");
        sb.Append("\"PrefixFence\":").Append(BuildAlpha21FenceJson(seed: 1, requiredDepth: 1)).Append(',');
        sb.Append("\"ContentStartFence\":").Append(BuildAlpha21FenceJson(seed: 2, requiredDepth: 3)).Append(',');
        sb.Append("\"ContentEndFence\":").Append(BuildAlpha21FenceJson(seed: 3, requiredDepth: 3)).Append(',');
        sb.Append("\"BailoutBytes\":262144,");
        sb.Append("\"MaxCaptureBytes\":1048576,");
        sb.Append("\"WindowSize\":8,");
        sb.Append("\"MaxEventsWithoutTransition\":256,");
        sb.Append("\"Version\":1");
        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string BuildAlpha21FenceJson(int seed, int requiredDepth)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"MinHash\":[");
        for (int i = 0; i < 128; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(unchecked((uint)(seed * 1_000 + i)));
        }
        sb.Append("],");
        sb.Append("\"LshBands\":[");
        for (int i = 0; i < 16; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(unchecked((ulong)(seed * 100_000 + i)));
        }
        sb.Append("],");
        sb.Append($"\"RequiredDepth\":{requiredDepth}");
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildAlpha20FenceJson(int seed, int requiredDepth)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"MinHash\":[");
        for (int i = 0; i < 128; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(unchecked((uint)(seed * 1_000 + i)));
        }
        sb.Append("],");
        sb.Append("\"LshBands\":[");
        for (int i = 0; i < 16; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(unchecked((ulong)(seed * 100_000 + i)));
        }
        sb.Append("],");
        // alpha.20-only field — alpha.21 drops this. Should be discarded silently.
        sb.Append($"\"TagAllowlistBloom\":{unchecked((ulong)(seed * 999))},");
        sb.Append($"\"RequiredDepth\":{requiredDepth}");
        sb.Append('}');
        return sb.ToString();
    }

    private static StreamingTemplate BuildAlpha21Template(string host, int version) =>
        TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"),
            bailoutBytes: 262_144,
            maxCaptureBytes: 1_048_576)
        with { Host = host, Version = version };
}
