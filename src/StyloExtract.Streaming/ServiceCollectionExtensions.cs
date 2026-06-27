using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace StyloExtract.Streaming;

/// <summary>
/// DI helpers for wiring the streaming fence scanner / inducer / refit
/// stack. Mirrors the existing <c>AddStyloExtract</c> /
/// <c>AddStyloExtractOperatorTemplates</c> / <c>AddStyloExtractLlamaSharp</c>
/// pattern in sibling packages: one call registers everything a consumer
/// needs, with an options-builder for the bits that vary by deployment
/// shape.
/// </summary>
public static class StreamingServiceCollectionExtensions
{
    /// <summary>
    /// Register the streaming pipeline: <see cref="IStreamingTemplateStore"/>,
    /// <see cref="StreamingPathSelector"/>, <see cref="StreamingTemplateInducer"/>,
    /// and <see cref="StreamingRefitOrchestrator"/>.
    ///
    /// <para>
    /// Defaults to <see cref="InMemoryStreamingTemplateStore"/> (no persistence).
    /// Set <see cref="StreamingOptions.SqlitePath"/> on the configuration
    /// action to opt into <see cref="SqliteStreamingTemplateStore"/> instead.
    /// </para>
    ///
    /// <para>
    /// All registrations use <c>TryAddSingleton</c>, so a consumer-supplied
    /// <see cref="IStreamingTemplateVersionSink"/> registered before <em>or</em>
    /// after this call wins over the default no-op sink. If no sink is registered
    /// the orchestrator resolves <see cref="NoopStreamingTemplateVersionSink"/>
    /// and refit events become no-ops.
    /// </para>
    ///
    /// <example>
    /// <code>
    /// // InMemory store (default — no persistence)
    /// services.AddStyloExtractStreaming();
    ///
    /// // SQLite store with persistence path
    /// services.AddStyloExtractStreaming(o =>
    /// {
    ///     o.SqlitePath = Path.Combine(AppPaths.LocalState, "streaming-templates.db");
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddStyloExtractStreaming(
        this IServiceCollection services,
        Action<StreamingOptions>? configure = null)
    {
        var options = new StreamingOptions();
        configure?.Invoke(options);

        if (!string.IsNullOrWhiteSpace(options.SqlitePath))
        {
            var connectionString = $"Data Source={options.SqlitePath}";
            services.TryAddSingleton<IStreamingTemplateStore>(_ =>
                new SqliteStreamingTemplateStore(connectionString));
        }
        else
        {
            services.TryAddSingleton<IStreamingTemplateStore, InMemoryStreamingTemplateStore>();
        }

        // Default no-op sink — consumer-registered sinks (registered before or
        // after this call) win because of TryAddSingleton.
        services.TryAddSingleton<IStreamingTemplateVersionSink, NoopStreamingTemplateVersionSink>();

        services.TryAddSingleton<StreamingPathSelector>();
        services.TryAddSingleton<StreamingTemplateInducer>();
        services.TryAddSingleton<StreamingRefitOrchestrator>(sp =>
            new StreamingRefitOrchestrator(
                sp.GetRequiredService<IStreamingTemplateStore>(),
                sp.GetRequiredService<StreamingTemplateInducer>(),
                sp.GetService<IStreamingTemplateVersionSink>(),
                options.RelativeDriftThreshold,
                options.DriftBailoutCount,
                options.ScansPerForcedRefit));

        return services;
    }
}

/// <summary>
/// Options for <see cref="StreamingServiceCollectionExtensions.AddStyloExtractStreaming"/>.
/// </summary>
public sealed class StreamingOptions
{
    /// <summary>
    /// When non-null/non-empty, the streaming-template store is
    /// <see cref="SqliteStreamingTemplateStore"/> rooted at this file path.
    /// When null/empty (the default), the store is
    /// <see cref="InMemoryStreamingTemplateStore"/> — single-process,
    /// no persistence.
    /// </summary>
    public string? SqlitePath { get; set; }

    /// <summary>
    /// Capture-range EWMA relative-drift threshold for the refit orchestrator.
    /// Defaults to <see cref="StreamingRefitOrchestrator.DefaultRelativeDriftThreshold"/>.
    /// </summary>
    public double RelativeDriftThreshold { get; set; } =
        StreamingRefitOrchestrator.DefaultRelativeDriftThreshold;

    /// <summary>
    /// Consecutive drifty observations needed before a forced refit fires.
    /// Defaults to <see cref="StreamingRefitOrchestrator.DefaultDriftBailoutCount"/>.
    /// </summary>
    public int DriftBailoutCount { get; set; } =
        StreamingRefitOrchestrator.DefaultDriftBailoutCount;

    /// <summary>
    /// Every Nth captured scan triggers a cadence refit regardless of drift.
    /// Defaults to <see cref="StreamingRefitOrchestrator.DefaultScansPerForcedRefit"/>.
    /// </summary>
    public int ScansPerForcedRefit { get; set; } =
        StreamingRefitOrchestrator.DefaultScansPerForcedRefit;
}
