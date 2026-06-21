using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;

namespace StyloExtract.AspNetCore.Markdown;

public static class MinimalApiMarkdownExtensions
{
    /// <summary>
    /// Adds an endpoint filter that intercepts HTML results and converts them to Markdown
    /// when the client sends <c>Accept: text/markdown</c>. Equivalent to the
    /// <see cref="NegotiateMarkdownAttribute"/> for Minimal API routes.
    /// Supports the query-string Accept override and optional IDistributedCache caching.
    /// </summary>
    public static RouteHandlerBuilder WithMarkdownNegotiation(
        this RouteHandlerBuilder builder,
        ExtractionProfile? profile = null)
    {
        return builder.AddEndpointFilter(new MarkdownNegotiationEndpointFilter(profile));
    }
}

/// <summary>
/// Endpoint filter that rewrites HTML responses to Markdown on <c>Accept: text/markdown</c>.
/// When markdown is accepted, the result is intercepted before it executes and rendered as markdown instead.
/// </summary>
internal sealed class MarkdownNegotiationEndpointFilter : IEndpointFilter
{
    private readonly ExtractionProfile? _profile;

    public MarkdownNegotiationEndpointFilter(ExtractionProfile? profile)
    {
        _profile = profile;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var sp = ctx.HttpContext.RequestServices;
        var opts = sp.GetRequiredService<IOptions<MarkdownNegotiationOptions>>().Value;

        // Resolve effective Accept (query override wins over real Accept header).
        var effectiveAccept = MarkdownCacheHelper.GetEffectiveAccept(ctx.HttpContext, opts);

        if (AcceptHeaderParser.GetQuality(effectiveAccept, "text/markdown") <= 0.0)
            return await next(ctx);

        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        var cache = sp.GetRequiredService<IDistributedCache>();
        var logger = sp.GetService<ILogger<MarkdownNegotiationEndpointFilter>>();

        var profile = _profile ?? opts.DefaultProfile;
        var sourceUri = new Uri(ctx.HttpContext.Request.GetDisplayUrl());

        // Cache path: try hit before invoking the handler.
        if (opts.Cache.Enabled)
        {
            var cacheKey = MarkdownCacheHelper.ComputeCacheKey(ctx.HttpContext, opts, profile);

            // Use a wrapper result that handles the cache lookup + potential handler invocation.
            return new CachedMarkdownResult(cacheKey, profile, _profile, opts, extractor, cache, logger, next, ctx, sourceUri);
        }

        // Get the result from the handler without executing it yet.
        var result = await next(ctx);

        // Only intercept IResult that produce HTML.
        if (result is IResult htmlResult)
        {
            // Execute into a buffer so we can inspect the content type and body.
            using var buffer = new MemoryStream();
            var savedBody = ctx.HttpContext.Response.Body;
            ctx.HttpContext.Response.Body = buffer;

            try
            {
                await htmlResult.ExecuteAsync(ctx.HttpContext);
            }
            finally
            {
                ctx.HttpContext.Response.Body = savedBody;
            }

            var capturedContentType = ctx.HttpContext.Response.ContentType ?? string.Empty;

            if (capturedContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) && buffer.Length > 0)
            {
                buffer.Seek(0, SeekOrigin.Begin);
                var html = new StreamReader(buffer, Encoding.UTF8).ReadToEnd();

                try
                {
                    var extracted = await extractor.ExtractAsync(
                        html, sourceUri, new ExtractionOptions { Profile = profile },
                        ctx.HttpContext.RequestAborted);

                    if (opts.EmitVaryHeader)
                        ctx.HttpContext.Response.Headers.Vary = "Accept";

                    return new RawMarkdownResult(extracted.Markdown, opts.EmitVaryHeader);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "WithMarkdownNegotiation: extraction failed; returning original HTML.");
                }
            }

            // Not HTML or extraction failed: replay the buffered bytes as-is.
            buffer.Seek(0, SeekOrigin.Begin);
            return new BufferedBytesResult(buffer.ToArray(), capturedContentType);
        }

        return result;
    }
}

