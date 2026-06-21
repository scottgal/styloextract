namespace StyloExtract.AspNetCore.CacheHints;

/// <summary>
/// Options controlling Cache-Control, ETag, and Vary header emission for a CacheHintPolicy instance.
/// </summary>
public sealed class CacheHintOptions
{
    /// <summary>Sets max-age in Cache-Control. Null omits the max-age directive.</summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>Sets s-maxage in Cache-Control for shared caches. Null omits the s-maxage directive.</summary>
    public TimeSpan? SharedMaxAge { get; set; }

    /// <summary>Emit Cache-Control: public. Default true. Set to false to emit Cache-Control: private.</summary>
    public bool Public { get; set; } = true;

    /// <summary>Emit must-revalidate directive in Cache-Control.</summary>
    public bool MustRevalidate { get; set; }

    /// <summary>Emit Cache-Control: no-store (overrides MaxAge, Public, and all other directives).</summary>
    public bool NoStore { get; set; }

    /// <summary>Emit Cache-Control: no-cache directive.</summary>
    public bool NoCache { get; set; }

    /// <summary>Additional Vary header values contributed by this policy instance.</summary>
    public List<string> Vary { get; init; } = new();

    /// <summary>Compute a SHA-256 ETag from the response body and emit it. Default true.</summary>
    public bool EmitETag { get; set; } = true;

    /// <summary>Honor If-None-Match conditional GET and return 304 on a match. Default true.</summary>
    public bool HonorIfNoneMatch { get; set; } = true;
}
