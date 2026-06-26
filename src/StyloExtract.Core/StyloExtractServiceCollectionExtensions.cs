using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral;
using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;

namespace StyloExtract.Core;

public static class StyloExtractServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full StyloExtract stack (extraction, fingerprinting, templates).
    /// Safe to call from any host type (desktop, CLI, worker service, ASP.NET Core).
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
            sp.GetService<StyloExtract.Core.Skeleton.DomSkeletonRenderer>(),
            // Deterministic-template YAML sink (alpha.11+). Optional — when
            // AddStyloExtractOperatorTemplates is called it registers the sink
            // here so LayoutExtractor writes <host>-deterministic.yaml on each
            // induced extractor. alpha.11 introduced the sink but forgot to
            // pass it through here; fixed in alpha.12.
            sp.GetService<StyloExtract.Core.OperatorTemplates.DeterministicTemplateYamlSink>()));

        return services;
    }
}
