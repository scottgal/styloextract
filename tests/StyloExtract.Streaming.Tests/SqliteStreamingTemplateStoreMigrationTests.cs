using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace StyloExtract.Streaming.Tests;

/// <summary>
/// alpha.21 follow-up — verifies the SqliteStreamingTemplateStore's auto-migration
/// from the alpha.16–alpha.20 schema (single-column PK on <c>template_id</c>) to the
/// alpha.21 composite PK on <c>(host, version)</c> preserves every row from a real,
/// live dogfood DB and that legacy JSON blobs (which carried TagAllowlistBloom +
/// MinContentDepth) round-trip cleanly.
/// </summary>
public sealed class SqliteStreamingTemplateStoreMigrationTests
{
    [Fact]
    public async Task OpeningAlpha20Db_MigratesToVersionedSchema_PreservesAllRows()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"sx-migration-{Guid.NewGuid():N}.db");
        try
        {
            // 1. Build an alpha.20-shaped DB by hand: verbatim alpha.20 CREATE TABLE
            //    (single-column PK on template_id, host column added by alpha.17
            //    migration) + insert rows whose JSON blobs match the alpha.20 shape
            //    — i.e. TemplateFence carries TagAllowlistBloom and StreamingTemplate
            //    carries MinContentDepth.
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

            // 2. Open the same path via the alpha.21 SqliteStreamingTemplateStore.
            //    The ctor calls EnsureSchema which should auto-migrate.
            await using var store = new SqliteStreamingTemplateStore($"Data Source={tempDb}");

            // 3. Each seeded row must be queryable via GetByHostAsync (auto-promoted
            //    to version 1) and the JSON blob must deserialise — the alpha.21
            //    shape drops TagAllowlistBloom + MinContentDepth, but System.Text.Json
            //    discards unknown properties by default so the actual sketch data
            //    (MinHash / LshBands) round-trips intact.
            foreach (var (host, id) in seeded)
            {
                var template = await store.GetByHostAsync(host);
                template.Should().NotBeNull($"row for {host} must survive migration");
                template!.Host.Should().Be(host);
                template.TemplateId.Should().Be(id);
                template.Version.Should().Be(1, "legacy rows are auto-promoted to version 1");
                template.PrefixFence.MinHash.Should().NotBeNull().And.HaveCount(128);
                template.PrefixFence.LshBands.Should().NotBeNull().And.HaveCount(16);
                template.PrefixFence.RequiredDepth.Should().Be(1);
            }

            // 4. ListVersionsByHostAsync should report [1] for every seeded host.
            foreach (var (host, _) in seeded)
            {
                var versions = await store.ListVersionsByHostAsync(host);
                versions.Should().Equal(1);
            }

            // 5. After migration we can append a new version on top — proves the new
            //    composite-PK schema is fully usable post-migration.
            var promoted = await store.GetByHostAsync(seeded[0].Host);
            var v2 = promoted! with { TemplateId = Guid.NewGuid(), Version = 2 };
            await store.UpsertAsync(v2);

            var versionsAfter = await store.ListVersionsByHostAsync(seeded[0].Host);
            versionsAfter.Should().Equal(1, 2);
            (await store.GetByHostAsync(seeded[0].Host))!.Version.Should().Be(2);
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-wal"); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-shm"); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task OpeningFreshDb_UsesAlpha21Schema_WithoutMigration()
    {
        // Regression net: a fresh DB must skip the migration branch entirely
        // and land on the alpha.21 shape directly.
        var tempDb = Path.Combine(Path.GetTempPath(), $"sx-fresh-{Guid.NewGuid():N}.db");
        try
        {
            await using (var store = new SqliteStreamingTemplateStore($"Data Source={tempDb}"))
            {
                var template = BuildAlpha21Template("fresh.example", version: 1);
                await store.UpsertAsync(template);
            }

            // Confirm the on-disk schema is the alpha.21 shape — composite PK
            // on (host, version).
            await using var conn = new SqliteConnection($"Data Source={tempDb}");
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
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-wal"); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-shm"); } catch { /* best-effort */ }
        }
    }

    // Two rows for the SAME host can't coexist under alpha.20's single-PK schema —
    // alpha.20 UpsertAsync explicitly DELETEd any prior row for the host before
    // inserting a new one (see HEAD~1 source), so the migration only has to handle
    // the one-row-per-host case. No second test needed.

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

    private static StreamingTemplate BuildAlpha21Template(string host, int version)
    {
        var fence = new TemplateFence(new uint[128], new ulong[16], 1);
        return new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = host,
            PrefixFence = fence,
            ContentStartFence = fence,
            ContentEndFence = fence,
            BailoutBytes = 262_144,
            MaxCaptureBytes = 1_048_576,
            WindowSize = 8,
            MaxEventsWithoutTransition = 256,
            Version = version,
        };
    }
}
