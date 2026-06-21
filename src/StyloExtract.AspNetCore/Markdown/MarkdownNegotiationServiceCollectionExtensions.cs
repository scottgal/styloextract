using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace StyloExtract.AspNetCore.Markdown;

public static class MarkdownNegotiationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MarkdownNegotiationOptions"/> and the
    /// <see cref="StyloExtractMarkdownMiddleware"/> for use with
    /// <see cref="MarkdownNegotiationApplicationBuilderExtensions.UseStyloExtractMarkdownNegotiation"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="AddStyloExtract"/> must also be called so that
    /// <c>ILayoutExtractor</c> is available in the DI container.
    /// If no <see cref="IDistributedCache"/> is already registered, an in-memory implementation
    /// is registered automatically so caching works out of the box without external dependencies.
    /// </remarks>
    public static IServiceCollection AddStyloExtractMarkdownNegotiation(
        this IServiceCollection services,
        Action<MarkdownNegotiationOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<MarkdownNegotiationOptions>();

        // IHttpContextAccessor is needed by StyloExtractResults.HtmlOrMarkdown.
        services.AddHttpContextAccessor();

        // Register an in-memory IDistributedCache only when nothing else is registered.
        // Consumers can register a real distributed cache before calling this method to override.
        services.TryAddSingleton<IDistributedCache, MemoryDistributedCache>();
        services.AddDistributedMemoryCache();

        return services;
    }
}
