using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions.TemplateEnrichment;

namespace StyloExtract.Core.TemplateEnrichment;

/// <summary>
/// Default in-memory queue implementation. Bounded channel + per-host
/// cooldown dedup. Drops silently when the cooldown is still active or
/// the channel is full — both signal "the LLM is busy or the host has
/// been induced recently, don't pile on."
///
/// <para>
/// Survives nothing across process restarts. That's fine for the
/// design: the cost of a dropped enrichment is "we re-induce when the
/// host is next visited," not data loss. Operators who want persistence
/// can layer a different implementation behind the same interface.
/// </para>
/// </summary>
public sealed class InMemoryTemplateEnrichmentQueue : ITemplateEnrichmentQueue, IDisposable
{
    private readonly Channel<TemplateEnrichmentJob> _channel;
    // Keyed by (host, kind). Induce and Repair for the same host get
    // independent cooldown slots so a first-visit Induce can't silently
    // block a later Repair (the apply-time bug-out signal that says "the
    // template you induced was wrong, please redo").
    private readonly ConcurrentDictionary<(string Host, EnrichmentJobKind Kind), DateTimeOffset> _lastEnqueued =
        new(new HostKindComparer());
    private readonly TimeSpan _perHostCooldown;
    private readonly TimeSpan _maxJobAge;
    private readonly ILogger<InMemoryTemplateEnrichmentQueue>? _logger;

    private sealed class HostKindComparer : IEqualityComparer<(string Host, EnrichmentJobKind Kind)>
    {
        public bool Equals((string Host, EnrichmentJobKind Kind) x, (string Host, EnrichmentJobKind Kind) y) =>
            x.Kind == y.Kind && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Host, EnrichmentJobKind Kind) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Host),
                (int)obj.Kind);
    }

    public InMemoryTemplateEnrichmentQueue(
        EnrichmentQueueOptions? options = null,
        ILogger<InMemoryTemplateEnrichmentQueue>? logger = null)
    {
        options ??= EnrichmentQueueOptions.Default;
        _perHostCooldown = options.PerHostCooldown;
        _maxJobAge = options.MaxJobAge;
        _channel = Channel.CreateBounded<TemplateEnrichmentJob>(
            new BoundedChannelOptions(options.Capacity)
            {
                FullMode = BoundedChannelFullMode.DropNewest,
                SingleReader = true,
                SingleWriter = false,
            });
        _logger = logger;
    }

    public ValueTask<bool> TryEnqueueAsync(TemplateEnrichmentJob job, CancellationToken cancellationToken = default)
    {
        if (job is null) throw new ArgumentNullException(nameof(job));

        // Per-(host, kind) cooldown: if this host already has an active job
        // of the SAME kind, drop the duplicate. Induce and Repair for the
        // same host don't compete for the same slot — Repair after a recent
        // Induce represents new information (apply-time bug-out), not a
        // duplicate.
        var now = DateTimeOffset.UtcNow;
        var key = (job.Host, job.Kind);
        if (_lastEnqueued.TryGetValue(key, out var lastAt) &&
            now - lastAt < _perHostCooldown)
        {
            _logger?.LogTrace("dropping enrichment for {Host} ({Kind}): cooldown active", job.Host, job.Kind);
            return new ValueTask<bool>(false);
        }

        if (!_channel.Writer.TryWrite(job))
        {
            _logger?.LogTrace("dropping enrichment for {Host} ({Kind}): queue full", job.Host, job.Kind);
            return new ValueTask<bool>(false);
        }
        _lastEnqueued[key] = now;
        return new ValueTask<bool>(true);
    }

    public async IAsyncEnumerable<TemplateEnrichmentJob> DequeueAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // Age-out: if the job has been sitting in the queue longer
            // than MaxJobAge, the cached extractor it would update has
            // probably already drifted or been refit by other paths.
            // Drop the stale job; the next visit to the host re-enqueues.
            if (DateTimeOffset.UtcNow - job.CreatedAt > _maxJobAge)
            {
                _logger?.LogTrace("dropping enrichment for {Host}: job age {Age} > {Max}",
                    job.Host, DateTimeOffset.UtcNow - job.CreatedAt, _maxJobAge);
                continue;
            }
            yield return job;
        }
    }

    /// <summary>
    /// Mark the queue complete. Drain loops exit cleanly after this.
    /// Called by the coordinator on host shutdown.
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();

    public void Dispose() => Complete();
}

/// <summary>
/// Tunables for <see cref="InMemoryTemplateEnrichmentQueue"/>. Defaults
/// match the design doc's "10 LLM QPS, 1 hour per-host cooldown" target.
/// </summary>
public sealed record EnrichmentQueueOptions
{
    /// <summary>Maximum jobs the queue holds before drops start.</summary>
    public int Capacity { get; init; } = 256;

    /// <summary>Cooldown between consecutive enqueues for the same host.</summary>
    public TimeSpan PerHostCooldown { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum age before a queued job is dropped on dequeue. Prevents
    /// the queue from servicing stale jobs after a quiet period.
    /// </summary>
    public TimeSpan MaxJobAge { get; init; } = TimeSpan.FromMinutes(30);

    public static EnrichmentQueueOptions Default { get; } = new();
}
