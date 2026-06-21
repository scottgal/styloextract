using Microsoft.Extensions.DependencyInjection;
using StyloExtract.AspNetCore.Policies;

namespace StyloExtract.AspNetCore.Markdown;

/// <summary>
/// Extends ResponsePolicyBuilder with the NegotiateMarkdown() fluent method.
/// </summary>
public static class MarkdownNegotiationPolicyBuilderExtensions
{
    /// <summary>
    /// Configures this builder to apply Markdown content negotiation.
    /// Requires MarkdownNegotiationPolicy to be registered in the DI container
    /// (call AddStyloExtractMarkdownNegotiation() before building the container).
    /// </summary>
    public static ResponsePolicyBuilder NegotiateMarkdown(this ResponsePolicyBuilder builder)
    {
        var sp = builder.ServiceProvider
            ?? throw new InvalidOperationException(
                "ResponsePolicyBuilder requires a ServiceProvider to resolve MarkdownNegotiationPolicy. " +
                "Register policies by constructing ResponsePolicyOptions in a AddSingleton<ResponsePolicyOptions>(sp => ...) factory " +
                "so the service provider is available at registration time.");

        var policy = sp.GetRequiredService<MarkdownNegotiationPolicy>();
        return builder.Use(policy);
    }
}
