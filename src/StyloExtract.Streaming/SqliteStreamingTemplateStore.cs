using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mostlylucid.Ephemeral.Sqlite;

namespace StyloExtract.Streaming;

public sealed class SqliteStreamingTemplateStore : IStreamingTemplateStore, IAsyncDisposable
{
    /// <summary>
    /// Algorithm-compat version of the streaming-template store, tracked in SQLite
    /// via <c>PRAGMA user_version</c>. Bump this whenever scanner-side scoring
    /// rules change (shingling shape, structural-tag filter, depth handling —
    /// anything that changes the MinHash signature the scanner produces from a
    /// given byte stream). On open, any DB with a lower user_version has its
    /// templates dropped because their signatures were sketched under prior rules
    /// and can no longer match. Existing rows would always Bailout and consumers
    /// that don't auto-reinduct on Bailout would stay stuck forever.
    ///
    /// Version history:
    ///   1 = alpha.21 algorithm (Markov bigram shingles, structural-tag filter,
    ///       depth-aware capture). Note: alpha.21 itself shipped without this
    ///       PRAGMA, so existing alpha.21 DBs read as user_version=0 and get
    ///       dropped on alpha.22 first-open. That's intentional — those rows
    ///       were sketched before the scanner stabilised on these rules.
    ///   2 = alpha.22 (this constant introduced). Same algorithm as alpha.21
    ///       but stamped so future bumps can self-heal cleanly.
    /// </summary>
    private const int CurrentStoreVersion = 2;

    private readonly SqliteSingleWriter _writer;
    private readonly ConcurrentDictionary<Guid, StreamingTemplate> _hot = new();
    // alpha.21: per-host latest cache. Lookups for older versions go to the DB.
    private readonly ConcurrentDictionary<string, StreamingTemplate> _hotByHostLatest =
        new(StringComparer.OrdinalIgnoreCase);

    public SqliteStreamingTemplateStore(string connectionString)
    {
        connectionString = PromoteInMemoryConnectionString(connectionString);
        using (var bootstrap = new SqliteConnection(connectionString))
        {
            bootstrap.Open();
            EnsureSchema(bootstrap);
        }
        _writer = SqliteSingleWriter.GetOrCreate(connectionString);
    }

    public StreamingTemplate? TryGetHot(Guid templateId) =>
        _hot.TryGetValue(templateId, out var t) ? t : null;

    public async ValueTask<StreamingTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        if (_hot.TryGetValue(templateId, out var cached)) return cached;

