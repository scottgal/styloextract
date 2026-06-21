using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;
using StyloExtract.Templates;
using StyloExtract.Templates.Serialization;

namespace StyloExtract.Templates.Postgres;

/// <summary>
/// PostgreSQL-backed implementation of <see cref="ITemplateIndex"/>.
///
/// Contract parity with <see cref="SqliteTemplateIndex"/>:
///   - Identical schema shape (table names, column semantics, observation LRU cap of 100)
///   - Same aging priority scorer weights
///   - Same serialization: extractor_blob = UTF-8 JSON (camelCase), pq_gram_vector = PqGramVectorCodec binary,
///     signature_minhash = little-endian uint[] byte dump, LSH bands = little-endian ulong bytes
///   - GUIDs stored as bytea (16 bytes, same byte order as Guid.ToByteArray())
///
/// Divergence from SQLite:
///   - No single-writer coordinator (Npgsql pools connections; Postgres handles concurrency natively)
///   - No read cache (adding one is future work; the critical path is sub-ms in Postgres with indexes)
///   - Schema applied asynchronously via EnsureSchemaAsync; call this once before use
///
/// Thread-safety: safe for concurrent calls. Each operation acquires a pooled connection.
/// </summary>
public sealed class PostgresTemplateIndex : ITemplateIndex, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly double _lambdaObs;
    private readonly double _lambdaRecent;
    private readonly double _tauDays;
    private bool _schemaEnsured;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    // camelCase JSON serialization to match the SQLite provider blob format (TemplatesJsonContext).
    // The Postgres package is not AOT-compatible so reflection-based serialization is fine.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public PostgresTemplateIndex(
        string connectionString,
        double lambdaObs = 0.02,
        double lambdaRecent = 0.05,
        double tauDays = 30.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _lambdaObs = lambdaObs;
        _lambdaRecent = lambdaRecent;
        _tauDays = tauDays;
    }

    /// <summary>
    /// Ensures the schema exists. Called lazily on first operation; safe to call multiple times.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaEnsured) return;
        await _schemaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaEnsured) return;
            await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await PostgresSchema.EnsureCreatedAsync(conn, cancellationToken).ConfigureAwait(false);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    public async Task<Guid> RegisterAsync(
        byte[] hostHash,
        StructuralFingerprint fingerprint,
        LearnedExtractor extractor,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var id = extractor.TemplateId == Guid.Empty ? Guid.NewGuid() : extractor.TemplateId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sigBytes = UintArrayToBytes(fingerprint.StructuralMinHash);
        var anchorBytes = UintArrayToBytes(fingerprint.AnchorMinHash);
        var pqBytes = PqGramVectorCodec.Encode(fingerprint.PqGramCounts);
        var extractorBytes = JsonSerializer.SerializeToUtf8Bytes(extractor, JsonOptions);
        var idBytes = id.ToByteArray();

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO templates(template_id, host_hash, version_number, signature_minhash, anchor_signature, pq_gram_vector, pq_gram_norm, extractor_blob, observation_count, created_at, last_seen)
                VALUES (@id, @host, 1, @sig, @anchor, @pq, @norm, @ex, 1, @now, @now)
                ON CONFLICT (template_id) DO NOTHING
                """;
            AddBytea(cmd, "@id", idBytes);
            AddBytea(cmd, "@host", hostHash);
            AddBytea(cmd, "@sig", sigBytes);
            AddBytea(cmd, "@anchor", anchorBytes);
            AddBytea(cmd, "@pq", pqBytes);
            cmd.Parameters.AddWithValue("@norm", fingerprint.PqGramNorm);
            AddBytea(cmd, "@ex", extractorBytes);
            cmd.Parameters.AddWithValue("@now", now);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var bandCmd = conn.CreateCommand())
        {
            bandCmd.Transaction = tx;
            bandCmd.CommandText = """
                INSERT INTO template_lsh_band_index(band_hash, band_index, template_id)
                VALUES (@bh, @bi, @id)
                ON CONFLICT DO NOTHING
                """;
            var bhParam = bandCmd.Parameters.Add("@bh", NpgsqlDbType.Bytea);
            var biParam = bandCmd.Parameters.Add("@bi", NpgsqlDbType.Integer);
            var idParam = bandCmd.Parameters.Add("@id", NpgsqlDbType.Bytea);
            idParam.Value = idBytes;

            for (int i = 0; i < fingerprint.LshBands.Length; i++)
            {
                bhParam.Value = BitConverter.GetBytes(fingerprint.LshBands[i]);
                biParam.Value = i;
                await bandCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<LearnedExtractor?> GetExtractorAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extractor_blob FROM templates WHERE template_id = @id";
        AddBytea(cmd, "@id", templateId.ToByteArray());
        var blob = (byte[]?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return blob is null ? null : JsonSerializer.Deserialize<LearnedExtractor>(blob, JsonOptions);
    }

    public async Task<int> GetObservationCountAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT observation_count FROM templates WHERE template_id = @id";
        AddBytea(cmd, "@id", templateId.ToByteArray());
        var val = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return val is null ? 0 : Convert.ToInt32(val);
    }

    public async Task<int> GetTemplateVersionAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version_number FROM templates WHERE template_id = @id";
        AddBytea(cmd, "@id", templateId.ToByteArray());
        var val = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return val is null ? 0 : Convert.ToInt32(val);
    }

    public async Task<Guid?> ProbeFastPathAsync(
        byte[] hostHash,
        StructuralFingerprint fingerprint,
        double threshold,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        // Collect candidate template_id bytes that share at least one LSH band bucket.
        var candidates = new HashSet<byte[]>(ByteArrayComparer.Instance);

        await using (var conn = await OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT b.template_id
                FROM template_lsh_band_index b
                INNER JOIN templates t ON t.template_id = b.template_id
                WHERE t.host_hash = @host AND b.band_hash = @bh AND b.band_index = @bi
                """;
            AddBytea(cmd, "@host", hostHash);
            var bhParam = cmd.Parameters.Add("@bh", NpgsqlDbType.Bytea);
            var biParam = cmd.Parameters.Add("@bi", NpgsqlDbType.Integer);

            for (int i = 0; i < fingerprint.LshBands.Length; i++)
            {
                bhParam.Value = BitConverter.GetBytes(fingerprint.LshBands[i]);
                biParam.Value = i;
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
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
            await using var conn2 = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = "SELECT signature_minhash, observation_count, last_seen FROM templates WHERE template_id = @id";
            AddBytea(cmd2, "@id", candidateBytes);
            await using var reader = await cmd2.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) continue;

            var sig = (byte[])reader["signature_minhash"];
            var observationCount = reader.GetInt32(reader.GetOrdinal("observation_count"));
            var lastSeenMs = reader.GetInt64(reader.GetOrdinal("last_seen"));

            var candidateSig = BytesToUintArray(sig);
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

    public async Task<(Guid TemplateId, double Cosine)?> ProbeSlowPathAsync(
        byte[] hostHash,
        StructuralFingerprint fingerprint,
        double threshold,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        (Guid TemplateId, double Cosine)? best = null;
        double bestScore = double.MinValue;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT template_id, pq_gram_vector, pq_gram_norm, observation_count, last_seen FROM templates WHERE host_hash = @host";
        AddBytea(cmd, "@host", hostHash);

        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
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

    public async Task RecordObservationAsync(
        Guid templateId,
        StructuralFingerprint fingerprint,
        double similarity,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sigBytes = UintArrayToBytes(fingerprint.StructuralMinHash);
        var idBytes = templateId.ToByteArray();

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE templates SET observation_count = observation_count + 1, last_seen = @now WHERE template_id = @id";
            AddBytea(upd, "@id", idBytes);
            upd.Parameters.AddWithValue("@now", now);
            await upd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO template_observations(template_id, observed_at, signature_minhash, similarity_at_match) VALUES (@id, @now, @sig, @sim)";
            AddBytea(ins, "@id", idBytes);
            ins.Parameters.AddWithValue("@now", now);
            AddBytea(ins, "@sig", sigBytes);
            ins.Parameters.AddWithValue("@sim", similarity);
            await ins.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // LRU bound: keep only last 100 observations per template (mirrors SQLite provider).
        await using (var trim = conn.CreateCommand())
        {
            trim.Transaction = tx;
            trim.CommandText = """
                DELETE FROM template_observations
                WHERE ctid IN (
                  SELECT ctid FROM template_observations
                  WHERE template_id = @id
                  ORDER BY observed_at DESC
                  OFFSET 100
                )
                """;
            AddBytea(trim, "@id", idBytes);
            await trim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates only the extractor_blob column (used during EWMA drift accumulation).
    /// Mirrors SqliteTemplateIndex.UpdateExtractorAsync.
    /// </summary>
    public async Task UpdateExtractorAsync(
        Guid templateId,
        LearnedExtractor newExtractor,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var newExBytes = JsonSerializer.SerializeToUtf8Bytes(newExtractor, JsonOptions);
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE templates SET extractor_blob = @ex WHERE template_id = @id";
        AddBytea(cmd, "@ex", newExBytes);
        AddBytea(cmd, "@id", templateId.ToByteArray());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retires the current template version to history and writes the new fingerprint and extractor.
    /// Mirrors SqliteTemplateIndex.BumpVersionAsync.
    /// </summary>
    public async Task<(int OldVersion, int NewVersion, StructuralFingerprint? OldFingerprint)> BumpVersionAsync(
        Guid templateId,
        LearnedExtractor newExtractor,
        StructuralFingerprint newFp,
        string reason,
        int versionHistoryDepth,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var newExBytes = JsonSerializer.SerializeToUtf8Bytes(newExtractor, JsonOptions);
        var newSigBytes = UintArrayToBytes(newFp.StructuralMinHash);
        var newAnchorBytes = UintArrayToBytes(newFp.AnchorMinHash);
        var newPqBytes = PqGramVectorCodec.Encode(newFp.PqGramCounts);
        var idBytes = templateId.ToByteArray();

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int oldVersion = 0;
        byte[]? oldSig = null;
        byte[]? oldPq = null;
        double oldPqNorm = 0;
        byte[]? oldExBlob = null;

        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT version_number, signature_minhash, pq_gram_vector, pq_gram_norm, extractor_blob FROM templates WHERE template_id = @id";
            AddBytea(read, "@id", idBytes);
            await using var r = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                oldVersion = r.GetInt32(0);
                oldSig = (byte[])r["signature_minhash"];
                oldPq = (byte[])r["pq_gram_vector"];
                oldPqNorm = r.GetDouble(r.GetOrdinal("pq_gram_norm"));
                oldExBlob = (byte[])r["extractor_blob"];
            }
        }

        // Retire old version to history.
        await using (var hist = conn.CreateCommand())
        {
            hist.Transaction = tx;
            hist.CommandText = """
                INSERT INTO template_version_history(template_id, version_number, signature_minhash, pq_gram_vector, extractor_blob, retired_at, retirement_reason)
                VALUES (@id, @ver, @sig, @pq, @ex, @now, @reason)
                """;
            AddBytea(hist, "@id", idBytes);
            hist.Parameters.AddWithValue("@ver", oldVersion);
            AddByteaNullable(hist, "@sig", oldSig);
            AddByteaNullable(hist, "@pq", oldPq);
            AddByteaNullable(hist, "@ex", oldExBlob);
            hist.Parameters.AddWithValue("@now", now);
            hist.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
            await hist.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Trim history to versionHistoryDepth.
        await using (var trim = conn.CreateCommand())
        {
            trim.Transaction = tx;
            trim.CommandText = """
                DELETE FROM template_version_history
                WHERE template_id = @id
                  AND version_number NOT IN (
                    SELECT version_number FROM template_version_history
                    WHERE template_id = @id
                    ORDER BY retired_at DESC
                    LIMIT @keep
                  )
                """;
            AddBytea(trim, "@id", idBytes);
            trim.Parameters.AddWithValue("@keep", versionHistoryDepth);
            await trim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int newVersion = oldVersion + 1;
        await using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
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
            AddBytea(upd, "@id", idBytes);
            upd.Parameters.AddWithValue("@ver", newVersion);
            AddBytea(upd, "@sig", newSigBytes);
            AddBytea(upd, "@anchor", newAnchorBytes);
            AddBytea(upd, "@pq", newPqBytes);
            upd.Parameters.AddWithValue("@norm", newFp.PqGramNorm);
            AddBytea(upd, "@ex", newExBytes);
            upd.Parameters.AddWithValue("@now", now);
            await upd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Reconstruct old fingerprint for the diff (mirrors SqliteTemplateIndex behaviour).
        StructuralFingerprint? oldFp = null;
        if (oldSig is not null && oldPq is not null)
        {
            oldFp = new StructuralFingerprint
            {
                StructuralMinHash = BytesToUintArray(oldSig),
                AnchorMinHash = BytesToUintArray(oldSig), // anchor not separately stored; use sig as proxy
                LshBands = Array.Empty<ulong>(),
                PqGramCounts = PqGramVectorCodec.Decode(oldPq),
                PqGramNorm = oldPqNorm,
                ShingleCount = 0,
                Hex = ""
            };
        }

        return (oldVersion, newVersion, oldFp);
    }

    // --- helpers ---

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    private static void AddBytea(NpgsqlCommand cmd, string name, byte[] value)
        => cmd.Parameters.Add(name, NpgsqlDbType.Bytea).Value = value;

    private static void AddByteaNullable(NpgsqlCommand cmd, string name, byte[]? value)
    {
        var p = cmd.Parameters.Add(name, NpgsqlDbType.Bytea);
        p.Value = (object?)value ?? DBNull.Value;
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

    private static double CosineSimilarity(
        IReadOnlyDictionary<string, double> a, double na,
        IReadOnlyDictionary<string, double> b, double nb)
    {
        if (na == 0 || nb == 0) return 0;
        double dot = 0;
        foreach (var kv in a)
        {
            if (b.TryGetValue(kv.Key, out var v)) dot += kv.Value * v;
        }
        return dot / (na * nb);
    }

    public ValueTask DisposeAsync()
    {
        _schemaLock.Dispose();
        return ValueTask.CompletedTask;
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
