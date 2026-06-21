using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;
using StyloExtract.Templates.Serialization;

namespace StyloExtract.Templates;

// Microsoft.Data.Sqlite connections are NOT safe for concurrent use.
// All public async methods must acquire _gate before touching _conn to
// serialise access from ASP.NET Core's multi-threaded request pipeline.
public sealed class SqliteTemplateIndex : ITemplateIndex, IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly double _lambdaObs;
    private readonly double _lambdaRecent;
    private readonly double _tauDays;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public SqliteTemplateIndex(SqliteConnection conn,
        double lambdaObs = 0.02,
        double lambdaRecent = 0.05,
        double tauDays = 30.0)
    {
        _conn = conn;
        _lambdaObs = lambdaObs;
        _lambdaRecent = lambdaRecent;
        _tauDays = tauDays;
    }

    public async Task<Guid> RegisterAsync(
        byte[] hostHash,
        StructuralFingerprint fingerprint,
        LearnedExtractor extractor,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LearnedExtractor?> GetExtractorAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT extractor_blob FROM templates WHERE template_id = @id";
            cmd.Parameters.AddWithValue("@id", templateId.ToByteArray());
            var blob = (byte[]?)await cmd.ExecuteScalarAsync(cancellationToken);
            return blob is null ? null : JsonSerializer.Deserialize<LearnedExtractor>(blob, JsonOpts);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetObservationCountAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT observation_count FROM templates WHERE template_id = @id";
            cmd.Parameters.AddWithValue("@id", templateId.ToByteArray());
            var val = await cmd.ExecuteScalarAsync(cancellationToken);
            return val is null ? 0 : Convert.ToInt32(val);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetTemplateVersionAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT version_number FROM templates WHERE template_id = @id";
            cmd.Parameters.AddWithValue("@id", templateId.ToByteArray());
            var val = await cmd.ExecuteScalarAsync(cancellationToken);
            return val is null ? 0 : Convert.ToInt32(val);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Guid?> ProbeFastPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
            double bestScore = double.MinValue;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var candidateBytes in candidates)
            {
                await using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT signature_minhash, observation_count, last_seen FROM templates WHERE template_id = @id";
                cmd.Parameters.AddWithValue("@id", candidateBytes);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) continue;
                var blob = (byte[])reader["signature_minhash"];
                var observationCount = reader.GetInt32(reader.GetOrdinal("observation_count"));
                var lastSeenMs = reader.GetInt64(reader.GetOrdinal("last_seen"));

                var candidateSig = BytesToUintArray(blob);
                var j = JaccardEstimator.Estimate(candidateSig, fingerprint.StructuralMinHash);
                if (j < threshold) continue;

                var ageDays = (nowMs - lastSeenMs) / 86_400_000.0;
                var score = AgingPriorityScorer.Score(j, observationCount, ageDays, _lambdaObs, _lambdaRecent, _tauDays);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new Guid(candidateBytes);
                }
            }
            return best;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(Guid TemplateId, double Cosine)?> ProbeSlowPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT template_id, pq_gram_vector, pq_gram_norm, observation_count, last_seen FROM templates WHERE host_hash = @host";
            cmd.Parameters.AddWithValue("@host", hostHash);
            (Guid TemplateId, double Cosine)? best = null;
            double bestScore = double.MinValue;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                var id = new Guid((byte[])r["template_id"]);
                var candidateCounts = PqGramVectorCodec.Decode((byte[])r["pq_gram_vector"]);
                var candidateNorm = r.GetDouble(r.GetOrdinal("pq_gram_norm"));
                var observationCount = r.GetInt32(r.GetOrdinal("observation_count"));
                var lastSeenMs = r.GetInt64(r.GetOrdinal("last_seen"));

                var cosine = CosineSimilarity(fingerprint.PqGramCounts, fingerprint.PqGramNorm, candidateCounts, candidateNorm);
                if (cosine < threshold) continue;

                var ageDays = (nowMs - lastSeenMs) / 86_400_000.0;
                var score = AgingPriorityScorer.Score(cosine, observationCount, ageDays, _lambdaObs, _lambdaRecent, _tauDays);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (id, cosine);
                }
            }
            return best;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordObservationAsync(Guid templateId, StructuralFingerprint fingerprint, double similarity, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sigBytes = UintArrayToBytes(fingerprint.StructuralMinHash);

            await using var tx = await _conn.BeginTransactionAsync(cancellationToken);
            await using (var upd = _conn.CreateCommand())
            {
                upd.Transaction = (SqliteTransaction)tx;
                upd.CommandText = "UPDATE templates SET observation_count = observation_count + 1, last_seen = @now WHERE template_id = @id";
                upd.Parameters.AddWithValue("@id", templateId.ToByteArray());
                upd.Parameters.AddWithValue("@now", now);
                await upd.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = (SqliteTransaction)tx;
                ins.CommandText = "INSERT INTO template_observations(template_id, observed_at, signature_minhash, similarity_at_match) VALUES (@id, @now, @sig, @sim)";
                ins.Parameters.AddWithValue("@id", templateId.ToByteArray());
                ins.Parameters.AddWithValue("@now", now);
                ins.Parameters.AddWithValue("@sig", sigBytes);
                ins.Parameters.AddWithValue("@sim", similarity);
                await ins.ExecuteNonQueryAsync(cancellationToken);
            }
            // LRU bound: keep only last 100 observations per template
            await using (var trim = _conn.CreateCommand())
            {
                trim.Transaction = (SqliteTransaction)tx;
                trim.CommandText = """
                    DELETE FROM template_observations
                    WHERE rowid IN (
                      SELECT rowid FROM template_observations WHERE template_id = @id
                      ORDER BY observed_at DESC LIMIT -1 OFFSET 100
                    )
                    """;
                trim.Parameters.AddWithValue("@id", templateId.ToByteArray());
                await trim.ExecuteNonQueryAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Updates only the extractor_blob column for a template (used for EWMA drift accumulation).
    /// </summary>
    internal async Task UpdateExtractorAsync(Guid templateId, LearnedExtractor newExtractor, CancellationToken cancellationToken)
    {
        var newExBytes = JsonSerializer.SerializeToUtf8Bytes(newExtractor, JsonOpts);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE templates SET extractor_blob = @ex WHERE template_id = @id";
            cmd.Parameters.AddWithValue("@id", templateId.ToByteArray());
            cmd.Parameters.AddWithValue("@ex", newExBytes);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<(int OldVersion, int NewVersion, StructuralFingerprint? OldFingerprint)> BumpVersionAsync(
        Guid templateId,
        LearnedExtractor newExtractor,
        StructuralFingerprint newFp,
        string reason,
        int versionHistoryDepth,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var newExBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(newExtractor, JsonOpts);
        var newSigBytes = UintArrayToBytes(newFp.StructuralMinHash);
        var newAnchorBytes = UintArrayToBytes(newFp.AnchorMinHash);
        var newPqBytes = Serialization.PqGramVectorCodec.Encode(newFp.PqGramCounts);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = await _conn.BeginTransactionAsync(cancellationToken);
            int oldVersion = 0;
            byte[]? oldSig = null;
            byte[]? oldPq = null;
            double oldPqNorm = 0;
            byte[]? oldExBlob = null;
            await using (var read = _conn.CreateCommand())
            {
                read.Transaction = (SqliteTransaction)tx;
                read.CommandText = "SELECT version_number, signature_minhash, pq_gram_vector, pq_gram_norm, extractor_blob FROM templates WHERE template_id = @id";
                read.Parameters.AddWithValue("@id", templateId.ToByteArray());
                await using var r = await read.ExecuteReaderAsync(cancellationToken);
                if (await r.ReadAsync(cancellationToken))
                {
                    oldVersion = r.GetInt32(0);
                    oldSig = (byte[])r["signature_minhash"];
                    oldPq = (byte[])r["pq_gram_vector"];
                    oldPqNorm = r.GetDouble(r.GetOrdinal("pq_gram_norm"));
                    oldExBlob = (byte[])r["extractor_blob"];
                }
            }
            // Retire old row to history
            await using (var hist = _conn.CreateCommand())
            {
                hist.Transaction = (SqliteTransaction)tx;
                hist.CommandText = """
                    INSERT INTO template_version_history(template_id, version_number, signature_minhash, pq_gram_vector, extractor_blob, retired_at, retirement_reason)
                    VALUES (@id, @ver, @sig, @pq, @ex, @now, @reason)
                    """;
                hist.Parameters.AddWithValue("@id", templateId.ToByteArray());
                hist.Parameters.AddWithValue("@ver", oldVersion);
                hist.Parameters.AddWithValue("@sig", (object?)oldSig ?? DBNull.Value);
                hist.Parameters.AddWithValue("@pq", (object?)oldPq ?? DBNull.Value);
                hist.Parameters.AddWithValue("@ex", (object?)oldExBlob ?? DBNull.Value);
                hist.Parameters.AddWithValue("@now", now);
                hist.Parameters.AddWithValue("@reason", reason);
                await hist.ExecuteNonQueryAsync(cancellationToken);
            }
            // Trim history to versionHistoryDepth
            await using (var trim = _conn.CreateCommand())
            {
                trim.Transaction = (SqliteTransaction)tx;
                trim.CommandText = """
                    DELETE FROM template_version_history
                    WHERE template_id = @id
                      AND version_number NOT IN (
                        SELECT version_number FROM template_version_history
                        WHERE template_id = @id
                        ORDER BY retired_at DESC LIMIT @keep
                      )
                    """;
                trim.Parameters.AddWithValue("@id", templateId.ToByteArray());
                trim.Parameters.AddWithValue("@keep", versionHistoryDepth);
                await trim.ExecuteNonQueryAsync(cancellationToken);
            }
            int newVersion = oldVersion + 1;
            await using (var upd = _conn.CreateCommand())
            {
                upd.Transaction = (SqliteTransaction)tx;
                upd.CommandText = """
                    UPDATE templates SET
                      version_number = @ver,
                      signature_minhash = @sig,
                      anchor_signature = @anchor,
                      pq_gram_vector = @pq,
                      pq_gram_norm = @norm,
                      extractor_blob = @ex,
                      last_refit_at = @now
                    WHERE template_id = @id
                    """;
                upd.Parameters.AddWithValue("@id", templateId.ToByteArray());
                upd.Parameters.AddWithValue("@ver", newVersion);
                upd.Parameters.AddWithValue("@sig", newSigBytes);
                upd.Parameters.AddWithValue("@anchor", newAnchorBytes);
                upd.Parameters.AddWithValue("@pq", newPqBytes);
                upd.Parameters.AddWithValue("@norm", newFp.PqGramNorm);
                upd.Parameters.AddWithValue("@ex", newExBytes);
                upd.Parameters.AddWithValue("@now", now);
                await upd.ExecuteNonQueryAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);

            // Reconstruct old fingerprint for the diff
            StructuralFingerprint? oldFp = null;
            if (oldSig is not null && oldPq is not null)
            {
                oldFp = new StructuralFingerprint
                {
                    StructuralMinHash = BytesToUintArray(oldSig),
                    AnchorMinHash = BytesToUintArray(oldSig), // anchor not separately stored; use sig as proxy
                    LshBands = Array.Empty<ulong>(),
                    PqGramCounts = Serialization.PqGramVectorCodec.Decode(oldPq),
                    PqGramNorm = oldPqNorm,
                    ShingleCount = 0,
                    Hex = ""
                };
            }

            return (oldVersion, newVersion, oldFp);
        }
        finally
        {
            _gate.Release();
        }
    }

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

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _conn.DisposeAsync().ConfigureAwait(false);
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
