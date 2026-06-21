using System.Security.Cryptography;
using StyloExtract.AspNetCore.Policies;

namespace StyloExtract.AspNetCore.CacheHints;

/// <summary>
/// IResponsePolicy that emits Cache-Control, ETag, and Vary response headers
/// and handles conditional GET (304 Not Modified) responses.
/// Computes the ETag from the response body after all prior policies have run,
/// so when composed with MarkdownNegotiationPolicy the ETag reflects the Markdown body.
/// </summary>
public sealed class CacheHintPolicy : IResponsePolicy
{
    private const string IfNoneMatchKey = "stylo.cache.if_none_match";

    private readonly CacheHintOptions _options;

    /// <summary>Initialises the policy with the given options.</summary>
    public CacheHintPolicy(CacheHintOptions options)
    {
        _options = options;
    }

    /// <inheritdoc/>
    public ValueTask OnRequestAsync(ResponsePolicyContext context)
    {
        // Add any options-specified Vary entries to the shared list.
        foreach (var v in _options.Vary)
            context.VaryBy.Add(v);

        // Store the If-None-Match header value for comparison in OnProducedAsync.
        if (_options.EmitETag && _options.HonorIfNoneMatch)
        {
            var ifNoneMatch = context.HttpContext.Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrEmpty(ifNoneMatch))
                context.Items[IfNoneMatchKey] = ifNoneMatch;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask OnServeAsync(ResponsePolicyContext context)
    {
        // Cache hints do not short-circuit; serving is left to the downstream handler.
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask OnProducedAsync(ResponsePolicyContext context)
    {
        // Use the buffered body (already updated by prior policy rewrites via the middleware chain).
        var body = context.BufferedBody;
        if (body is null)
            return ValueTask.CompletedTask;

        var response = context.HttpContext.Response;

        // Emit Cache-Control.
        var cacheControl = BuildCacheControl();
        if (cacheControl is not null)
            response.Headers.CacheControl = cacheControl;

        // Emit ETag and handle 304.
        if (_options.EmitETag)
        {
            var bodySpan = body.Value.Span;
            var hash = SHA256.HashData(bodySpan);
            var etag = "\"" + Convert.ToHexString(hash).ToLowerInvariant() + "\"";
            response.Headers.ETag = etag;

            if (_options.HonorIfNoneMatch
                && context.Items.TryGetValue(IfNoneMatchKey, out var stored)
                && stored is string requestEtag
                && string.Equals(requestEtag, etag, StringComparison.Ordinal))
            {
                response.StatusCode = 304;
                context.RewrittenBody = ReadOnlyMemory<byte>.Empty;
                // RFC 7232 §4.1: the 304 status is final; stop the OnProducedAsync chain.
                context.State = Policies.PolicyChainState.Terminate;
                return ValueTask.CompletedTask;
            }
        }

        // Vary is written once by the middleware after all policies run; no per-policy write needed here.
        return ValueTask.CompletedTask;
    }

    private string? BuildCacheControl()
    {
        if (_options.NoStore)
            return "no-store";

        var parts = new List<string>(6);

        parts.Add(_options.Public ? "public" : "private");

        if (_options.NoCache)
            parts.Add("no-cache");

        if (_options.MaxAge.HasValue)
            parts.Add($"max-age={(int)_options.MaxAge.Value.TotalSeconds}");

        if (_options.SharedMaxAge.HasValue)
            parts.Add($"s-maxage={(int)_options.SharedMaxAge.Value.TotalSeconds}");

        if (_options.MustRevalidate)
            parts.Add("must-revalidate");

        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }
}
