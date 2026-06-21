using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

        return services;
    }
}
