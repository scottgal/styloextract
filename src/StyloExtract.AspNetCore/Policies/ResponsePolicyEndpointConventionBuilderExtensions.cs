using Microsoft.AspNetCore.Builder;

namespace StyloExtract.AspNetCore.Policies;

/// <summary>
/// Extension methods for attaching response policies to endpoint convention builders.
/// </summary>
public static class ResponsePolicyEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Attaches a named response policy to the endpoint.
    /// The policy is resolved from ResponsePolicyOptions at request time.
    /// </summary>
    public static TBuilder WithResponsePolicy<TBuilder>(this TBuilder builder, string policyName)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(eb => eb.Metadata.Add(new ResponsePolicyMetadata(policyName)));
        return builder;
    }

    /// <summary>
    /// Attaches a direct policy instance to the endpoint (bypasses the named registry).
    /// </summary>
    public static TBuilder WithResponsePolicy<TBuilder>(this TBuilder builder, IResponsePolicy policy)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(eb => eb.Metadata.Add(new ResponsePolicyMetadata(policy)));
        return builder;
    }
}