        var idBytes = templateId.ToByteArray();
        var row = await _writer.QueryAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT template_blob, host FROM streaming_templates WHERE template_id = @id";
            cmd.Parameters.AddWithValue("@id", idBytes);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return ((byte[]?)null, (string?)null);
            var blob = (byte[])reader.GetValue(0);
            var host = reader.IsDBNull(1) ? "" : reader.GetString(1);
            return ((byte[]?)blob, (string?)host);
        }, cancellationToken).ConfigureAwait(false);

        if (row.Item1 is null) return null;
        var template = JsonSerializer.Deserialize(row.Item1, StreamingJsonContext.Default.StreamingTemplate);
        if (template is null) return null;
        _hot[templateId] = template;
        return template;
    }

    public ValueTask RegisterAsync(StreamingTemplate template, CancellationToken cancellationToken = default) =>
        UpsertAsync(template, cancellationToken);

    public async ValueTask UpsertAsync(StreamingTemplate template, CancellationToken cancellationToken = default)
    {
        var blob = JsonSerializer.SerializeToUtf8Bytes(template, StreamingJsonContext.Default.StreamingTemplate);
        var idBytes = template.TemplateId.ToByteArray();
        var host = template.Host ?? "";
        var version = template.Version;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _writer.ExecuteInTransactionAsync(async (conn, tx, ct) =>
        {
            // INSERT OR REPLACE on (host, version) — append-only per version.
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO streaming_templates(host, version, template_id, template_blob, created_at) " +
                "VALUES (@host, @version, @id, @blob, @now)";
            cmd.Parameters.AddWithValue("@host", host);
            cmd.Parameters.AddWithValue("@version", version);
            cmd.Parameters.AddWithValue("@id", idBytes);
            cmd.Parameters.AddWithValue("@blob", blob);
            cmd.Parameters.AddWithValue("@now", nowMs);
            await cmd.ExecuteNonQueryAsync(ct);
        }, cancellationToken).ConfigureAwait(false);

        _hot[template.TemplateId] = template;
        if (!string.IsNullOrEmpty(host))
        {
            // Update latest-by-host cache only if this version is newest.
            _hotByHostLatest.AddOrUpdate(host, template,
                (_, existing) => existing.Version > version ? existing : template);
        }
    }

    public StreamingTemplate? TryGetHotByHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        return _hotByHostLatest.TryGetValue(host, out var t) ? t : null;
    }

    public async ValueTask<StreamingTemplate?> GetByHostAsync(string host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(host)) return null;

        var hot = TryGetHotByHost(host);
        if (hot is not null) return hot;

        var blob = await _writer.QueryAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT template_blob FROM streaming_templates WHERE host = @host " +
                "ORDER BY version DESC, created_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@host", host);
            return (byte[]?)await cmd.ExecuteScalarAsync(cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

        if (blob is null) return null;
        var template = JsonSerializer.Deserialize(blob, StreamingJsonContext.Default.StreamingTemplate);
        if (template is null) return null;
        _hot[template.TemplateId] = template;
        _hotByHostLatest[host] = template;
        return template;
    }

    public async ValueTask<StreamingTemplate?> GetByHostAtVersionAsync(
        string host,
        int version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(host)) return null;

        var blob = await _writer.QueryAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT template_blob FROM streaming_templates WHERE host = @host AND version = @version LIMIT 1";
            cmd.Parameters.AddWithValue("@host", host);
            cmd.Parameters.AddWithValue("@version", version);
            return (byte[]?)await cmd.ExecuteScalarAsync(cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

        if (blob is null) return null;
        var template = JsonSerializer.Deserialize(blob, StreamingJsonContext.Default.StreamingTemplate);
        if (template is null) return null;
        _hot[template.TemplateId] = template;
        return template;
    }

    public async ValueTask<IReadOnlyList<int>> ListVersionsByHostAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(host)) return Array.Empty<int>();

        var versions = await _writer.QueryAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT version FROM streaming_templates WHERE host = @host ORDER BY version ASC";
            cmd.Parameters.AddWithValue("@host", host);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var list = new List<int>();
            while (await reader.ReadAsync(cancellationToken))
                list.Add(reader.GetInt32(0));
            return list;
        }, cancellationToken).ConfigureAwait(false);

        return versions;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        // alpha.21 schema migration: composite PK on (host, version).
        // Detect existing shape by inspecting PRIMARY KEY constraints.
        // The pre-alpha.21 schema had `template_id BLOB PRIMARY KEY`.
        var needsMigration = false;
        var hasTable = false;
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(streaming_templates);";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                hasTable = true;
                // column 1 = name, column 5 = pk position (0 = not PK)
                var name = reader.GetString(1);
                var pkOrdinal = reader.GetInt32(5);
                if (name.Equals("template_id", StringComparison.OrdinalIgnoreCase) && pkOrdinal > 0)
                    needsMigration = true;
            }
        }

        if (!hasTable)
        {
            // Fresh DB — create the new shape directly.
            using var create = conn.CreateCommand();
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
            create.ExecuteNonQuery();
            EnsureStoreVersion(conn);
            return;
        }

        if (needsMigration)
        {
            // Migrate pre-alpha.21 rows into the new shape. Existing rows
            // have NULL/'' version → assigned version 1. Ensure host column
            // exists first (alpha.17 added it; pre-alpha.17 didn't have it).
            var hasHost = false;
            using (var info = conn.CreateCommand())
            {
                info.CommandText = "PRAGMA table_info(streaming_templates);";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetString(1).Equals("host", StringComparison.OrdinalIgnoreCase))
                    {
                        hasHost = true;
                        break;
                    }
                }
            }
            if (!hasHost)
            {
                using var alterHost = conn.CreateCommand();
                alterHost.CommandText = "ALTER TABLE streaming_templates ADD COLUMN host TEXT NOT NULL DEFAULT '';";
                alterHost.ExecuteNonQuery();
            }

            using (var tx = conn.BeginTransaction())
            {
                // Rename old table, create new, copy.
                using (var rename = conn.CreateCommand())
                {
                    rename.Transaction = tx;
                    rename.CommandText = "ALTER TABLE streaming_templates RENAME TO streaming_templates_old;";
                    rename.ExecuteNonQuery();
                }
                using (var create = conn.CreateCommand())
                {
                    create.Transaction = tx;
                    create.CommandText = """
                        CREATE TABLE streaming_templates (
                            host TEXT NOT NULL,
                            version INTEGER NOT NULL,
                            template_id BLOB NOT NULL,
                            template_blob BLOB NOT NULL,
                            created_at INTEGER NOT NULL,
                            PRIMARY KEY (host, version)
                        );
                        """;
                    create.ExecuteNonQuery();
                }
                using (var copy = conn.CreateCommand())
                {
                    copy.Transaction = tx;
                    copy.CommandText = """
                        INSERT OR IGNORE INTO streaming_templates(host, version, template_id, template_blob, created_at)
                        SELECT host, 1, template_id, template_blob, created_at FROM streaming_templates_old;
                        """;
                    copy.ExecuteNonQuery();
                }
                using (var drop = conn.CreateCommand())
                {
                    drop.Transaction = tx;
                    drop.CommandText = "DROP TABLE streaming_templates_old;";
                    drop.ExecuteNonQuery();
                }
                tx.Commit();
            }
            using (var idx = conn.CreateCommand())
            {
                idx.CommandText =
                    "CREATE INDEX IF NOT EXISTS idx_streaming_templates_host ON streaming_templates(host); " +
                    "CREATE INDEX IF NOT EXISTS idx_streaming_templates_template_id ON streaming_templates(template_id);";
                idx.ExecuteNonQuery();
            }
            EnsureStoreVersion(conn);
            return;
        }

        // Existing alpha.21+ shape; just make sure helper indices exist.
        using (var idx = conn.CreateCommand())
        {
            idx.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_streaming_templates_host ON streaming_templates(host); " +
                "CREATE INDEX IF NOT EXISTS idx_streaming_templates_template_id ON streaming_templates(template_id);";
            idx.ExecuteNonQuery();
        }
        EnsureStoreVersion(conn);
    }

    /// <summary>
    /// Alpha.22 algorithm-compat gate. Checks <c>PRAGMA user_version</c>; if it's
    /// below <see cref="CurrentStoreVersion"/> the existing rows were sketched
    /// under prior scanner-side scoring rules and would always Bailout — they're
    /// dead weight, so drop them all and stamp the user_version to current.
    ///
    /// Fresh DBs read user_version=0, the truncate is a no-op, then user_version
    /// gets stamped. Pre-alpha.22 DBs (alpha.21 algorithm baseline included) also
    /// read user_version=0 — their rows get dropped, forcing clean re-induction
    /// on the next request through the scanner.
    ///
    /// Both steps run in a single transaction so a crash between them doesn't
    /// leave the DB stamped-but-not-dropped or dropped-but-not-stamped. PRAGMA
    /// user_version is the SQLite-native integer in the file header — exactly
    /// the right tool for "what algorithm wrote these rows".
    /// </summary>
    private static void EnsureStoreVersion(SqliteConnection conn)
    {
        long current;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            current = (long)(read.ExecuteScalar() ?? 0L);
        }

        if (current >= CurrentStoreVersion) return;

        using var tx = conn.BeginTransaction();
        using (var drop = conn.CreateCommand())
        {
            drop.Transaction = tx;
            drop.CommandText = "DELETE FROM streaming_templates;";
            drop.ExecuteNonQuery();
        }
        using (var stamp = conn.CreateCommand())
        {
            stamp.Transaction = tx;
            // PRAGMA user_version doesn't accept parameter binding; CurrentStoreVersion
            // is a private compile-time constant we control, so interpolation is safe.
            stamp.CommandText = $"PRAGMA user_version = {CurrentStoreVersion};";
            stamp.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal static string PromoteInMemoryConnectionString(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return cs;
        var lower = cs.Trim().ToLowerInvariant();
        if (lower == ":memory:" || lower == "data source=:memory:" || lower == "datasource=:memory:")
        {
            var name = $"styloextract-streaming-{Guid.NewGuid():N}";
            return $"Data Source=file:{name}?mode=memory&cache=shared&uri=true";
        }
        return cs;
    }
}
