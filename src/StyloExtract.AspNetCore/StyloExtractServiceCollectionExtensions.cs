using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore.Policies;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;

namespace StyloExtract.AspNetCore;

public static class StyloExtractServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full StyloExtract stack (extraction, fingerprinting, templates).
    /// Also registers a default ResponsePolicyOptions singleton for use with UseStyloExtract().
    /// </summary>
    public static IServiceCollection AddStyloExtract(this IServiceCollection services, Action<StyloExtractOptions>? configure = null)
    {
        var options = new StyloExtractOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddSingleton<ClassNoiseFilter>(_ => ClassNoiseFilter.LoadFromEmbeddedResource());
        services.AddSingleton<IHtmlDomParser, AngleSharpHtmlDomParser>();
        services.AddSingleton<IDomCleaner, DomCleaner>();
        services.AddSingleton<IBlockSegmenter, BlockSegmenter>();
        services.AddSingleton<IBlockClassifier>(_ => HeuristicBlockClassifier.LoadFromEmbeddedResources());
        services.AddSingleton<IMarkdownRenderer, TypedMarkdownRenderer>();
        services.AddSingleton<IExtractorInducer, ExtractorInducer>();
        services.AddSingleton<IExtractorApplicator>(sp =>
            new ExtractorApplicator(sp.GetService<ILogger<ExtractorApplicator>>()));

        services.AddSingleton<MinHashSketcher>(sp => new MinHashSketcher(options.Fingerprint.MinHashSize));
        services.AddSingleton<ShingleGenerator>(sp => new ShingleGenerator(sp.GetRequiredService<ClassNoiseFilter>(), options.Fingerprint.ShingleWidth));
        services.AddSingleton<LshBander>(_ => new LshBander(options.Fingerprint.LshBands, options.Fingerprint.LshRowsPerBand));
        services.AddSingleton<AnchorPathFingerprinter>(sp => new AnchorPathFingerprinter(sp.GetRequiredService<ClassNoiseFilter>(), sp.GetRequiredService<MinHashSketcher>()));
        services.AddSingleton<PqGramExtractor>(_ => new PqGramExtractor());
        services.AddSingleton<IStructuralFingerprinter, StructuralFingerprinter>();

        services.AddSingleton<HostHasher>(_ => HostHasher.FromConfiguredKeyOrRandom(options.HostHashKey));

        // SqliteTemplateIndex ctor runs schema init internally via SqliteSingleWriter bootstrap.
        var connectionString = $"Data Source={options.StorePath}";
        services.AddSingleton<ITemplateIndex>(sp => new SqliteTemplateIndex(
            connectionString,
            options.Match.AgingLambdaObs,
            options.Match.AgingLambdaRecent,
            options.Match.AgingTauDays,
            sp.GetRequiredService<TypedSignalSink<StyloExtractSignal>>()));
        services.AddSingleton<SqliteTemplateIndex>(sp => (SqliteTemplateIndex)sp.GetRequiredService<ITemplateIndex>());
        services.AddSingleton<RefitOrchestrator>(sp => new RefitOrchestrator(
            sp.GetRequiredService<SqliteTemplateIndex>(),
            sp.GetRequiredService<IExtractorInducer>(),
            options.Centroid.DriftRefitThreshold,
            options.Centroid.ObservationsBeforeStable,
            options.Centroid.VersionHistoryDepth));

        services.TryAddSingleton<ITemplateVersionEventSink, DefaultNoopVersionEventSink>();

        // Register a default TypedSignalSink<StyloExtractSignal> if none is already registered.
        // Consumers can subscribe to TypedSignalRaised to observe extraction signals.
        services.TryAddSingleton<TypedSignalSink<StyloExtractSignal>>(_ => new TypedSignalSink<StyloExtractSignal>());

        services.AddSingleton<ILayoutExtractor>(sp => new LayoutExtractor(
            sp.GetRequiredService<IHtmlDomParser>(),
            sp.GetRequiredService<IDomCleaner>(),
            sp.GetRequiredService<IStructuralFingerprinter>(),
            sp.GetRequiredService<IBlockSegmenter>(),
            sp.GetRequiredService<IBlockClassifier>(),
            sp.GetRequiredService<IMarkdownRenderer>(),
            sp.GetRequiredService<ITemplateIndex>(),
            sp.GetRequiredService<HostHasher>(),
            sp.GetRequiredService<IExtractorInducer>(),
            sp.GetRequiredService<IExtractorApplicator>(),
            options.Match.FastPathJaccardThreshold,
            options.Match.SlowPathCosineThreshold,
            sp.GetRequiredService<RefitOrchestrator>(),
            sp.GetRequiredService<ITemplateVersionEventSink>(),
            sp.GetRequiredService<TypedSignalSink<StyloExtractSignal>>(),
            sp.GetService<ILogger<LayoutExtractor>>(),
            // Operator-template store is optional. When a consumer registers an
            // IOperatorTemplateStore in DI, the LayoutExtractor consults it before
            // every fingerprint and short-circuits to MatchStatus.OperatorOverride
            // for any host with an authored template. See AddStyloExtractOperatorTemplates.
            sp.GetService<IOperatorTemplateStore>(),
            // Template-enrichment queue is optional. When a consumer registers
            // ITemplateEnrichmentQueue + TemplateEnrichmentCoordinator via
            // AddStyloExtractLlmInducer, novel templates seen here enqueue an
            // LLM enrichment job; the background coordinator drains and writes
            // the induced template into the operator-template root.
            sp.GetService<StyloExtract.Abstractions.TemplateEnrichment.ITemplateEnrichmentQueue>(),
            sp.GetService<StyloExtract.Core.Skeleton.DomSkeletonRenderer>()));

        // Register a default ResponsePolicyOptions so ResponsePolicyMiddleware is always resolvable.
        services.TryAddSingleton<ResponsePolicyOptions>();

        return services;
    }

    /// <summary>
    /// Registers the full StyloExtract stack and configures named response policies.
    /// Requires AddStyloExtractMarkdownNegotiation() to have been called first when using
    /// b.NegotiateMarkdown() inside the configurePolicy delegate.
    /// </summary>
    public static IServiceCollection AddStyloExtract(
        this IServiceCollection services,
        Action<StyloExtractOptions>? configure,
        Action<ResponsePolicyOptions>? configurePolicy)
    {
        services.AddStyloExtract(configure);

        if (configurePolicy is not null)
        {
            // Replace the TryAdd-registered descriptor with a factory that applies the delegate.
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ResponsePolicyOptions));
            if (descriptor is not null)
                services.Remove(descriptor);

            services.AddSingleton<ResponsePolicyOptions>(_ =>
            {
                var opts = new ResponsePolicyOptions();
                configurePolicy(opts);
                return opts;
            });
        }

        return services;
    }

    /// <summary>
    /// Registers the full StyloExtract stack with named response policies (StyloExtractOptions left at defaults).
    /// </summary>
    public static IServiceCollection AddStyloExtract(
        this IServiceCollection services,
        Action<ResponsePolicyOptions> configurePolicy)
        => services.AddStyloExtract(null, configurePolicy);

    /// <summary>
    /// Registers the full StyloExtract stack and configures named response policies via the fluent
    /// <see cref="ResponsePolicyBuilder"/>. This is the recommended registration path for new code.
    /// </summary>
    /// <remarks>
    /// Call <c>AddStyloExtractMarkdownNegotiation()</c> before this method when using
    /// <c>p.NegotiateMarkdown()</c> inside the builder delegate so that
    /// <see cref="StyloExtract.AspNetCore.Markdown.MarkdownNegotiationPolicy"/> is resolvable from DI.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddStyloExtract(o => o.StorePath = "styloextract.db");
    /// services.AddStyloExtractMarkdownNegotiation();
    /// services.AddStyloExtract(b =>
    /// {
    ///     b.AddPolicy("md",    p => p.NegotiateMarkdown());
    ///     b.AddPolicy("cache", p => p.CacheHints(o => o.MaxAge = TimeSpan.FromMinutes(10)));
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddStyloExtract(
        this IServiceCollection services,
        Action<ResponsePolicyBuilder> configureBuilder)
    {
        ArgumentNullException.ThrowIfNull(configureBuilder);

        // Replace any TryAdd-registered ResponsePolicyOptions descriptor with a factory
        // so the service provider is available at construction time (needed by NegotiateMarkdown()).
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(ResponsePolicyOptions));
        if (existing is not null)
            services.Remove(existing);

        services.AddSingleton<ResponsePolicyOptions>(sp =>
        {
            var builder = new ResponsePolicyBuilder(sp);
            configureBuilder(builder);
            var opts = new ResponsePolicyOptions();
            builder.ApplyNamedPoliciesTo(opts);
            return opts;
        });

        return services;
    }
}
