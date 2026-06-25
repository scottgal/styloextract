using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StyloExtract.AspNetCore.Policies;
using StyloExtract.Core;

namespace StyloExtract.AspNetCore;

public static class StyloExtractServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full StyloExtract stack (extraction, fingerprinting, templates).
    /// Also registers a default ResponsePolicyOptions singleton for use with UseStyloExtract().
    /// Delegates to <see cref="StyloExtract.Core.StyloExtractServiceCollectionExtensions.AddStyloExtract"/>;
    /// AspNetCore consumers can continue to call this without adding a <c>using StyloExtract.Core;</c>.
    /// </summary>
    public static IServiceCollection AddStyloExtract(this IServiceCollection services, Action<StyloExtractOptions>? configure = null)
    {
        StyloExtract.Core.StyloExtractServiceCollectionExtensions.AddStyloExtract(services, configure);
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
