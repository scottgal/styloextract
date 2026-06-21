namespace StyloExtract.AspNetCore.Policies;

/// <summary>
/// Fluent builder for composing named IResponsePolicy instances.
/// Extension methods (NegotiateMarkdown, CacheHints) are added by the policy implementations.
/// Obtain an instance from AddStyloExtract() or construct directly for testing.
/// </summary>
public sealed class ResponsePolicyBuilder
{
    private readonly IServiceProvider? _serviceProvider;
    private readonly Dictionary<string, IResponsePolicy> _namedPolicies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Constructs a builder with an optional service provider for DI-resolved policies.</summary>
    public ResponsePolicyBuilder(IServiceProvider? serviceProvider = null)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The service provider, if one was supplied at construction time.</summary>
    internal IServiceProvider? ServiceProvider => _serviceProvider;

    /// <summary>The policy currently configured on this builder (null until Use() is called).</summary>
    internal IResponsePolicy? Built { get; private set; }

    /// <summary>Sets the policy directly. Used by extension methods such as NegotiateMarkdown() and CacheHints().</summary>
    public ResponsePolicyBuilder Use(IResponsePolicy policy)
    {
        Built = policy;
        return this;
    }

    /// <summary>
    /// Registers a named policy by configuring an inner ResponsePolicyBuilder.
    /// The inner builder receives the same service provider as this builder.
    /// Policy names are case-insensitive.
    /// </summary>
    /// <param name="name">The policy name used with WithResponsePolicy() or [ResponsePolicy].</param>
    /// <param name="configure">Delegate that configures the inner builder (e.g. p.NegotiateMarkdown(), p.CacheHints()).</param>
    public ResponsePolicyBuilder AddPolicy(string name, Action<ResponsePolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var inner = new ResponsePolicyBuilder(_serviceProvider);
        configure(inner);
        _namedPolicies[name] = inner.Build();
        return this;
    }

    /// <summary>
    /// Builds and returns the configured policy.
    /// Throws InvalidOperationException if no policy was configured.
    /// </summary>
    public IResponsePolicy Build()
    {
        if (Built is null)
            throw new InvalidOperationException(
                "No policy was configured on this ResponsePolicyBuilder. " +
                "Call NegotiateMarkdown(), CacheHints(), or Use(policy) first.");
        return Built;
    }

    /// <summary>
    /// Applies all named policies registered via AddPolicy() to the provided ResponsePolicyOptions.
    /// Called by the AddStyloExtract(Action&lt;ResponsePolicyBuilder&gt;) overload.
    /// </summary>
    internal void ApplyNamedPoliciesTo(ResponsePolicyOptions options)
    {
        foreach (var (name, policy) in _namedPolicies)
            options.AddPolicy(name, policy);
    }
}
