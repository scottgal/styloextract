using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mostlylucid.Ephemeral.Sqlite;

namespace StyloExtract.Streaming;

public sealed class SqliteStreamingTemplateStore : IStreamingTemplateStore, IAsyncDisposable
{
    private readonly SqliteSingleWriter _writer;
    private readonly ConcurrentDictionary<Guid, StreamingTemplate> _hot = new();
    private readonly ConcurrentDictionary<string, Guid> _hostIndex =
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
        if (!string.IsNullOrEmpty(template.Host))
            _hostIndex[template.Host] = templateId;
        return template;
    }

    public ValueTask RegisterAsync(StreamingTemplate template, CancellationToken cancellationToken = default) =>
        UpsertAsync(template, cancellationToken);

    public async ValueTask UpsertAsync(StreamingTemplate template, CancellationToken cancellationToken = default)
    {
        var blob = JsonSerializer.SerializeToUtf8Bytes(template, StreamingJsonContext.Default.StreamingTemplate);
        var idBytes = template.TemplateId.ToByteArray();
        var host = template.Host ?? "";
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _writer.ExecuteInTransactionAsync(async (conn, tx, ct) =>
        {
            // If a different template already exists for this host, drop it
            // first — one template per host (latest wins).
            if (!string.IsNullOrEmpty(host))
            {
                await using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText =
                    "DELETE FROM streaming_templates WHERE host = @host AND template_id != @id";
                del.Parameters.AddWithValue("@host", host);
                del.Parameters.AddWithValue("@id", idBytes);
                await del.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO streaming_templates(template_id, template_blob, host, created_at) " +
                "VALUES (@id, @blob, @host, @now)";
            cmd.Parameters.AddWithValue("@id", idBytes);
            cmd.Parameters.AddWithValue("@blob", blob);
            cmd.Parameters.AddWithValue("@host", host);
            cmd.Parameters.AddWithValue("@now", nowMs);
            await cmd.ExecuteNonQueryAsync(ct);
        }, cancellationToken).ConfigureAwait(false);

        _hot[template.TemplateId] = template;
        if (!string.IsNullOrEmpty(host))
            _hostIndex[host] = template.TemplateId;
    }

    public StreamingTemplate? TryGetHotByHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        return _hostIndex.TryGetValue(host, out var id) && _hot.TryGetValue(id, out var t)
            ? t
            : null;
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
                "ORDER BY created_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@host", host);
            return (byte[]?)await cmd.ExecuteScalarAsync(cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

        if (blob is null) return null;
        var template = JsonSerializer.Deserialize(blob, StreamingJsonContext.Default.StreamingTemplate);
        if (template is null) return null;
        _hot[template.TemplateId] = template;
        _hostIndex[host] = template.TemplateId;
        return template;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        // Create the base table if needed (pre-alpha.17 shape — no host column).
        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS streaming_templates (
                    template_id BLOB PRIMARY KEY NOT NULL,
                    template_blob BLOB NOT NULL,
                    created_at INTEGER NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        // alpha.17 migration: add the host column if it doesn't exist yet.
        // PRAGMA table_info returns rows for each column; check for 'host'.
        var hasHost = false;
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(streaming_templates);";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                // column 1 is "name"
                var name = reader.GetString(1);
                if (string.Equals(name, "host", StringComparison.OrdinalIgnoreCase))
                {
                    hasHost = true;
                    break;
                }
            }
        }
        if (!hasHost)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText =
                "ALTER TABLE streaming_templates ADD COLUMN host TEXT NOT NULL DEFAULT '';";
            alter.ExecuteNonQuery();
        }

        using (var idx = conn.CreateCommand())
        {
            idx.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_streaming_templates_host " +
                "ON streaming_templates(host);";
            idx.ExecuteNonQuery();
        }
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
