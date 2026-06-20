using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;
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

    public async Task<Guid?> ProbeFastPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
    {
        var candidates = new HashSet<byte[]>(ByteArrayComparer.Instance);
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT b.template_id
                FROM template_lsh_band_index b
                INNER JOIN templates t ON t.template_id = b.template_id
                WHERE t.host_hash = @host AND b.band_hash = @bh AND b.band_index = @bi
                """;
            cmd.Parameters.Add("@host", SqliteType.Blob).Value = hostHash;
            cmd.Parameters.Add("@bh", SqliteType.Blob);
            cmd.Parameters.Add("@bi", SqliteType.Integer);
            for (int i = 0; i < fingerprint.LshBands.Length; i++)
            {
                cmd.Parameters["@bh"].Value = BitConverter.GetBytes(fingerprint.LshBands[i]);
                cmd.Parameters["@bi"].Value = i;
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await r.ReadAsync(cancellationToken))
                {
                    candidates.Add((byte[])r["template_id"]);
                }
            }
        }

        Guid? best = null;
        double bestJaccard = 0;
        foreach (var candidateBytes in candidates)
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT signature_minhash FROM templates WHERE template_id = @id";
            cmd.Parameters.AddWithValue("@id", candidateBytes);
            var blob = (byte[]?)await cmd.ExecuteScalarAsync(cancellationToken);
            if (blob is null) continue;
            var candidateSig = BytesToUintArray(blob);
            var j = JaccardEstimator.Estimate(candidateSig, fingerprint.StructuralMinHash);
            if (j >= threshold && j > bestJaccard)
            {
                bestJaccard = j;
                best = new Guid(candidateBytes);
            }
        }
        return best;
    }

    public async Task<(Guid TemplateId, double Cosine)?> ProbeSlowPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT template_id, pq_gram_vector, pq_gram_norm FROM templates WHERE host_hash = @host";
        cmd.Parameters.AddWithValue("@host", hostHash);
        (Guid TemplateId, double Cosine)? best = null;
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var id = new Guid((byte[])r["template_id"]);
            var candidateCounts = PqGramVectorCodec.Decode((byte[])r["pq_gram_vector"]);
            var candidateNorm = r.GetDouble(r.GetOrdinal("pq_gram_norm"));
            var cosine = CosineSimilarity(fingerprint.PqGramCounts, fingerprint.PqGramNorm, candidateCounts, candidateNorm);
            if (cosine >= threshold && (best is null || cosine > best.Value.Cosine))
            {
                best = (id, cosine);
            }
        }
        return best;
    }

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

    private static double CosineSimilarity(IReadOnlyDictionary<string, double> a, double na, IReadOnlyDictionary<string, double> b, double nb)
    {
        if (na == 0 || nb == 0) return 0;
        double dot = 0;
        foreach (var kv in a)
        {
            if (b.TryGetValue(kv.Key, out var v)) dot += kv.Value * v;
        }
        return dot / (na * nb);
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj)
        {
            int h = 17;
            foreach (var b in obj) h = h * 31 + b;
            return h;
        }
    }
}
