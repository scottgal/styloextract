using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using StyloExtract.Abstractions;

namespace StyloExtract.AspNetCore.Markdown;

/// <summary>
/// Shared cache logic for Markdown negotiation: key computation, store/retrieve,
/// ETag comparison, and Cache-Control emission.
/// Used by the middleware, the MVC attribute, and the Minimal API filter.
/// </summary>
internal static class MarkdownCacheHelper
{
    // Response header names.
    internal const string CacheStatusHeader = "X-Stylo-Cache";
    internal const string AcceptOverrideHeader = "X-Stylo-Accept-Override";

    /// <summary>
    /// Resolves the effective Accept value for this request, applying any query-string override.
    /// Returns null when no override is found and the caller should use the real Accept header.
    /// Also sets <c>X-Stylo-Accept-Override</c> on the response when an override fires.
    /// </summary>
    internal static string? ResolveAcceptOverride(HttpContext context, MarkdownNegotiationOptions opts)
    {
        var overrideKey = opts.AcceptOverrideQueryName;
        if (string.IsNullOrEmpty(overrideKey))
            return null;

        if (!context.Request.Query.TryGetValue(overrideKey, out var queryVal))
            return null;

        var queryStr = queryVal.ToString();
        if (!opts.AcceptOverrideMappings.TryGetValue(queryStr, out var mime))
            return null;

        // Signal to the caller (and to debugging consumers) that an override is active.
        context.Response.Headers[AcceptOverrideHeader] = mime;
        return mime;
    }

    /// <summary>
    /// Returns the effective Accept header string to use for quality evaluation.
    /// Prefers the query-string override when available.
    /// </summary>
    internal static string GetEffectiveAccept(HttpContext context, MarkdownNegotiationOptions opts)
    {
        var overrideMime = ResolveAcceptOverride(context, opts);
        if (overrideMime is not null)
            return overrideMime;

        return context.Request.Headers.Accept.ToString();
    }

    /// <summary>
    /// Computes a stable hex-encoded SHA-256 cache key for this request.
    /// The override query parameter is excluded from the key so that requests reaching the
    /// same URL via Accept header or ?format=markdown share a single cache slot.
    /// </summary>
    internal static string ComputeCacheKey(HttpContext context, MarkdownNegotiationOptions opts, ExtractionProfile profile)
    {
        var req = context.Request;
        var overrideKey = opts.AcceptOverrideQueryName ?? string.Empty;

        // Build a sorted query string that excludes the override param.
        var sortedQuery = BuildSortedQuery(req.Query, overrideKey);

        var raw = string.Concat(
            req.Method, "|",
            req.Scheme, "|",
            req.Host.Value, "|",
            req.Path.Value, "|",
            sortedQuery, "|",
            profile.ToString());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "stylo-md:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildSortedQuery(IQueryCollection query, string excludeKey)
    {
        var pairs = new List<string>(query.Count);
        foreach (var kv in query)
        {
            if (string.Equals(kv.Key, excludeKey, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var v in kv.Value)
                pairs.Add(kv.Key + "=" + v);
        }

        pairs.Sort(StringComparer.Ordinal);
        return string.Join("&", pairs);
    }

    /// <summary>
    /// Computes a hex-encoded SHA-256 ETag for the given Markdown bytes.
    /// </summary>
    internal static string ComputeEtag(byte[] markdownBytes)
    {
        var hash = SHA256.HashData(markdownBytes);
        return "\"" + Convert.ToHexString(hash).ToLowerInvariant() + "\"";
    }

    /// <summary>
    /// Attempts to serve a cached Markdown response. Returns true when the request was
    /// fully handled (cache hit or 304); the caller should not call the next middleware/handler.
    /// </summary>
    internal static async Task<bool> TryServeCachedAsync(
        HttpContext context,
        IDistributedCache cache,
        string cacheKey,
        MarkdownNegotiationOptions.CacheOptions cacheOpts,
        bool emitVary,
        CancellationToken ct)
    {
        var cached = await cache.GetAsync(cacheKey, ct);
        if (cached is null)
            return false;

        var etag = ComputeEtag(cached);
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();

        context.Response.Headers[CacheStatusHeader] = "hit";

        if (cacheOpts.EnableEtag)
            context.Response.Headers.ETag = etag;

        if (cacheOpts.EmitCacheControlHeader)
        {
            var maxAge = (int)cacheOpts.AbsoluteExpiration.TotalSeconds;
            context.Response.Headers.CacheControl = $"public, max-age={maxAge}";
        }

        if (emitVary)
            context.Response.Headers.Vary = "Accept";

        // Honor If-None-Match.
        if (cacheOpts.EnableEtag
            && !string.IsNullOrEmpty(ifNoneMatch)
            && string.Equals(ifNoneMatch, etag, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 304;
            return true;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/markdown; charset=utf-8";
        context.Response.ContentLength = cached.Length;
        await context.Response.Body.WriteAsync(cached, ct);
        return true;
    }

    /// <summary>
    /// Writes Markdown bytes to the response and stores them in the cache.
    /// </summary>
    internal static async Task WriteAndCacheAsync(
        HttpContext context,
        IDistributedCache cache,
        string cacheKey,
        byte[] markdownBytes,
        MarkdownNegotiationOptions.CacheOptions cacheOpts,
        bool emitVary,
        CancellationToken ct)
    {
        var etag = ComputeEtag(markdownBytes);

        context.Response.Headers[CacheStatusHeader] = "miss";

        if (cacheOpts.EnableEtag)
            context.Response.Headers.ETag = etag;

        if (cacheOpts.EmitCacheControlHeader)
        {
            var maxAge = (int)cacheOpts.AbsoluteExpiration.TotalSeconds;
            context.Response.Headers.CacheControl = $"public, max-age={maxAge}";
        }

        if (emitVary)
            context.Response.Headers.Vary = "Accept";

        context.Response.ContentType = "text/markdown; charset=utf-8";
        context.Response.ContentLength = markdownBytes.Length;
        await context.Response.Body.WriteAsync(markdownBytes, ct);

        var entryOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = cacheOpts.AbsoluteExpiration,
            SlidingExpiration = cacheOpts.SlidingExpiration,
        };

        await cache.SetAsync(cacheKey, markdownBytes, entryOptions, ct);
    }
}
