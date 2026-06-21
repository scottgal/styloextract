using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace StyloExtract.AspNetCore.Markdown;

public static class MarkdownNegotiationApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the <see cref="StyloExtractMarkdownMiddleware"/> to the pipeline.
    /// Responses with Content-Type text/html are transparently converted to Markdown
    /// when the client sends <c>Accept: text/markdown</c>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="configure">Optional delegate to override <see cref="MarkdownNegotiationOptions"/>.</param>
    public static IApplicationBuilder UseStyloExtractMarkdownNegotiation(
        this IApplicationBuilder app,
        Action<MarkdownNegotiationOptions>? configure = null)
    {
        if (configure is not null)
        {
            var monitor = app.ApplicationServices.GetRequiredService<IOptionsMonitor<MarkdownNegotiationOptions>>();
            // Apply the delegate to the current snapshot so in-process overrides take effect.
            configure(monitor.CurrentValue);
        }

        return app.UseMiddleware<StyloExtractMarkdownMiddleware>();
    }
}
