using System.IO.Hashing;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates.Serialization;

namespace StyloExtract.Templates;

public sealed record ImportResult(int ImportedCount, int SkippedCount, int ReplacedCount);

public static class TemplateImporter
{
    public static async Task<ImportResult> ImportAsync(SqliteConnection conn, byte[] hostHash, Stream input, CancellationToken ct)
    {
        var doc = await JsonSerializer.DeserializeAsync(input, TemplatesJsonContext.Default.ExportSchemaV1, ct);
        if (doc is null) return new ImportResult(0, 0, 0);
        if (doc.SchemaVersion != 1) throw new InvalidDataException($"Unsupported schemaVersion {doc.SchemaVersion}");

        int imported = 0, replaced = 0;

        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var t in doc.Templates)
        {
            var idBytes = t.TemplateId.ToByteArray();
            var sigBytes = Convert.FromBase64String(t.Fingerprints.SignatureMinhash);
            var anchorBytes = Convert.FromBase64String(t.Fingerprints.AnchorSignature);
            var pqBytes = PqGramVectorCodec.Encode(t.Fingerprints.PqGramVector.Values);
            // Re-serialize with the canonical source-gen context (camelCase).
            var extractorBytes = JsonSerializer.SerializeToUtf8Bytes(t.Extractor, TemplatesJsonContext.Default.LearnedExtractor);

            // Check if already exists
            bool existed;
            await using (var check = conn.CreateCommand())
            {
                check.Transaction = (SqliteTransaction)tx;
                check.CommandText = "SELECT 1 FROM templates WHERE template_id = @id";
                check.Parameters.AddWithValue("@id", idBytes);
                existed = await check.ExecuteScalarAsync(ct) is not null;
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = (SqliteTransaction)tx;
                ins.CommandText = """
                    INSERT OR REPLACE INTO templates(template_id, host_hash, version_number, signature_minhash, anchor_signature, pq_gram_vector, pq_gram_norm, extractor_blob, observation_count, created_at, last_seen)
                    VALUES (@id, @host, @ver, @sig, @anchor, @pq, @norm, @ex, @obs, @created, @last)
                    """;
                ins.Parameters.AddWithValue("@id", idBytes);
                ins.Parameters.AddWithValue("@host", hostHash);
                ins.Parameters.AddWithValue("@ver", t.Version);
                ins.Parameters.AddWithValue("@sig", sigBytes);
                ins.Parameters.AddWithValue("@anchor", anchorBytes);
                ins.Parameters.AddWithValue("@pq", pqBytes);
                ins.Parameters.AddWithValue("@norm", t.Fingerprints.PqGramVector.Norm);
                ins.Parameters.AddWithValue("@ex", extractorBytes);
                ins.Parameters.AddWithValue("@obs", t.Observations.Count);
                ins.Parameters.AddWithValue("@created", t.Observations.FirstSeen.ToUnixTimeMilliseconds());
                ins.Parameters.AddWithValue("@last", t.Observations.LastSeen.ToUnixTimeMilliseconds());
                await ins.ExecuteNonQueryAsync(ct);
            }

            // Rebuild LSH band index
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = (SqliteTransaction)tx;
                del.CommandText = "DELETE FROM template_lsh_band_index WHERE template_id = @id";
                del.Parameters.AddWithValue("@id", idBytes);
                await del.ExecuteNonQueryAsync(ct);
            }
            var sig = SqliteTemplateIndex.BytesToUintArray(sigBytes);
            var bands = ComputeBandHashes(sig, 16, 8);
            await using (var bandCmd = conn.CreateCommand())
            {
                bandCmd.Transaction = (SqliteTransaction)tx;
                bandCmd.CommandText = "INSERT OR IGNORE INTO template_lsh_band_index(band_hash, band_index, template_id) VALUES (@bh, @bi, @id)";
                bandCmd.Parameters.Add("@bh", SqliteType.Blob);
                bandCmd.Parameters.Add("@bi", SqliteType.Integer);
                bandCmd.Parameters.Add("@id", SqliteType.Blob);
                for (int i = 0; i < bands.Length; i++)
                {
                    bandCmd.Parameters["@bh"].Value = BitConverter.GetBytes(bands[i]);
                    bandCmd.Parameters["@bi"].Value = i;
                    bandCmd.Parameters["@id"].Value = idBytes;
                    await bandCmd.ExecuteNonQueryAsync(ct);
                }
            }

            if (existed) replaced++; else imported++;
        }
        await tx.CommitAsync(ct);
        return new ImportResult(imported, 0, replaced);
    }

    private static ulong[] ComputeBandHashes(uint[] signature, int bands, int rows)
    {
        var result = new ulong[bands];
        var buf = new byte[rows * 4];
        for (int b = 0; b < bands; b++)
        {
            for (int r = 0; r < rows; r++)
            {
                BitConverter.GetBytes(signature[b * rows + r]).CopyTo(buf, r * 4);
            }
            result[b] = XxHash64.HashToUInt64(buf);
        }
        return result;
    }
}
