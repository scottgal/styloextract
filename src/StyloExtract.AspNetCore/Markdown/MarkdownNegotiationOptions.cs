using StyloExtract.Abstractions;

namespace StyloExtract.AspNetCore.Markdown;

public sealed class MarkdownNegotiationOptions
{
    public ExtractionProfile DefaultProfile { get; set; } = ExtractionProfile.RagFull;
    public int MaxBodyBytes { get; set; } = 4 * 1024 * 1024;
    public HashSet<int> StatusCodes { get; init; } = [200];
    public string ProfileHeaderName { get; set; } = "X-Stylo-Profile";
    public string ProfileQueryName { get; set; } = "stylo_profile";
    public bool EmitVaryHeader { get; set; } = true;

    /// <summary>
    /// Name of a query-string parameter that overrides the request's Accept header.
    /// When the query string has <c>?format=markdown</c> (or html, or json), the
    /// middleware treats the request as if it had <c>Accept: text/markdown</c> etc.
    /// Useful for browser testing where Accept headers are hard to set.
    /// Set to null or empty to disable.
    /// </summary>
    public string? AcceptOverrideQueryName { get; set; } = "format";

    /// <summary>
    /// Maps <c>?format=NAME</c> values to a MIME type used as a virtual Accept header.
    /// Defaults: "markdown" to "text/markdown", "html" to "text/html", "json" to "application/json".
    /// Case-insensitive on the key.
    /// </summary>
    public IDictionary<string, string> AcceptOverrideMappings { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["markdown"] = "text/markdown",
        ["md"] = "text/markdown",
        ["html"] = "text/html",
        ["json"] = "application/json",
        ["text"] = "text/plain",
    };

    /// <summary>Caching options for rendered Markdown responses.</summary>
    public CacheOptions Cache { get; init; } = new();

    /// <summary>Options controlling server-side and HTTP caching of Markdown responses.</summary>
    public sealed class CacheOptions
    {
        /// <summary>Off by default. Enable to cache rendered Markdown by URL + profile.</summary>
        public bool Enabled { get; set; }

        /// <summary>Absolute cache expiration. Default: 5 minutes.</summary>
        public TimeSpan AbsoluteExpiration { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Sliding cache expiration. Default: 2 minutes.</summary>
        public TimeSpan SlidingExpiration { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Whether to set <c>Cache-Control: public, max-age=...</c> on Markdown responses.
        /// Defaults to <c>false</c>: server-side cache, but no client cache hint.
        /// </summary>
        public bool EmitCacheControlHeader { get; set; }

        /// <summary>Whether to honor <c>If-None-Match</c> requests and return 304.</summary>
        public bool EnableEtag { get; set; } = true;
    }
}