/// <summary>
/// IResult that handles cache lookup, optional handler invocation, and cache population
/// for the Minimal API filter path.
/// </summary>
internal sealed class CachedMarkdownResult : IResult
{
    private readonly string _cacheKey;
    private readonly ExtractionProfile _profile;
    private readonly ExtractionProfile? _pinnedProfile;
    private readonly MarkdownNegotiationOptions _opts;
    private readonly ILayoutExtractor _extractor;
    private readonly IDistributedCache _cache;
    private readonly ILogger? _logger;
    private readonly EndpointFilterDelegate _next;
    private readonly EndpointFilterInvocationContext _ctx;
    private readonly Uri _sourceUri;

    public CachedMarkdownResult(
        string cacheKey,
        ExtractionProfile profile,
        ExtractionProfile? pinnedProfile,
        MarkdownNegotiationOptions opts,
        ILayoutExtractor extractor,
        IDistributedCache cache,
        ILogger? logger,
        EndpointFilterDelegate next,
        EndpointFilterInvocationContext ctx,
        Uri sourceUri)
    {
        _cacheKey = cacheKey;
        _profile = profile;
        _pinnedProfile = pinnedProfile;
        _opts = opts;
        _extractor = extractor;
        _cache = cache;
        _logger = logger;
        _next = next;
        _ctx = ctx;
        _sourceUri = sourceUri;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        // Try cache hit.
        if (await MarkdownCacheHelper.TryServeCachedAsync(
                httpContext, _cache, _cacheKey, _opts.Cache, _opts.EmitVaryHeader, httpContext.RequestAborted))
        {
            return;
        }

        // Cache miss: invoke the actual handler.
        var handlerResult = await _next(_ctx);

        if (handlerResult is IResult htmlResult)
        {
            using var buffer = new MemoryStream();
            var savedBody = httpContext.Response.Body;
            httpContext.Response.Body = buffer;

            try
            {
                await htmlResult.ExecuteAsync(httpContext);
            }
            finally
            {
                httpContext.Response.Body = savedBody;
            }

            var capturedContentType = httpContext.Response.ContentType ?? string.Empty;

            if (capturedContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) && buffer.Length > 0)
            {
                buffer.Seek(0, SeekOrigin.Begin);
                var html = new StreamReader(buffer, Encoding.UTF8).ReadToEnd();

                try
                {
                    var extracted = await _extractor.ExtractAsync(
                        html, _sourceUri, new ExtractionOptions { Profile = _profile },
                        httpContext.RequestAborted);

                    var markdownBytes = Encoding.UTF8.GetBytes(extracted.Markdown);
                    await MarkdownCacheHelper.WriteAndCacheAsync(
                        httpContext, _cache, _cacheKey, markdownBytes,
                        _opts.Cache, _opts.EmitVaryHeader, httpContext.RequestAborted);
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "WithMarkdownNegotiation (cached): extraction failed; returning original HTML.");
                }
            }

            // Not HTML or extraction failed: replay the buffered bytes.
            buffer.Seek(0, SeekOrigin.Begin);
            var bytes = buffer.ToArray();
            if (!string.IsNullOrEmpty(capturedContentType))
                httpContext.Response.ContentType = capturedContentType;
            httpContext.Response.ContentLength = bytes.Length;
            await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted);
            return;
        }

        // Handler returned something other than IResult: write it as-is (no cache).
        if (handlerResult is not null)
        {
            if (handlerResult is string str)
            {
                var bytes = Encoding.UTF8.GetBytes(str);
                httpContext.Response.ContentType = "text/plain; charset=utf-8";
                httpContext.Response.ContentLength = bytes.Length;
                await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted);
            }
        }
    }
}

/// <summary>Writes pre-rendered Markdown bytes as the HTTP response.</summary>
internal sealed class RawMarkdownResult : IResult
{
    private readonly string _markdown;
    private readonly bool _emitVary;

    public RawMarkdownResult(string markdown, bool emitVary)
    {
        _markdown = markdown;
        _emitVary = emitVary;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var bytes = Encoding.UTF8.GetBytes(_markdown);
        httpContext.Response.ContentType = "text/markdown; charset=utf-8";
        httpContext.Response.ContentLength = bytes.Length;
        if (_emitVary)
            httpContext.Response.Headers.Vary = "Accept";
        await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted);
    }
}

