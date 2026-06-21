using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;

namespace StyloExtract.AspNetCore.Markdown;

/// <summary>
/// Intercepts responses with Content-Type text/html when the client prefers text/markdown,
/// extracts the content via ILayoutExtractor, and returns Markdown instead.
/// Supports a query-string Accept override and optional IDistributedCache caching.
/// </summary>
public sealed class StyloExtractMarkdownMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILayoutExtractor _extractor;
    private readonly IOptions<MarkdownNegotiationOptions> _options;
    private readonly IDistributedCache _cache;
    private readonly ILogger<StyloExtractMarkdownMiddleware> _logger;

    public StyloExtractMarkdownMiddleware(
        RequestDelegate next,
        ILayoutExtractor extractor,
        IOptions<MarkdownNegotiationOptions> options,
        IDistributedCache cache,
        ILogger<StyloExtractMarkdownMiddleware> logger)
    {
        _next = next;
        _extractor = extractor;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var opts = _options.Value;

        // Resolve the effective Accept value (query override wins over real Accept header).
        var effectiveAccept = MarkdownCacheHelper.GetEffectiveAccept(context, opts);

        // Only intercept when client effectively accepts text/markdown.
        if (AcceptHeaderParser.GetQuality(effectiveAccept, "text/markdown") <= 0.0)
        {
            await _next(context);
            return;
        }

        var originalBody = context.Response.Body;
        var buffer = new MemoryStream();

        context.Response.Body = buffer;
        try
        {
            await _next(context);

            var status = context.Response.StatusCode;
            var contentType = context.Response.ContentType ?? string.Empty;
            var bufferLen = buffer.Length;

            if (opts.StatusCodes.Contains(status)
                && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                && bufferLen > 0
                && bufferLen <= opts.MaxBodyBytes)
            {
                buffer.Seek(0, SeekOrigin.Begin);

                var encoding = DetectEncoding(contentType);
                var html = await new StreamReader(buffer, encoding).ReadToEndAsync(context.RequestAborted);

                var profile = ResolveProfile(context, opts);
                var sourceUri = new Uri(context.Request.GetDisplayUrl());

                // Cache path: try cache hit first (swap to real body stream before writing).
                if (opts.Cache.Enabled)
                {
                    var cacheKey = MarkdownCacheHelper.ComputeCacheKey(context, opts, profile);
                    context.Response.Body = originalBody;

                    if (await MarkdownCacheHelper.TryServeCachedAsync(
                            context, _cache, cacheKey, opts.Cache, opts.EmitVaryHeader, context.RequestAborted))
                    {
                        return;
                    }

                    // Cache miss: extract, write, and store.
                    try
                    {
                        var result = await _extractor.ExtractAsync(
                            html, sourceUri, new ExtractionOptions { Profile = profile }, context.RequestAborted);

                        var markdownBytes = Encoding.UTF8.GetBytes(result.Markdown);
                        await MarkdownCacheHelper.WriteAndCacheAsync(
                            context, _cache, cacheKey, markdownBytes, opts.Cache, opts.EmitVaryHeader, context.RequestAborted);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Markdown negotiation: extraction failed for {Url}; returning original HTML.", sourceUri);
                        // Fall through to write the original buffered HTML.
                        buffer.Seek(0, SeekOrigin.Begin);
                        await buffer.CopyToAsync(originalBody, context.RequestAborted);
                        return;
                    }
                }

                // Non-cached path.
                try
                {
                    var result = await _extractor.ExtractAsync(
                        html,
                        sourceUri,
                        new ExtractionOptions { Profile = profile },
                        context.RequestAborted);

                    var markdown = result.Markdown;
                    var markdownBytes = Encoding.UTF8.GetBytes(markdown);

                    context.Response.ContentType = "text/markdown; charset=utf-8";
                    context.Response.ContentLength = markdownBytes.Length;

                    if (opts.EmitVaryHeader)
                        context.Response.Headers.Vary = "Accept";

                    await originalBody.WriteAsync(markdownBytes, context.RequestAborted);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Markdown negotiation: extraction failed for {Url}; returning original HTML.", sourceUri);
                }
            }

            // Fallback: copy the buffer to the original stream unchanged.
            buffer.Seek(0, SeekOrigin.Begin);
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
            await buffer.DisposeAsync();
        }
    }

    private static ExtractionProfile ResolveProfile(HttpContext context, MarkdownNegotiationOptions opts)
    {
        // 1. Check request header.
        if (context.Request.Headers.TryGetValue(opts.ProfileHeaderName, out var headerVal)
            && TryParseProfile(headerVal.ToString(), out var fromHeader))
        {
            return fromHeader;
        }

        // 2. Check query string.
        if (context.Request.Query.TryGetValue(opts.ProfileQueryName, out var queryVal)
            && TryParseProfile(queryVal.ToString(), out var fromQuery))
        {
            return fromQuery;
        }

        return opts.DefaultProfile;
    }

    private static bool TryParseProfile(string value, out ExtractionProfile profile) =>
        Enum.TryParse(value, ignoreCase: true, out profile);

    private static Encoding DetectEncoding(string contentType)
    {
        // content-type: text/html; charset=utf-8
        var parts = contentType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (!part.StartsWith("charset", StringComparison.OrdinalIgnoreCase))
                continue;

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
                continue;

            var charsetName = part[(eq + 1)..].Trim().Trim('"');
            try
            {
                return Encoding.GetEncoding(charsetName);
            }
            catch (ArgumentException)
            {
                // Unknown charset; fall through to UTF-8.
            }
        }

        return Encoding.UTF8;
    }
}
