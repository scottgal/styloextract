using Microsoft.AspNetCore.Http;

namespace StyloExtract.AspNetCore.Policies;

/// <summary>
/// Three-phase contract for response-transformation policies.
/// Modelled on IOutputCachePolicy's lifecycle shape: request, serve, produce.
/// </summary>
public interface IResponsePolicy
{
    /// <summary>
    /// Pre-pipeline hook. Decide whether this policy applies to the current request.
    /// Configure vary semantics, request-time short-circuiting, and any per-request state
    /// the policy needs in OnServeAsync or OnProducedAsync.
    /// </summary>
    ValueTask OnRequestAsync(ResponsePolicyContext context);

    /// <summary>
    /// Pre-serve hook. Called when the policy might serve cached or canned content
    /// without running downstream middleware. Set ShouldShortCircuit to true and write
    /// to context.HttpContext.Response to take over the response.
    /// </summary>
    ValueTask OnServeAsync(ResponsePolicyContext context);

    /// <summary>
    /// Post-produce hook. Called after downstream middleware has produced a response,
    /// before the response is written to the wire. Transform the buffered body, set
    /// headers, populate caches, etc.
    /// </summary>
    ValueTask OnProducedAsync(ResponsePolicyContext context);
}