/// <summary>Replays a pre-captured response body with its original content type.</summary>
internal sealed class BufferedBytesResult : IResult
{
    private readonly byte[] _bytes;
    private readonly string _contentType;

    public BufferedBytesResult(byte[] bytes, string contentType)
    {
        _bytes = bytes;
        _contentType = contentType;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        if (!string.IsNullOrEmpty(_contentType))
            httpContext.Response.ContentType = _contentType;
        httpContext.Response.ContentLength = _bytes.Length;
        await httpContext.Response.Body.WriteAsync(_bytes, httpContext.RequestAborted);
    }
}

/// <summary>
/// Factory for IResult instances that serve HTML to standard clients and Markdown to
/// clients that send <c>Accept: text/markdown</c>.
/// </summary>
public static class StyloExtractResults
{
    /// <summary>
    /// Inspects the current request's Accept header. Returns a Markdown result when
    /// <c>text/markdown</c> is preferred; returns an HTML result otherwise.
    /// Extraction is performed via the registered <see cref="ILayoutExtractor"/>.
    /// </summary>
    public static IResult HtmlOrMarkdown(
        string html,
        Uri? sourceUri = null,
        ExtractionProfile? profile = null)
    {
        return new HtmlOrMarkdownResult(html, sourceUri, profile);
    }
}

internal sealed class HtmlOrMarkdownResult : IResult
{
    private readonly string _html;
    private readonly Uri? _sourceUri;
    private readonly ExtractionProfile? _profile;

    public HtmlOrMarkdownResult(string html, Uri? sourceUri, ExtractionProfile? profile)
    {
        _html = html;
        _sourceUri = sourceUri;
        _profile = profile;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var sp = httpContext.RequestServices;
        var opts = sp.GetRequiredService<IOptions<MarkdownNegotiationOptions>>().Value;
        var logger = sp.GetService<ILogger<HtmlOrMarkdownResult>>();

        // Resolve effective Accept (query override wins over real Accept header).
        var effectiveAccept = MarkdownCacheHelper.GetEffectiveAccept(httpContext, opts);

        if (AcceptHeaderParser.GetQuality(effectiveAccept, "text/markdown") <= 0.0)
        {
            await Results.Content(_html, "text/html; charset=utf-8").ExecuteAsync(httpContext);
            return;
        }

        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        var cache = sp.GetRequiredService<IDistributedCache>();

        var profile = _profile ?? opts.DefaultProfile;
        var sourceUri = _sourceUri ?? new Uri(httpContext.Request.GetDisplayUrl());

        if (opts.Cache.Enabled)
        {
            var cacheKey = MarkdownCacheHelper.ComputeCacheKey(httpContext, opts, profile);

            if (await MarkdownCacheHelper.TryServeCachedAsync(
                    httpContext, cache, cacheKey, opts.Cache, opts.EmitVaryHeader, httpContext.RequestAborted))
            {
                return;
            }

            try
            {
                var result = await extractor.ExtractAsync(
                    _html, sourceUri, new ExtractionOptions { Profile = profile }, httpContext.RequestAborted);

                var markdownBytes = Encoding.UTF8.GetBytes(result.Markdown);
                await MarkdownCacheHelper.WriteAndCacheAsync(
                    httpContext, cache, cacheKey, markdownBytes, opts.Cache, opts.EmitVaryHeader, httpContext.RequestAborted);
                return;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "HtmlOrMarkdown: extraction failed; returning original HTML.");
                await Results.Content(_html, "text/html; charset=utf-8").ExecuteAsync(httpContext);
                return;
            }
        }

        try
        {
            var result = await extractor.ExtractAsync(
                _html,
                sourceUri,
                new ExtractionOptions { Profile = profile },
                httpContext.RequestAborted);

            var markdownBytes = Encoding.UTF8.GetBytes(result.Markdown);

            if (opts.EmitVaryHeader)
                httpContext.Response.Headers.Vary = "Accept";

            httpContext.Response.ContentType = "text/markdown; charset=utf-8";
            httpContext.Response.ContentLength = markdownBytes.Length;
            await httpContext.Response.Body.WriteAsync(markdownBytes, httpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "HtmlOrMarkdown: extraction failed; returning original HTML.");
            await Results.Content(_html, "text/html; charset=utf-8").ExecuteAsync(httpContext);
        }
    }
}
