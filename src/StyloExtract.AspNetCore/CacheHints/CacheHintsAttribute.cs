namespace StyloExtract.AspNetCore.CacheHints;

/// <summary>
/// Convenience attribute that documents cache hint intent on a controller action or class.
/// For use with a CacheHintAttributeFilter in a future release; in v1.2 use
/// [ResponsePolicy("cache-name")] with a registered CacheHintPolicy for full middleware integration.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class CacheHintsAttribute : Attribute
{
    /// <summary>Cache-Control max-age in seconds. 0 means no max-age directive is emitted.</summary>
    public int MaxAgeSeconds { get; set; }

    /// <summary>Whether to emit Cache-Control: public. Default true.</summary>
    public bool Public { get; set; } = true;

    /// <summary>Whether to emit must-revalidate. Default false.</summary>
    public bool MustRevalidate { get; set; }

    /// <summary>Whether to emit no-store, overriding all other directives. Default false.</summary>
    public bool NoStore { get; set; }

    /// <summary>Whether to compute and emit an ETag from the response body. Default true.</summary>
    public bool EmitETag { get; set; } = true;
}
