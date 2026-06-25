using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;

namespace StyloExtract.Playwright;

/// <summary>
/// DI helpers for wiring the Playwright fallback path into the extraction
/// pipeline. Two registrations:
///   <list type="bullet">
///   <item><see cref="IRenderedHtmlFetcher"/> as a singleton
///     <see cref="PlaywrightHtmlFetcher"/> — owns the headless Chromium
///     browser, lazy-launched on first FetchAsync.</item>
///   <item><see cref="ILayoutExtractor"/> decorated with
///     <see cref="RenderingLayoutExtractor"/> — wraps the existing
///     extractor with an automatic Playwright re-fetch when the static
///     extraction produces no usable content for a URL-bearing request.</item>
///   </list>
///
/// <para>
/// Call AFTER <c>services.AddStyloExtract(...)</c> so the inner LayoutExtractor
/// registration is already in place to decorate.
/// </para>
/// </summary>
public static class PlaywrightServiceCollectionExtensions
{
    /// <summary>
    /// Register Playwright as the rendered-HTML fetcher AND decorate the
    /// existing <see cref="ILayoutExtractor"/> registration so requests
    /// whose static extraction comes up empty get a second-chance Playwright
    /// pass automatically. The wrapper only fires for requests that pass a
    /// non-null source URI; file-only callers never trigger a render.
    /// </summary>
    public static IServiceCollection AddStyloExtractPlaywright(this IServiceCollection services)
    {
        services.TryAddSingleton<IRenderedHtmlFetcher>(_ => new PlaywrightHtmlFetcher());

        // Decorate the existing ILayoutExtractor registration. Find the
        // currently-registered descriptor and wrap it. The inner factory is
        // re-resolved on every request because we don't have access to the
        // original implementation type from here.
        var innerDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ILayoutExtractor))
            ?? throw new InvalidOperationException(
                "AddStyloExtractPlaywright must be called after AddStyloExtract — no ILayoutExtractor is registered.");

        services.Remove(innerDescriptor);
        services.AddSingleton<ILayoutExtractor>(sp =>
        {
            ILayoutExtractor inner = innerDescriptor switch
            {
                { ImplementationInstance: ILayoutExtractor instance } => instance,
                { ImplementationFactory: { } factory } => (ILayoutExtractor)factory(sp),
                { ImplementationType: { } implType } => (ILayoutExtractor)ActivatorUtilities.CreateInstance(sp, implType),
                _ => throw new InvalidOperationException("Cannot decorate ILayoutExtractor — descriptor shape unrecognised."),
            };
            return new RenderingLayoutExtractor(
                inner,
                sp.GetRequiredService<IRenderedHtmlFetcher>(),
                sp.GetService<ILogger<RenderingLayoutExtractor>>());
        });

        return services;
    }
}
