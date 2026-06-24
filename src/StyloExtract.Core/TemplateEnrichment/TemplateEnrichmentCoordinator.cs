using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;
using StyloExtract.Abstractions.TemplateEnrichment;
using StyloExtract.Core.Llm;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Html;

namespace StyloExtract.Core.TemplateEnrichment;

/// <summary>
/// Background <see cref="BackgroundService"/> that drains
/// <see cref="ITemplateEnrichmentQueue"/>, invokes
/// <see cref="LlmTemplateInducer"/> on each job, and persists the
/// resulting <see cref="OperatorTemplate"/> as a YAML file in the
/// operator-template root. The existing
/// <c>YamlFileOperatorTemplateStore</c> FileSystemWatcher picks the
/// file up and the next request for that host hits the hard-override
/// path with the LLM-induced template.
///
/// <para>
/// Slow-path only. Runs at most one induction at a time (configurable);
/// each induction blocks within the coordinator's loop, not the request
/// hot path. Per-host cooldown is enforced by the queue itself; this
/// service adds a global QPS throttle and graceful shutdown.
/// </para>
///
/// <para>
/// Persistence path: the induced template is round-tripped through
/// <see cref="YamlOperatorTemplateLoader"/> + <see cref="OperatorTemplateYamlEmitter"/>
/// so the on-disk file matches the canonical operator-template shape
/// (operators can hand-edit it; the file watcher reloads; the
/// hard-override path picks it up automatically). An induction whose
/// output the parser refuses is logged and discarded.
/// </para>
/// </summary>
public sealed class TemplateEnrichmentCoordinator : BackgroundService
{
    private readonly ITemplateEnrichmentQueue _queue;
    private readonly LlmTemplateInducer _inducer;
    private readonly string _operatorTemplateRoot;
    private readonly IOperatorTemplateStore? _operatorTemplateStore;
    private readonly EnrichmentCoordinatorOptions _options;
    private readonly ILogger<TemplateEnrichmentCoordinator>? _logger;

    public TemplateEnrichmentCoordinator(
        ITemplateEnrichmentQueue queue,
        LlmTemplateInducer inducer,
        string operatorTemplateRoot,
        IOperatorTemplateStore? operatorTemplateStore = null,
        EnrichmentCoordinatorOptions? options = null,
        ILogger<TemplateEnrichmentCoordinator>? logger = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _inducer = inducer ?? throw new ArgumentNullException(nameof(inducer));
        _operatorTemplateRoot = operatorTemplateRoot ?? throw new ArgumentNullException(nameof(operatorTemplateRoot));
        _operatorTemplateStore = operatorTemplateStore;
        _options = options ?? EnrichmentCoordinatorOptions.Default;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation(
            "TemplateEnrichmentCoordinator started; root={Root}, minInterval={MinInterval}",
            _operatorTemplateRoot, _options.MinInterCallInterval);
        try
        {
            await foreach (var job in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
                if (_options.MinInterCallInterval > TimeSpan.Zero)
                {
                    // Cheap global QPS limiter: sleep between calls. Avoids
                    // hammering the LLM backend even when the queue is
                    // momentarily full.
                    await Task.Delay(_options.MinInterCallInterval, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TemplateEnrichmentCoordinator drain loop crashed");
            throw;
        }
    }

    private async Task ProcessJobAsync(TemplateEnrichmentJob job, CancellationToken cancellationToken)
    {
        // Skip if an operator-written template already exists for this host
        // — hand-authored takes precedence over induced, always.
        if (_operatorTemplateStore is not null &&
            _operatorTemplateStore.TryGet(job.Host, out _))
        {
            _logger?.LogDebug(
                "skipping enrichment for {Host}: hand-authored operator template exists",
                job.Host);
            return;
        }

        OperatorTemplate? template;
        try
        {
            template = await _inducer.InduceFromSkeletonAsync(
                job.Skeleton, job.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "induction crashed for {Host}; skipping job", job.Host);
            return;
        }

        if (template is null)
        {
            _logger?.LogInformation(
                "induction returned null for {Host}; heuristic-induced template stays in place",
                job.Host);
            return;
        }

        // Validate selector minimum: an "induced" template with zero
        // MainContent selectors is worse than the heuristic; don't write it.
        if (!template.Rules.Any(r => r.Role == BlockRole.MainContent))
        {
            _logger?.LogInformation(
                "induction for {Host} produced no MainContent rule; skipping",
                job.Host);
            return;
        }

        await WriteTemplateAsync(template, job, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteTemplateAsync(
        OperatorTemplate template,
        TemplateEnrichmentJob job,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_operatorTemplateRoot);
        var path = Path.Combine(_operatorTemplateRoot, template.Host + ".yaml");

        // Stamp the description so operators can tell which templates the
        // LLM induced vs which they wrote by hand. The host field is
        // already canonical (LlmTemplateInducer rewrites hallucinations).
        var stamped = template with
        {
            Description = string.IsNullOrEmpty(template.Description)
                ? $"Induced from fingerprint {job.FingerprintHex} on {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z"
                : template.Description,
        };

        var yaml = OperatorTemplateYamlEmitter.Emit(stamped);
        try
        {
            await File.WriteAllTextAsync(path, yaml, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation(
                "induced template written for {Host}: {RuleCount} rule(s) to {Path}",
                job.Host, stamped.Rules.Count, path);
            (_operatorTemplateStore as IDisposable)?.Dispose();
            // Force the store to re-scan immediately (file-watcher event
            // can lag on network mounts or Linux distros without inotify).
            if (_operatorTemplateStore is YamlFileOperatorTemplateStore yfs)
            {
                yfs.Reload();
            }
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex,
                "failed to write induced template for {Host} to {Path}", job.Host, path);
        }
    }
}

/// <summary>
/// Tunables for <see cref="TemplateEnrichmentCoordinator"/>. Defaults
/// match the design doc's "10 LLM QPS max" target.
/// </summary>
public sealed record EnrichmentCoordinatorOptions
{
    /// <summary>
    /// Minimum wall-clock delay between two consecutive LLM calls. With
    /// the default (100 ms) the coordinator can sustain ~10 QPS at most;
    /// realistic throughput is far lower because each induction takes
    /// 5-30s itself.
    /// </summary>
    public TimeSpan MinInterCallInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public static EnrichmentCoordinatorOptions Default { get; } = new();
}
