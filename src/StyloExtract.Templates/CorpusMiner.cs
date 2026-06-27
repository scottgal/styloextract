using System.IO.Hashing;
using System.Text;
using StyloExtract.Abstractions;

namespace StyloExtract.Templates;

/// <summary>
/// Read-only query primitives over the <c>template_rule_observations</c> corpus.
/// Composes observation-cluster queries from <see cref="ITemplateIndex"/> with
/// <see cref="SelectorDistance"/> to surface mining signals: the median chain
/// across a cluster, the stable substructure shared by most members, outliers
/// that need re-induction, and a per-position attribute-frequency report for
/// diagnostics.
///
/// No persistent state. No writes. The Phase 2 miner composes these to emit
/// new template-rule candidates; consumers of this class own the storage and
/// promotion decisions.
/// </summary>
public sealed class CorpusMiner
{
    private readonly ITemplateIndex _index;

    public CorpusMiner(ITemplateIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <summary>
    /// Compute the median selector chain across all observations in a cluster
    /// for a given role. "Median" is the observation whose summed
    /// <see cref="SelectorDistance"/> to every other observation in the group
    /// is minimised. O(N^2) in cluster size; callers can cap with the index's
    /// observation-query limit.
    ///
    /// Returns <c>null</c> when fewer than 3 observations exist in the cluster
    /// for the role — a median is not meaningful for trivial groups.
    /// </summary>
    public async ValueTask<IReadOnlyList<IdentityClaim>?> ComputeMedianAsync(
        int lshBucket, BlockRole role, CancellationToken ct = default)
    {
        var observations = await _index.GetObservationsByBucketAsync(lshBucket, role, limit: 1000, ct)
            .ConfigureAwait(false);

        if (observations.Count < 3) return null;

        var n = observations.Count;
        var sums = new double[n];

        // Symmetric distance matrix; only fill the upper triangle and mirror.
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var d = SelectorDistance.Compute(observations[i].Claims, observations[j].Claims);
                sums[i] += d;
                sums[j] += d;
            }
        }

        var bestIdx = 0;
        var bestSum = sums[0];
        for (var i = 1; i < n; i++)
        {
            if (sums[i] < bestSum)
            {
                bestSum = sums[i];
                bestIdx = i;
            }
        }

