using StyloExtract.AspNetCore.Policies;

namespace StyloExtract.AspNetCore.CacheHints;

/// <summary>
/// Extends ResponsePolicyBuilder with the CacheHints() fluent method.
/// </summary>
public static class CacheHintPolicyBuilderExtensions
{
    /// <summary>
    /// Configures this builder to emit Cache-Control, ETag, and Vary headers,
    /// and to handle conditional GET (304 Not Modified) responses.
    /// </summary>
    public static ResponsePolicyBuilder CacheHints(
        this ResponsePolicyBuilder builder,
        Action<CacheHintOptions>? configure = null)
    {
        var opts = new CacheHintOptions();
        configure?.Invoke(opts);
        return builder.Use(new CacheHintPolicy(opts));
    }
}
