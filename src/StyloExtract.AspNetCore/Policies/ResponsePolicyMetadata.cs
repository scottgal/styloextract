namespace StyloExtract.AspNetCore.Policies;

/// <summary>
/// Endpoint metadata that identifies which response policy (by name or instance) applies
/// to an endpoint. Multiple entries on one endpoint compose in declaration order.
/// Attach via WithResponsePolicy() or IEndpointConventionBuilder.WithMetadata().
/// </summary>
public sealed class ResponsePolicyMetadata
{
    /// <summary>Identifies a policy by its registered name in ResponsePolicyOptions.</summary>
    public ResponsePolicyMetadata(string policyName)
    {
        PolicyName = policyName ?? throw new ArgumentNullException(nameof(policyName));
    }

    /// <summary>Uses the provided policy instance directly (bypasses the named registry).</summary>
    public ResponsePolicyMetadata(IResponsePolicy policy)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>The registered policy name, or null when a direct instance is used.</summary>
    public string? PolicyName { get; }

    /// <summary>The direct policy instance, or null when a name is used.</summary>
    public IResponsePolicy? Policy { get; }
}
