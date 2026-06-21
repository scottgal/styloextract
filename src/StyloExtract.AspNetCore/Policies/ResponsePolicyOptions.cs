namespace StyloExtract.AspNetCore.Policies;

/// <summary>
/// Holds the named-policy registry used by ResponsePolicyMiddleware.
/// Populated via AddStyloExtract() overloads or by configuring the singleton directly.
/// </summary>
public sealed class ResponsePolicyOptions
{
    private readonly Dictionary<string, IResponsePolicy> _policies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The registered named policies, keyed by name.</summary>
    public IReadOnlyDictionary<string, IResponsePolicy> Policies => _policies;

    /// <summary>
    /// Registers a named policy. Policy names are case-insensitive.
    /// Later calls with the same name (regardless of case) replace the earlier registration.
    /// Returns this instance for fluent chaining.
    /// </summary>
    public ResponsePolicyOptions AddPolicy(string name, IResponsePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(policy);
        _policies[name] = policy;
        return this;
    }

    /// <summary>
    /// An optional fallback policy applied when an endpoint has no explicit policy metadata.
    /// Null (the default) means endpoints without metadata are skipped entirely.
    /// </summary>
    public IResponsePolicy? DefaultPolicy { get; set; }
}
