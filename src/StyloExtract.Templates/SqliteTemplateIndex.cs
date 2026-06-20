using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates.Serialization;

namespace StyloExtract.Templates;

public sealed class SqliteTemplateIndex : ITemplateIndex
{
    private readonly SqliteConnection _conn;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public SqliteTemplateIndex(SqliteConnection conn)
    {
        _conn = conn;
    }

    public async Task<Guid> RegisterAsync(
        byte[] hostHash,
        StructuralFingerprint fingerprint,
        LearnedExtractor extractor,
        CancellationToken cancellationToken)
    {
        var id = extractor.TemplateId == Guid.Empty ? Guid.NewGuid() : extractor.TemplateId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sigBytes = UintArrayToBytes(fingerprint.StructuralMinHash);
        var anchorBytes = UintArrayToBytes(fingerprint.AnchorMinHash);
        var pqBytes = PqGramVectorCodec.Encode(fingerprint.PqGramCounts);
        var extractorBytes = JsonSerializer.SerializeToUtf8Bytes(extractor, JsonOpts);

        await using (var tx = await _conn.BeginTransactionAsync(cancellationToken))
        {
            await using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO templates(template_id, host_hash, version_number, signature_minhash, anchor_signature, pq_gram_vector, pq_gram_norm, extractor_blob, observation_count, created_at, last_seen)
                    VALUES (@id, @host, 1, @sig, @anchor, @pq, @norm, @ex, 1, @now, @now)
                    """;
                cmd.Parameters.AddWithValue("@id", id.ToByteArray());
                cmd.Parameters.AddWithValue("@host", hostHash);
                cmd.Parameters.AddWithValue("@sig", sigBytes);
                cmd.Parameters.AddWithValue("@anchor", anchorBytes);
                cmd.Parameters.AddWithValue("@pq", pqBytes);
                cmd.Parameters.AddWithValue("@norm", fingerprint.PqGramNorm);
                cmd.Parameters.AddWithValue("@ex", extractorBytes);
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var bandCmd = _conn.CreateCommand())
            {
                bandCmd.Transaction = (SqliteTransaction)tx;
                bandCmd.CommandText = "INSERT OR IGNORE INTO template_lsh_band_index(band_hash, band_index, template_id) VALUES (@bh, @bi, @id)";
                bandCmd.Parameters.Add("@bh", SqliteType.Blob);
                bandCmd.Parameters.Add("@bi", SqliteType.Integer);
                bandCmd.Parameters.Add("@id", SqliteType.Blob);
                for (int i = 0; i < fingerprint.LshBands.Length; i++)
                {
                    bandCmd.Parameters["@bh"].Value = BitConverter.GetBytes(fingerprint.LshBands[i]);
                    bandCmd.Parameters["@bi"].Value = i;
                    bandCmd.Parameters["@id"].Value = id.ToByteArray();
                    await bandCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            await tx.CommitAsync(cancellationToken);
        }
        return id;
    }

    public async Task<LearnedExtractor?> GetExtractorAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT extractor_blob FROM templates WHERE template_id = @id";
        cmd.Parameters.AddWithValue("@id", templateId.ToByteArray());
        var blob = (byte[]?)await cmd.ExecuteScalarAsync(cancellationToken);
        return blob is null ? null : JsonSerializer.Deserialize<LearnedExtractor>(blob, JsonOpts);
    }

    public async Task<int> GetObservationCountAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT observation_count FROM templates WHERE template_id = @id";
        cmd.Parameters.AddWithValue("@id", templateId.ToByteArray());
        var val = await cmd.ExecuteScalarAsync(cancellationToken);
        return val is null ? 0 : Convert.ToInt32(val);
    }

    public async Task<int> GetTemplateVersionAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT version_number FROM templates WHERE template_id = @id";
        cmd.Parameters.AddWithValue("@id", templateId.ToByteArray());
        var val = await cmd.ExecuteScalarAsync(cancellationToken);
        return val is null ? 0 : Convert.ToInt32(val);
    }

    public Task<Guid?> ProbeFastPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
        => throw new NotImplementedException("Filled in T23");

    public Task<(Guid TemplateId, double Cosine)?> ProbeSlowPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
        => throw new NotImplementedException("Filled in T23");

    public Task RecordObservationAsync(Guid templateId, StructuralFingerprint fingerprint, double similarity, CancellationToken cancellationToken)
        => throw new NotImplementedException("Filled in M4");

    private static byte[] UintArrayToBytes(uint[] sig)
    {
        var bytes = new byte[sig.Length * 4];
        Buffer.BlockCopy(sig, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static uint[] BytesToUintArray(byte[] bytes)
    {
        var sig = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, sig, 0, bytes.Length);
        return sig;
    }
}
