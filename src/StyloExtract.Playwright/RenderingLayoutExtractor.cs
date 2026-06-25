using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;

namespace StyloExtract.Playwright;

/// <summary>
/// Decorates an <see cref="ILayoutExtractor"/> with an automatic Playwright
/// re-fetch when the static extraction produces no usable content. Static
/// extraction is tried first — fast (~10 ms p50) and free. Only if the
/// content-role text mass is below the catastrophic threshold AND a URL
/// is available does the decorator re-fetch the page via the rendered HTML
/// fetcher (Playwright headless Chromium) and re-run the extractor on the
/// hydrated DOM.
///
/// <para>
/// Architecture: keeps the LayoutExtractor itself URL-agnostic (it still
/// receives HTML strings). The decorator owns the URL-fetch lifecycle and
/// the policy decision about WHEN to render. Operators register both the
/// inner extractor and the fetcher in DI; the wrapper composes them.
/// </para>
///
/// <para>
/// The Playwright re-fetch only fires when:
///   <list type="bullet">
///   <item>The caller passed a non-null <c>sourceUri</c>.</item>
///   <item>The static extraction produced &lt; 200 chars of content-role text.</item>
///   <item>An <see cref="IRenderedHtmlFetcher"/> is wired (i.e., the
///     consumer added the Playwright path).</item>
///   </list>
/// File-only callers (no URL) never trigger Playwright. Operators who don't
/// want the cost can omit the fetcher registration entirely.
/// </para>
/// </summary>
public sealed class RenderingLayoutExtractor : ILayoutExtractor
{
    private readonly ILayoutExtractor _inner;
    private readonly IRenderedHtmlFetcher _fetcher;
    private readonly ILogger<RenderingLayoutExtractor>? _logger;

    // Same threshold the LayoutExtractor uses for fallback activation —
    // content-role text below this is "catastrophic" enough to spend the
    // Playwright wall-clock budget on a re-fetch.
    private const int CatastrophicContentTextLength = 200;

    public RenderingLayoutExtractor(
        ILayoutExtractor inner,
        IRenderedHtmlFetcher fetcher,
        ILogger<RenderingLayoutExtractor>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _logger = logger;
    }

    public async Task<ExtractionResult> ExtractAsync(
        string html,
        Uri? sourceUri = null,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var first = await _inner.ExtractAsync(html, sourceUri, options, cancellationToken).ConfigureAwait(false);
        if (sourceUri is null) return first;

        var contentTextLen = first.Blocks
            .Where(b => b.Role is BlockRole.MainContent or BlockRole.Article
                or BlockRole.Heading or BlockRole.Summary or BlockRole.Table
                or BlockRole.CodeBlock or BlockRole.RepeatedItem)
            .Sum(b => b.Text.Length);
        if (contentTextLen >= CatastrophicContentTextLength) return first;

        // Static extraction was catastrophic. Re-fetch with Playwright; the
        // hydrated DOM may have content the static HTML lacked.
        _logger?.LogInformation(
            "static extraction returned {ContentTextLen} chars of content for {Url}; falling back to Playwright",
            contentTextLen, sourceUri);

        RenderedHtmlResult rendered;
        try
        {
            rendered = await _fetcher.FetchAsync(sourceUri, options: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Playwright fetch failed for {Url}; returning static result", sourceUri);
            return first;
        }

        if (string.IsNullOrWhiteSpace(rendered.Html) || rendered.Html.Length == html.Length)
        {
            // No new content — Playwright returned the same shell. Skip.
            return first;
        }

        var second = await _inner.ExtractAsync(rendered.Html, sourceUri, options, cancellationToken).ConfigureAwait(false);
        var secondContentLen = second.Blocks
            .Where(b => b.Role is BlockRole.MainContent or BlockRole.Article
                or BlockRole.Heading or BlockRole.Summary or BlockRole.Table
                or BlockRole.CodeBlock or BlockRole.RepeatedItem)
            .Sum(b => b.Text.Length);
        if (secondContentLen <= contentTextLen)
        {
            // Playwright didn't help. Return the static result.
            return first;
        }
        _logger?.LogInformation(
            "Playwright rescued {Url}: content text {Before} -> {After} chars",
            sourceUri, contentTextLen, secondContentLen);
        return second;
    }
}
