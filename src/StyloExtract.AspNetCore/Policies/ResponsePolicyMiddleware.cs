using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace StyloExtract.AspNetCore.Policies;

/// <summary>
/// Middleware that executes registered IResponsePolicy instances in the three-phase lifecycle:
/// OnRequestAsync (pre-pipeline), OnServeAsync (pre-downstream, short-circuit path),
/// OnProducedAsync (post-downstream, body transformation path).
/// </summary>
public sealed class ResponsePolicyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ResponsePolicyOptions _options;
    private readonly ILogger<ResponsePolicyMiddleware> _logger;

    /// <summary>Constructs the middleware with a reference to the named-policy registry.</summary>
    public ResponsePolicyMiddleware(
        RequestDelegate next,
        ResponsePolicyOptions options,
        ILogger<ResponsePolicyMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    /// <summary>Executes the three-phase policy pipeline for the current request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var policies = ResolvePolicies(context);

        // Fast path: no policies on this endpoint and no default policy.
        if (policies.Count == 0)
        {
            await _next(context);
            return;
        }

        var policyContext = new ResponsePolicyContext(context);

        // Phase 1: OnRequestAsync for all policies.
        foreach (var policy in policies)
            await policy.OnRequestAsync(policyContext);

        // Phase 2: OnServeAsync for all policies unconditionally.
        // Each policy checks whether it has cached content to serve. If any policy sets
        // State = ServeFromCache (or the legacy ShouldShortCircuit) the middleware serves
        // whatever the policy wrote to the response (or RewrittenBody) and skips downstream.
        foreach (var policy in policies)
            await policy.OnServeAsync(policyContext);

        if (policyContext.State != PolicyChainState.Continue)
        {
            // A policy either served from cache or declared a terminal status.
            // If it populated RewrittenBody, write that to the response stream.
            if (policyContext.RewrittenBody.HasValue)
            {
                var servedBody = policyContext.RewrittenBody.Value;
                if (policyContext.RewrittenContentType is not null)
                    context.Response.ContentType = policyContext.RewrittenContentType;

                // RFC 7232 §4.1: do not set Content-Length on a 304 response.
                if (context.Response.StatusCode != 304)
                    context.Response.ContentLength = servedBody.Length;

                await context.Response.Body.WriteAsync(servedBody, context.RequestAborted);
            }
            return;
        }

        // Phase 3 setup: buffer the downstream response.
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        // Restore stream, capture produced body and content-type.
        context.Response.Body = originalBody;

        buffer.Seek(0, SeekOrigin.Begin);
        var producedBytes = buffer.ToArray();
        policyContext.BufferedBody = producedBytes;
        policyContext.ProducedContentType = context.Response.ContentType;

        // Phase 3: OnProducedAsync for each policy in order.
        // After each policy, if it set RewrittenBody, update BufferedBody for the next policy.
        // If State becomes Terminate (e.g., 304 from CacheHintPolicy), stop the chain.
        foreach (var policy in policies)
        {
            await policy.OnProducedAsync(policyContext);

            if (policyContext.RewrittenBody.HasValue)
            {
                policyContext.BufferedBody = policyContext.RewrittenBody.Value;
                if (policyContext.RewrittenContentType is not null)
                    policyContext.ProducedContentType = policyContext.RewrittenContentType;
                policyContext.RewrittenBody = null;
                policyContext.RewrittenContentType = null;
            }

            // If a policy set State = Terminate (e.g. 304), stop here and write
            // whatever body is now buffered (may be empty).
            if (policyContext.State == PolicyChainState.Terminate)
                break;
        }

        // Emit accumulated Vary headers.
        if (policyContext.VaryBy.Count > 0)
        {
            var deduped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in policyContext.VaryBy)
                deduped.Add(v);
            context.Response.Headers.Vary = string.Join(", ", deduped);
        }

        // Write the final body (possibly rewritten multiple times).
        var finalBody = policyContext.BufferedBody ?? ReadOnlyMemory<byte>.Empty;

        if (policyContext.ProducedContentType is not null)
            context.Response.ContentType = policyContext.ProducedContentType;

        // RFC 7232 §4.1: a 304 response must not carry Content-Length derived from an empty body.
        // Either omit Content-Length entirely or echo the original 200 length — we omit it here
        // since we do not track the original 200 response's length.
        if (context.Response.StatusCode != 304)
            context.Response.ContentLength = finalBody.Length;

        await originalBody.WriteAsync(finalBody, context.RequestAborted);
    }

    private List<IResponsePolicy> ResolvePolicies(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var policies = new List<IResponsePolicy>();

        if (endpoint is null)
        {
            if (_options.DefaultPolicy is not null)
                policies.Add(_options.DefaultPolicy);
            return policies;
        }

        // Collect ResponsePolicyMetadata entries from endpoint metadata.
        var metadataEntries = endpoint.Metadata.GetOrderedMetadata<ResponsePolicyMetadata>();

        // Also collect ResponsePolicyAttribute entries (from MVC controller actions)
        // and synthesize metadata from them.
        var attributeEntries = endpoint.Metadata.GetOrderedMetadata<ResponsePolicyAttribute>();

        // Track resolved policy instances to deduplicate.
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var meta in metadataEntries)
            AddPolicy(meta, policies, seen);

        foreach (var attr in attributeEntries)
        {
            var syntheticMeta = new ResponsePolicyMetadata(attr.PolicyName);
            AddPolicy(syntheticMeta, policies, seen);
        }

        // Fallback to DefaultPolicy if nothing resolved.
        if (policies.Count == 0 && _options.DefaultPolicy is not null)
            policies.Add(_options.DefaultPolicy);

        return policies;
    }

    private void AddPolicy(ResponsePolicyMetadata meta, List<IResponsePolicy> policies, HashSet<object> seen)
    {
        if (meta.Policy is not null)
        {
            // Direct instance: deduplicate by reference.
            if (seen.Add(meta.Policy))
                policies.Add(meta.Policy);
            return;
        }

        if (meta.PolicyName is null)
            return;

        if (!_options.Policies.TryGetValue(meta.PolicyName, out var resolved))
        {
            _logger.LogDebug(
                "ResponsePolicyMiddleware: policy '{PolicyName}' is referenced by endpoint metadata but is not registered in ResponsePolicyOptions. Skipping.",
                meta.PolicyName);
            return;
        }

        // Named policies: deduplicate by resolved instance reference.
        if (seen.Add(resolved))
            policies.Add(resolved);
    }
}