        return observations[bestIdx].Claims;
    }

    /// <summary>
    /// Find the stable substructure across the cluster: per-position
    /// intersection of attribute claims (tag, id, classes, data-*, aria-*,
    /// role) filtered by <paramref name="minPresenceRatio"/>. Synthesises a
    /// claim chain anchored from the leaf (target) backwards. Trailing
    /// positions with no qualifying attributes are trimmed.
    ///
    /// Returns <c>null</c> when the cluster is empty or the synthesised chain
    /// would be entirely empty (no attribute met the presence threshold at the
    /// leaf).
    /// </summary>
    public async ValueTask<IReadOnlyList<IdentityClaim>?> ComputeStableSubsequenceAsync(
        int lshBucket, BlockRole role, double minPresenceRatio = 0.7, CancellationToken ct = default)
    {
        var observations = await _index.GetObservationsByBucketAsync(lshBucket, role, limit: 1000, ct)
            .ConfigureAwait(false);

        if (observations.Count == 0) return null;

        var maxDepth = 0;
        foreach (var obs in observations)
        {
            if (obs.Claims.Count > maxDepth) maxDepth = obs.Claims.Count;
        }
        if (maxDepth == 0) return null;

        var total = observations.Count;
        var threshold = (int)Math.Ceiling(total * minPresenceRatio);
        if (threshold < 1) threshold = 1;

        // Build per-position aggregates from leaf (pos 0) backwards.
        var synthesised = new List<IdentityClaim>(maxDepth);

        for (var pos = 0; pos < maxDepth; pos++)
        {
            var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var classCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var dataCounts = new Dictionary<(string Key, string Value), int>();
            var ariaCounts = new Dictionary<(string Key, string Value), int>();
            var roleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var obs in observations)
            {
                var idx = obs.Claims.Count - 1 - pos;
                if (idx < 0) continue;
                var claim = obs.Claims[idx];
                Increment(tagCounts, claim.Tag);
                if (claim.Id is not null) Increment(idCounts, claim.Id);
                foreach (var c in claim.Classes) Increment(classCounts, c);
                foreach (var kv in claim.DataAttrs) Increment(dataCounts, (kv.Key, kv.Value));
                foreach (var kv in claim.AriaAttrs) Increment(ariaCounts, (kv.Key, kv.Value));
                if (claim.Role is not null) Increment(roleCounts, claim.Role);
            }

            // Pick winners that meet the threshold. Tag: pick the most-frequent
            // tag that crosses; if none crosses, this position has no stable
            // identity.
            string? winningTag = null;
            var winningTagCount = 0;
            foreach (var kv in tagCounts)
            {
                if (kv.Value >= threshold && kv.Value > winningTagCount)
                {
                    winningTag = kv.Key;
                    winningTagCount = kv.Value;
                }
            }

            if (winningTag is null)
            {
                // No stable tag at this depth. Emit an empty placeholder so
                // alignment with deeper ancestors stays correct. Will be
                // trimmed at the end if it's a trailing run of empties.
                synthesised.Add(EmptyClaim());
                continue;
            }

            string? winningId = null;
            var winningIdCount = 0;
            foreach (var kv in idCounts)
            {
                if (kv.Value >= threshold && kv.Value > winningIdCount)
                {
                    winningId = kv.Key;
                    winningIdCount = kv.Value;
                }
            }

            var stableClasses = new List<string>();
            foreach (var kv in classCounts)
            {
                if (kv.Value >= threshold) stableClasses.Add(kv.Key);
            }
            stableClasses.Sort(StringComparer.Ordinal);

            var stableData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in dataCounts)
            {
                if (kv.Value >= threshold) stableData[kv.Key.Key] = kv.Key.Value;
            }

            var stableAria = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in ariaCounts)
            {
                if (kv.Value >= threshold) stableAria[kv.Key.Key] = kv.Key.Value;
            }

            string? winningRole = null;
            var winningRoleCount = 0;
            foreach (var kv in roleCounts)
            {
                if (kv.Value >= threshold && kv.Value > winningRoleCount)
                {
                    winningRole = kv.Key;
                    winningRoleCount = kv.Value;
                }
            }

            synthesised.Add(BuildClaim(winningTag, winningId, stableClasses, stableData, stableAria, winningRole));
        }

        // Trim trailing all-empty positions (no stable tag at outer ancestors).
        while (synthesised.Count > 0 && IsEmpty(synthesised[^1]))
        {
            synthesised.RemoveAt(synthesised.Count - 1);
        }

        if (synthesised.Count == 0) return null;

        // The list is leaf-first; templates store outermost-first → leaf last.
        // Mirror that convention so callers can compare directly with
        // BlockRule.Claims / TemplateObservation.Claims.
        synthesised.Reverse();
        return synthesised;
    }

    /// <summary>
    /// Identify outlier observations whose <see cref="SelectorDistance"/> to
    /// the cluster median exceeds <paramref name="outlierThreshold"/>. When no
    /// median is available (fewer than 3 observations) the stable-subsequence
    /// chain is used as the reference; if that is also unavailable, returns
    /// an empty list. Ordered by distance descending — biggest outlier first.
    /// </summary>
    public async ValueTask<IReadOnlyList<TemplateObservation>> FindOutliersAsync(
        int lshBucket, BlockRole role, double outlierThreshold = 5.0, CancellationToken ct = default)
    {
        var observations = await _index.GetObservationsByBucketAsync(lshBucket, role, limit: 1000, ct)
            .ConfigureAwait(false);

        if (observations.Count == 0) return Array.Empty<TemplateObservation>();

        var reference = await ComputeMedianAsync(lshBucket, role, ct).ConfigureAwait(false)
            ?? await ComputeStableSubsequenceAsync(lshBucket, role, ct: ct).ConfigureAwait(false);

        if (reference is null) return Array.Empty<TemplateObservation>();

        var scored = new List<(TemplateObservation Obs, double Distance)>(observations.Count);
        foreach (var obs in observations)
        {
            var d = SelectorDistance.Compute(obs.Claims, reference);
            if (d > outlierThreshold) scored.Add((obs, d));
        }

        scored.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        var result = new TemplateObservation[scored.Count];
        for (var i = 0; i < scored.Count; i++) result[i] = scored[i].Obs;
        return result;
    }

    /// <summary>
    /// Per-position attribute frequency table for the cluster + role. Useful
    /// for visualising the corpus or feeding alternate candidate emitters.
    /// Position index 0 = leaf (target), increasing = ancestor depth.
    /// </summary>
    public async ValueTask<ClusterFrequencyReport> AnalyseClusterAsync(
        int lshBucket, BlockRole role, CancellationToken ct = default)
    {
        var observations = await _index.GetObservationsByBucketAsync(lshBucket, role, limit: 1000, ct)
            .ConfigureAwait(false);

        if (observations.Count == 0)
        {
            return new ClusterFrequencyReport
            {
                LshBucket = lshBucket,
                Role = role,
                ObservationCount = 0,
                Positions = Array.Empty<PositionFrequency>(),
            };
        }

        var maxDepth = 0;
        foreach (var obs in observations)
        {
            if (obs.Claims.Count > maxDepth) maxDepth = obs.Claims.Count;
        }

        var positions = new List<PositionFrequency>(maxDepth);
        for (var pos = 0; pos < maxDepth; pos++)
        {
            var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var classCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var dataCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var obs in observations)
            {
                var idx = obs.Claims.Count - 1 - pos;
                if (idx < 0) continue;
                var claim = obs.Claims[idx];
                Increment(tagCounts, claim.Tag);
                if (claim.Id is not null) Increment(idCounts, claim.Id);
                foreach (var c in claim.Classes) Increment(classCounts, c);
                foreach (var kv in claim.DataAttrs) Increment(dataCounts, kv.Key);
            }

            positions.Add(new PositionFrequency
            {
                PositionFromLeaf = pos,
                TagCounts = tagCounts,
                IdCounts = idCounts,
                ClassCounts = classCounts,
                DataAttrKeyCounts = dataCounts,
            });
        }

        return new ClusterFrequencyReport
        {
            LshBucket = lshBucket,
            Role = role,
            ObservationCount = observations.Count,
            Positions = positions,
        };
    }

    private static void Increment<T>(Dictionary<T, int> dict, T key) where T : notnull
    {
        if (dict.TryGetValue(key, out var c)) dict[key] = c + 1;
        else dict[key] = 1;
    }

    private static IdentityClaim BuildClaim(
        string tag,
        string? id,
        IReadOnlyList<string> classes,
        IReadOnlyDictionary<string, string> dataAttrs,
        IReadOnlyDictionary<string, string> ariaAttrs,
        string? role)
    {
        var tagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag));
        ulong? idHash = id is null ? null : XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(id));

        var classArr = classes.Count == 0 ? Array.Empty<string>() : classes.ToArray();
        var classHashes = classes.Count == 0 ? Array.Empty<ulong>() : new ulong[classes.Count];
        for (var i = 0; i < classes.Count; i++)
        {
            classHashes[i] = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(classes[i]));
        }

        return new IdentityClaim
        {
            Tag = tag,
            TagHash = tagHash,
            Id = id,
            IdHash = idHash,
            Classes = classArr,
            ClassHashes = classHashes,
            DataAttrs = dataAttrs,
            AriaAttrs = ariaAttrs,
            Role = role,
        };
    }

    private static IdentityClaim EmptyClaim()
    {
        // Placeholder used during the leaf-first build so alignment stays
        // intact. Trimmed before the chain is returned.
        return new IdentityClaim { Tag = string.Empty, TagHash = 0UL };
    }

    private static bool IsEmpty(IdentityClaim claim)
        => claim.Tag.Length == 0 && claim.TagHash == 0UL;
}

/// <summary>
/// Per-position attribute frequency report for a cluster (LSH bucket + role).
/// </summary>
public sealed record ClusterFrequencyReport
{
    public required int LshBucket { get; init; }
    public required BlockRole Role { get; init; }
    public required int ObservationCount { get; init; }
    public required IReadOnlyList<PositionFrequency> Positions { get; init; }
}

/// <summary>
/// Frequency counts at one position in the leaf-anchored chain.
/// <see cref="PositionFromLeaf"/> 0 = leaf (target), 1 = parent, etc.
/// </summary>
public sealed record PositionFrequency
{
    public required int PositionFromLeaf { get; init; }
    public required IReadOnlyDictionary<string, int> TagCounts { get; init; }
    public required IReadOnlyDictionary<string, int> IdCounts { get; init; }
    public required IReadOnlyDictionary<string, int> ClassCounts { get; init; }
    public required IReadOnlyDictionary<string, int> DataAttrKeyCounts { get; init; }
}
