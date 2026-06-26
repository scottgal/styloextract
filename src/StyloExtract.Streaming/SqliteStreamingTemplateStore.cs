using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mostlylucid.Ephemeral.Sqlite;

namespace StyloExtract.Streaming;

public sealed class SqliteStreamingTemplateStore : IStreamingTemplateStore, IAsyncDisposable
{
    private readonly SqliteSingleWriter _writer;
    private readonly ConcurrentDictionary<Guid, StreamingTemplate> _hot = new();

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
        var blob = await _writer.QueryAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT template_blob FROM streaming_templates WHERE template_id = @id";
            cmd.Parameters.AddWithValue("@id", idBytes);
            return (byte[]?)await cmd.ExecuteScalarAsync(cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

        if (blob is null) return null;
        var template = JsonSerializer.Deserialize(blob, StreamingJsonContext.Default.StreamingTemplate);
        if (template is not null) _hot[templateId] = template;
        return template;
    }

    public async ValueTask RegisterAsync(StreamingTemplate template, CancellationToken cancellationToken = default)
    {
        var blob = JsonSerializer.SerializeToUtf8Bytes(template, StreamingJsonContext.Default.StreamingTemplate);
        var idBytes = template.TemplateId.ToByteArray();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _writer.ExecuteInTransactionAsync(async (conn, tx, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO streaming_templates(template_id, template_blob, created_at) " +
                "VALUES (@id, @blob, @now)";
            cmd.Parameters.AddWithValue("@id", idBytes);
            cmd.Parameters.AddWithValue("@blob", blob);
            cmd.Parameters.AddWithValue("@now", nowMs);
            await cmd.ExecuteNonQueryAsync(ct);
        }, cancellationToken).ConfigureAwait(false);

        _hot[template.TemplateId] = template;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS streaming_templates (
                template_id BLOB PRIMARY KEY NOT NULL,
                template_blob BLOB NOT NULL,
                created_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
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
