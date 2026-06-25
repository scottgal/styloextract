using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Core.TemplateEnrichment;

namespace StyloExtract.AspNetCore;

/// <summary>
/// Backward-compatible forwarding stubs for AspNetCore consumers.
/// The implementation has moved to <c>StyloExtract.Core.StyloExtractLlmInducerExtensions</c>.
/// </summary>
public static class StyloExtractLlmInducerExtensions
{
    /// <inheritdoc cref="StyloExtract.Core.StyloExtractLlmInducerExtensions.AddStyloExtractLlmInducer"/>
    public static IServiceCollection AddStyloExtractLlmInducer(
        this IServiceCollection services,
        string operatorTemplateRoot,
        Action<EnrichmentQueueOptions>? configureQueue = null,
        Action<EnrichmentCoordinatorOptions>? configureCoordinator = null)
        => StyloExtract.Core.StyloExtractLlmInducerExtensions.AddStyloExtractLlmInducer(
            services, operatorTemplateRoot, configureQueue, configureCoordinator);
}
