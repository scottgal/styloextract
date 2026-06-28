using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;
using StyloExtract.Abstractions.TemplateEnrichment;
using StyloExtract.Core.Llm;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Core.Skeleton;
using StyloExtract.Core.TemplateEnrichment;

namespace StyloExtract.Core;

/// <summary>
/// DI helpers for the LLM-driven template-induction stack (phase 3 of
/// ml-classifier-v2-design.md). Decoupled from the LLM backend:
/// register an <see cref="ILlmTextProvider"/> first (the Ollama package
/// ships <c>AddOllamaTextProvider</c>; operators can wire any other),
/// then call <see cref="AddStyloExtractLlmInducer"/> to wire the
/// queue, coordinator, inducer, and skeleton renderer.
/// </summary>
public static class StyloExtractLlmInducerExtensions
{
    /// <summary>
    /// Wires the full template-induction background stack:
    /// <see cref="DomSkeletonRenderer"/>, <see cref="LlmTemplateInducer"/>,
    /// <see cref="InMemoryTemplateEnrichmentQueue"/> as
    /// <see cref="ITemplateEnrichmentQueue"/>, and
    /// <see cref="TemplateEnrichmentCoordinator"/> as an
    /// <see cref="IHostedService"/>.
    ///
    /// <para>Prerequisites the caller wires elsewhere:</para>
    /// <list type="bullet">
    ///   <item><see cref="ILlmTextProvider"/> — e.g.
    ///     <c>services.AddOllamaTextProvider()</c> from the Ollama package,
    ///     or a custom provider.</item>
    ///   <item><see cref="IOperatorTemplateStore"/> — typically via
    ///     <see cref="StyloExtractOperatorTemplatesExtensions.AddStyloExtractOperatorTemplates"/>.
    ///     The induced YAML is written into the same root the store watches,
    ///     so the LLM result becomes a hand-editable operator template the
    ///     hard-override path picks up on the next request.</item>
    /// </list>
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="operatorTemplateRoot">Directory the coordinator writes
    /// induced YAML files into. Must match the root passed to
    /// AddStyloExtractOperatorTemplates so the file-watching store sees
    /// the writes.</param>
    /// <param name="configureQueue">Optional callback to tune queue
    /// capacity / per-host cooldown / max job age.</param>
    /// <param name="configureCoordinator">Optional callback to tune
    /// the inter-call interval (global QPS cap).</param>
    public static IServiceCollection AddStyloExtractLlmInducer(
        this IServiceCollection services,
        string operatorTemplateRoot,
        Action<EnrichmentQueueOptions>? configureQueue = null,
        Action<EnrichmentCoordinatorOptions>? configureCoordinator = null)
    {
        if (string.IsNullOrWhiteSpace(operatorTemplateRoot))
            throw new ArgumentException("operatorTemplateRoot required", nameof(operatorTemplateRoot));

        var queueOptions = EnrichmentQueueOptions.Default;
        configureQueue?.Invoke(queueOptions = new EnrichmentQueueOptions());

        var coordOptions = EnrichmentCoordinatorOptions.Default;
        configureCoordinator?.Invoke(coordOptions = new EnrichmentCoordinatorOptions());

        // Renderer is a stateless utility; share one instance across the
        // hot path (LayoutExtractor.MaybeEnqueueEnrichmentAsync) and the
        // background coordinator (LlmTemplateInducer.InduceAsync overload).
        services.TryAddSingleton<DomSkeletonRenderer>();

        services.TryAddSingleton<ITemplateEnrichmentQueue>(sp =>
            new InMemoryTemplateEnrichmentQueue(queueOptions,
                sp.GetService<ILogger<InMemoryTemplateEnrichmentQueue>>()));

        services.TryAddSingleton<LlmTemplateInducer>(sp =>
            new LlmTemplateInducer(
                sp.GetRequiredService<ILlmTextProvider>(),
                skeleton: sp.GetRequiredService<DomSkeletonRenderer>(),
                logger: sp.GetService<ILogger<LlmTemplateInducer>>()));

        services.AddHostedService(sp =>
            new TemplateEnrichmentCoordinator(
                sp.GetRequiredService<ITemplateEnrichmentQueue>(),
                sp.GetRequiredService<LlmTemplateInducer>(),
                operatorTemplateRoot,
                sp.GetService<IOperatorTemplateStore>(),
                coordOptions,
                sp.GetService<ILogger<TemplateEnrichmentCoordinator>>(),
                sp.GetService<ILlmActivityObserver>()));

        return services;
    }
}
