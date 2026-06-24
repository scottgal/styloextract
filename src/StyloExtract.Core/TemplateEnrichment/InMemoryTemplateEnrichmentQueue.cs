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
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEnqueuedByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _perHostCooldown;
    private readonly TimeSpan _maxJobAge;
    private readonly ILogger<InMemoryTemplateEnrichmentQueue>? _logger;

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

        // Per-host cooldown: if this host was enqueued recently, drop the
        // duplicate silently. The producer doesn't need to know; the
        // outstanding job will satisfy this host's enrichment too.
        var now = DateTimeOffset.UtcNow;
        if (_lastEnqueuedByHost.TryGetValue(job.Host, out var lastAt) &&
            now - lastAt < _perHostCooldown)
        {
            _logger?.LogTrace("dropping enrichment for {Host}: cooldown active", job.Host);
            return new ValueTask<bool>(false);
        }

        if (!_channel.Writer.TryWrite(job))
        {
            _logger?.LogTrace("dropping enrichment for {Host}: queue full", job.Host);
            return new ValueTask<bool>(false);
        }
        _lastEnqueuedByHost[job.Host] = now;
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
