namespace StyloExtract.AspNetCore.Policies;

/// <summary>
/// Marks an MVC controller action or class with a named response policy.
/// Multiple attributes are allowed; policies run in declaration order.
/// For Minimal API endpoints use WithResponsePolicy() instead.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class ResponsePolicyAttribute : Attribute
{
    /// <summary>Specifies the named policy to apply.</summary>
    public ResponsePolicyAttribute(string policyName)
    {
        PolicyName = policyName;
    }

    /// <summary>The registered policy name.</summary>
    public string PolicyName { get; }
}
