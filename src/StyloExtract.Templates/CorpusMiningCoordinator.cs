using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral;
using StyloExtract.Abstractions;

namespace StyloExtract.Templates;

/// <summary>
/// Periodically walks the template_rule_observations corpus and emits
/// evolved selector candidates per (LSH bucket, role) cell. Runs as a
/// background service; cadence is configurable.
///
/// <para>
/// Each pass drives <see cref="EvolvedSelectorEmitter.EmitForAllClustersAsync"/>
/// to completion before the next delay window starts. The emitter's
/// UNIQUE INDEX on (host_hash, role, target_signature) makes repeat
/// passes idempotent at the row level — a candidate already present
/// for a given target is left alone.
/// </para>
///
/// <para>
/// Opt-in via <c>StyloExtractOptions.EnableCorpusMining</c>. Default
/// off. The coordinator never touches the apply-time read path — it is
/// pure-write background work driven on a wall-clock cadence.
/// </para>
/// </summary>
public sealed class CorpusMiningCoordinator : BackgroundService
{
    private readonly EvolvedSelectorEmitter _emitter;
    private readonly TimeSpan _interval;
    private readonly ILogger<CorpusMiningCoordinator>? _logger;
    private readonly TypedSignalSink<StyloExtractSignal>? _signals;

    public CorpusMiningCoordinator(
        EvolvedSelectorEmitter emitter,
        TimeSpan interval,
        ILogger<CorpusMiningCoordinator>? logger = null,
        TypedSignalSink<StyloExtractSignal>? signals = null)
    {
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "interval must be > 0");
        _interval = interval;
        _logger = logger;
        _signals = signals;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Defer the first pass by one interval so the host has time to
        // accumulate observations before the miner runs. Without this,
        // a freshly-started service would always do a wasted first pass
        // against an empty corpus.
        try
        {
            await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var passStarted = DateTimeOffset.UtcNow;
                var clustersTouched = 0;
                var candidatesEmitted = 0;
                await foreach (var result in _emitter
                    .EmitForAllClustersAsync(ct: stoppingToken)
                    .ConfigureAwait(false))
                {
                    clustersTouched++;
                    candidatesEmitted += result.CandidatesEmitted;
                }

                var elapsed = DateTimeOffset.UtcNow - passStarted;
                _logger?.LogInformation(
                    "Corpus mining pass complete: {Clusters} cluster(s) scanned, {Candidates} candidate(s) emitted in {Elapsed}ms",
                    clustersTouched, candidatesEmitted, (long)elapsed.TotalMilliseconds);
                _signals?.Raise(
                    StyloExtractSignals.CorpusMiningPass,
                    new StyloExtractSignal(
                        ClustersTouched: clustersTouched,
                        CandidatesEmitted: candidatesEmitted,
                        ElapsedMs: (long)elapsed.TotalMilliseconds));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Per-pass failures must not crash the loop. The next
                // interval still runs; transient store / IO blips will
                // self-heal on the following pass.
                _logger?.LogError(ex, "Corpus mining pass failed");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
