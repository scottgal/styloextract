using Microsoft.AspNetCore.Http;
using System.ComponentModel;

namespace StyloExtract.AspNetCore.Policies;

/// <summary>
/// Describes the current state of the policy chain for the active request.
/// </summary>
public enum PolicyChainState
{
    /// <summary>Normal flow: continue to the next phase or policy.</summary>
    Continue = 0,

    /// <summary>
    /// A policy has populated <see cref="ResponsePolicyContext.RewrittenBody"/> with cached content.
    /// The middleware serves it and skips the downstream pipeline.
    /// </summary>
    ServeFromCache = 1,

    /// <summary>
    /// The response status code is final (e.g., 304 Not Modified). The middleware stops the
    /// OnProducedAsync chain immediately and writes whatever body is present (may be empty).
    /// </summary>
    Terminate = 2,
}

/// <summary>
/// Per-request context bag shared across all phases of every policy in the pipeline.
/// </summary>
public sealed class ResponsePolicyContext
{
    /// <summary>Initialises the context for the given request.</summary>
    public ResponsePolicyContext(HttpContext httpContext)
    {
        HttpContext = httpContext;
        VaryBy = new List<string>();
        Items = new Dictionary<string, object?>();
    }

    /// <summary>The current HTTP context.</summary>
    public HttpContext HttpContext { get; }

    /// <summary>
    /// Current state of the policy chain.
    /// Set to <see cref="PolicyChainState.ServeFromCache"/> when a policy has a cached response ready.
    /// Set to <see cref="PolicyChainState.Terminate"/> when the status code is final (e.g. 304).
    /// </summary>
    public PolicyChainState State { get; set; }

    /// <summary>
    /// True when the policy chain should not call downstream middleware.
    /// Equivalent to <c>State != PolicyChainState.Continue</c> for reads.
    /// Setting this to <c>true</c> sets <see cref="State"/> to <see cref="PolicyChainState.ServeFromCache"/>.
    /// </summary>
    [Obsolete("Use State == PolicyChainState.ServeFromCache or Terminate instead.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ShouldShortCircuit
    {
        get => State != PolicyChainState.Continue;
        set
        {
            if (value && State == PolicyChainState.Continue)
                State = PolicyChainState.ServeFromCache;
        }
    }

    /// <summary>Vary headers this policy contributes (Accept, Accept-Encoding, etc.).</summary>
    public List<string> VaryBy { get; }

    /// <summary>Per-policy state shared between OnRequestAsync, OnServeAsync, OnProducedAsync of THIS request.</summary>
    public Dictionary<string, object?> Items { get; }

    /// <summary>Buffered response body once downstream middleware has produced it (null before OnProducedAsync).</summary>
    public ReadOnlyMemory<byte>? BufferedBody { get; internal set; }

    /// <summary>Original response Content-Type once downstream has produced it.</summary>
    public string? ProducedContentType { get; internal set; }

    /// <summary>If set in OnProducedAsync, replaces the response body with these bytes.</summary>
    public ReadOnlyMemory<byte>? RewrittenBody { get; set; }

    /// <summary>If set alongside RewrittenBody, updates the response Content-Type.</summary>
    public string? RewrittenContentType { get; set; }
}
