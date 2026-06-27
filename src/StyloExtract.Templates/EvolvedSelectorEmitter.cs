using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;

namespace StyloExtract.Templates;

/// <summary>
/// Phase 2 Task 8: walks the rule-observation corpus per (LSH bucket, role)
/// cell, uses <see cref="CorpusMiner.ComputeStableSubsequenceAsync"/> to
/// derive the cluster's shared structural anchor, and persists one
/// <see cref="EvolvedSelectorCandidate"/> per distinct contributing host via
/// <see cref="ITemplateIndex.UpsertCandidateAsync"/>.
///
/// Repeat mining of the same anchor is a no-op write — the store dedupes on
/// (host, role, target_signature). Apply-time integration (Task 9) consumes
/// the rows and reports win/loss outcomes; promotion (Task 11) reads
/// reputation_score and folds high-scoring candidates into the active
/// template.
///
/// No internal state; callers may construct one per mining pass or share a
/// singleton. Task 10's background coordinator drives
/// <see cref="EmitForAllClustersAsync"/> on a schedule.
/// </summary>
public sealed class EvolvedSelectorEmitter
{
    private readonly ITemplateIndex _index;
    private readonly CorpusMiner _miner;
    private readonly ILogger<EvolvedSelectorEmitter>? _logger;

    public EvolvedSelectorEmitter(
        ITemplateIndex index,
        CorpusMiner miner,
        ILogger<EvolvedSelectorEmitter>? logger = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _miner = miner ?? throw new ArgumentNullException(nameof(miner));
        _logger = logger;
    }

    /// <summary>
    /// Mine one (bucket, role) cell and emit a candidate per distinct host
    /// whose observations contributed. Returns the count upserted. Skips
    /// cells with fewer than 3 observations or no stable substructure.
    /// </summary>
    public async Task<int> EmitForClusterAsync(
        int lshBucket,
        BlockRole role,
        double minPresenceRatio = 0.7,
        CancellationToken ct = default)
    {
        var observations = await _index
            .GetObservationsByBucketAsync(lshBucket, role, limit: 1000, ct)
            .ConfigureAwait(false);

        if (observations.Count < 3) return 0;

        var stable = await _miner
            .ComputeStableSubsequenceAsync(lshBucket, role, minPresenceRatio, ct)
            .ConfigureAwait(false);

        if (stable is null || stable.Count == 0) return 0;

        // Group by HostHash bytes — bucket-scoped reads hide the raw host but
        // surface the HMAC so the emitter can dedupe per-host writes without
        // needing the original string.
        var byHost = new Dictionary<byte[], int>(ByteArrayComparer.Instance);
        foreach (var obs in observations)
        {
            if (obs.HostHash.Length == 0) continue;
            if (!byHost.TryGetValue(obs.HostHash, out var c)) byHost[obs.HostHash] = 1;
            else byHost[obs.HostHash] = c + 1;
        }

        if (byHost.Count == 0) return 0;

        var targetSig = ComputeTargetSig(stable[^1]);
        var now = DateTimeOffset.UtcNow;
        var emitted = 0;

        foreach (var (hostHash, contributing) in byHost)
        {
            try
            {
                await _index.UpsertCandidateAsync(new EvolvedSelectorCandidate
                {
                    CandidateId = Guid.NewGuid(),
                    Host = "",
                    HostHash = hostHash,
                    LshBucket = lshBucket,
                    Role = role,
                    Claims = stable,
                    TargetSignature = targetSig,
                    SourceCount = contributing,
                    Confidence = minPresenceRatio,
                    CreatedAt = now,
                }, ct).ConfigureAwait(false);
                emitted++;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex,
                    "UpsertCandidate failed for role {Role} bucket {Bucket}", role, lshBucket);
            }
        }

        return emitted;
    }

    /// <summary>
    /// Walk every (bucket, role) cell present in the corpus, mining each in
    /// turn. Yields one <see cref="ClusterEmissionResult"/> per distinct cell
    /// discovered. Throttled callers (Task 10) iterate with cancellation and
    /// per-cell back-off.
    /// </summary>
    public async IAsyncEnumerable<ClusterEmissionResult> EmitForAllClustersAsync(
        double minPresenceRatio = 0.7,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<(int Bucket, BlockRole Role)>();
        await foreach (var obs in _index.EnumerateObservationsAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            var key = (obs.LshBucket, obs.Role);
            if (!seen.Add(key)) continue;
            var emitted = await EmitForClusterAsync(obs.LshBucket, obs.Role, minPresenceRatio, ct)
                .ConfigureAwait(false);
            yield return new ClusterEmissionResult
            {
                LshBucket = obs.LshBucket,
                Role = obs.Role,
                CandidatesEmitted = emitted,
            };
        }
    }

    private static ulong ComputeTargetSig(IdentityClaim leaf)
        => leaf.TagHash ^ (leaf.IdHash ?? 0UL);

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y)
            => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj)
        {
            var h = 17;
            foreach (var b in obj) h = h * 31 + b;
            return h;
        }
    }
}
