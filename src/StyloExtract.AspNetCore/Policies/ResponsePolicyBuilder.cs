namespace StyloExtract.AspNetCore.Policies;

/// <summary>
/// Fluent builder for composing named IResponsePolicy instances.
/// Extension methods (NegotiateMarkdown, CacheHints) are added by the policy implementations.
/// Obtain an instance from AddStyloExtract() or construct directly for testing.
/// </summary>
public sealed class ResponsePolicyBuilder
{
    private readonly IServiceProvider? _serviceProvider;

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
}
