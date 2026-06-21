using System.Text;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore.Policies;

namespace StyloExtract.AspNetCore.Markdown;

/// <summary>
/// IResponsePolicy implementation that performs Markdown content negotiation.
/// Converts HTML responses to Markdown when the client requests text/markdown via Accept header
/// or the query-string override. Integrates with IDistributedCache when caching is enabled.
/// Register via AddStyloExtractMarkdownNegotiation(); attach to endpoints via
/// WithResponsePolicy("stylo:markdown") or [ResponsePolicy("stylo:markdown")].
/// </summary>
public sealed class MarkdownNegotiationPolicy : IResponsePolicy
{
    private const string ActiveKey = "stylo.markdown.active";
    private const string ProfileKey = "stylo.markdown.profile";
    private const string CacheKeyItem = "stylo.markdown.cache_key";

    private readonly IOptions<MarkdownNegotiationOptions> _options;
    private readonly ILayoutExtractor _extractor;
    private readonly IDistributedCache _cache;
    private readonly ILogger<MarkdownNegotiationPolicy> _logger;

    /// <summary>Initialises the policy from registered services.</summary>
    public MarkdownNegotiationPolicy(
        IOptions<MarkdownNegotiationOptions> options,
        ILayoutExtractor extractor,
        IDistributedCache cache,
        ILogger<MarkdownNegotiationPolicy> logger)
    {
        _options = options;
        _extractor = extractor;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc/>
    public ValueTask OnRequestAsync(ResponsePolicyContext context)
    {
        var opts = _options.Value;
        var effectiveAccept = MarkdownCacheHelper.GetEffectiveAccept(context.HttpContext, opts);

        if (AcceptHeaderParser.GetQuality(effectiveAccept, "text/markdown") <= 0.0)
        {
            context.Items[ActiveKey] = false;
            return ValueTask.CompletedTask;
        }

        context.Items[ActiveKey] = true;
        context.Items[ProfileKey] = ResolveProfile(context.HttpContext, opts);
        context.VaryBy.Add("Accept");

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask OnServeAsync(ResponsePolicyContext context)
    {
        if (context.Items[ActiveKey] is not true)
            return;

        var opts = _options.Value;
        if (!opts.Cache.Enabled)
            return;

        var profile = (ExtractionProfile)context.Items[ProfileKey]!;
        var cacheKey = MarkdownCacheHelper.ComputeCacheKey(context.HttpContext, opts, profile);
        context.Items[CacheKeyItem] = cacheKey;

        var hit = await MarkdownCacheHelper.TryServeCachedAsync(
            context.HttpContext, _cache, cacheKey, opts.Cache, opts.EmitVaryHeader,
            context.HttpContext.RequestAborted);

        if (hit)
            context.State = Policies.PolicyChainState.ServeFromCache;
    }

    /// <inheritdoc/>
    public async ValueTask OnProducedAsync(ResponsePolicyContext context)
    {
        if (context.Items[ActiveKey] is not true)
            return;

        if (context.State != Policies.PolicyChainState.Continue)
            return;

        var contentType = context.ProducedContentType ?? string.Empty;
        if (!contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return;

        if (context.BufferedBody is not { Length: > 0 } buffered)
            return;

        var opts = _options.Value;

        // Guard maximum body size.
        if (buffered.Length > opts.MaxBodyBytes)
            return;

        var encoding = DetectEncoding(contentType);
        var html = encoding.GetString(buffered.Span);
        var profile = (ExtractionProfile)context.Items[ProfileKey]!;
        var sourceUri = new Uri(context.HttpContext.Request.GetDisplayUrl());
        var ct = context.HttpContext.RequestAborted;

        try
        {
            var result = await _extractor.ExtractAsync(
                html, sourceUri, new ExtractionOptions { Profile = profile }, ct);

            var markdownBytes = Encoding.UTF8.GetBytes(result.Markdown);

            // Emit cache-related response headers.
            if (opts.Cache.Enabled)
            {
                var etag = MarkdownCacheHelper.ComputeEtag(markdownBytes);

                context.HttpContext.Response.Headers[MarkdownCacheHelper.CacheStatusHeader] = "miss";

                if (opts.Cache.EnableEtag)
                    context.HttpContext.Response.Headers.ETag = etag;

                if (opts.Cache.EmitCacheControlHeader)
                {
                    var maxAge = (int)opts.Cache.AbsoluteExpiration.TotalSeconds;
                    context.HttpContext.Response.Headers.CacheControl = $"public, max-age={maxAge}";
                }

                // Store in cache (do not write the body here; the middleware handles that via RewrittenBody).
                if (context.Items.TryGetValue(CacheKeyItem, out var ck) && ck is string cacheKey)
                {
                    var entryOptions = new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = opts.Cache.AbsoluteExpiration,
                        SlidingExpiration = opts.Cache.SlidingExpiration,
                    };
                    await _cache.SetAsync(cacheKey, markdownBytes, entryOptions, ct);
                }
            }

            // Vary is written once by the middleware after all policies run; no per-policy write needed here.
            context.RewrittenBody = markdownBytes;
            context.RewrittenContentType = "text/markdown; charset=utf-8";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MarkdownNegotiationPolicy: extraction failed for {Url}; original HTML will be returned.", sourceUri);
        }
    }

    private static ExtractionProfile ResolveProfile(Microsoft.AspNetCore.Http.HttpContext context, MarkdownNegotiationOptions opts)
    {
        if (context.Request.Headers.TryGetValue(opts.ProfileHeaderName, out var headerVal)
            && TryParseProfile(headerVal.ToString(), out var fromHeader))
        {
            return fromHeader;
        }

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
