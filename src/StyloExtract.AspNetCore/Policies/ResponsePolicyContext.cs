using Microsoft.AspNetCore.Http;

namespace StyloExtract.AspNetCore.Policies;

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

    /// <summary>True if a previous policy has already short-circuited the response.</summary>
    public bool ShouldShortCircuit { get; set; }

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
