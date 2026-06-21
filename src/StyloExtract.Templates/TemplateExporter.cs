using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates.Serialization;

namespace StyloExtract.Templates;

public static class TemplateExporter
{
    public static async Task ExportHostAsync(
        SqliteConnection conn,
        byte[] hostHash,
        string hostDisplayName,
        Stream output,
        CancellationToken ct)
    {
        var templates = new List<ExportTemplate>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT template_id, version_number, signature_minhash, anchor_signature, pq_gram_vector, pq_gram_norm,
                   extractor_blob, observation_count, created_at, last_seen
            FROM templates WHERE host_hash = @host
            """;
        cmd.Parameters.AddWithValue("@host", hostHash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var sigBytes = (byte[])r["signature_minhash"];
            var anchorBytes = (byte[])r["anchor_signature"];
            var pqBytes = (byte[])r["pq_gram_vector"];
            var extractorBlob = (byte[])r["extractor_blob"];
            var extractor = JsonSerializer.Deserialize(extractorBlob, TemplatesJsonContext.Default.LearnedExtractor)!;
            var pqDecoded = PqGramVectorCodec.Decode(pqBytes);
            templates.Add(new ExportTemplate
            {
                TemplateId = new Guid((byte[])r["template_id"]),
                Version = r.GetInt32(r.GetOrdinal("version_number")),
                Fingerprints = new ExportFingerprints
                {
                    SignatureMinhash = Convert.ToBase64String(sigBytes),
                    AnchorSignature = Convert.ToBase64String(anchorBytes),
                    PqGramVector = new ExportPqGramVector
                    {
                        P = 2, Q = 3, TopK = 256,
                        Values = pqDecoded,
                        Norm = r.GetDouble(r.GetOrdinal("pq_gram_norm"))
                    }
                },
                Extractor = extractor,
                Observations = new ExportObservationSummary
                {
                    Count = r.GetInt32(r.GetOrdinal("observation_count")),
                    FirstSeen = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("created_at"))),
                    LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("last_seen")))
                }
            });
        }

        var doc = new ExportSchemaV1
        {
            SchemaVersion = 1,
            ExportedAt = DateTimeOffset.UtcNow,
            Host = new ExportHost
            {
                DisplayName = hostDisplayName,
                HashAlgorithm = "hmac-sha256",
                HashKey = null
            },
            Templates = templates
        };
        // Use the indented context for human-readable export output.
        await JsonSerializer.SerializeAsync(output, doc, TemplatesJsonContextIndented.Default.ExportSchemaV1, ct);
    }
}
