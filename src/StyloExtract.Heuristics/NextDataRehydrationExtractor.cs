using System.Text;
using System.Text.Json;
using AngleSharp.Dom;

namespace StyloExtract.Heuristics;

/// <summary>
/// Rehydration content extractor for Next.js applications.
///
/// <para>
/// Next.js ships every page as a server-rendered HTML shell plus a JSON blob
/// in <c>&lt;script id="__NEXT_DATA__" type="application/json"&gt;</c> containing
/// the props the client hydrates from. The actual content (product
/// descriptions, article bodies, search results, item lists) typically lives
/// somewhere under <c>props.pageProps</c>. Unlike Discourse's standardised
/// schema, the path varies per site — Shopify Hydrogen uses
/// <c>pageProps.shopifyProductsPreloadedState</c>, news sites use
/// <c>pageProps.initialState.article.body</c>, e-commerce uses
/// <c>pageProps.pageModel.content</c>, etc.
/// </para>
///
/// <para>
/// No canonical content path → walk the entire <c>props.pageProps</c> tree
/// and collect any string value that looks like prose: minimum length, contains
/// a space, isn't a URL / data-uri / CSS variable / serialised JSON. Concat
/// with paragraph separators. The result is noisy compared to a per-site
/// template but enough to recover catastrophic pages.
/// </para>
///
/// <para>
/// Empirical: WCXB diagnostic 2026-06-25 surfaced 5 catastrophic pages with
/// <c>__NEXT_DATA__</c> blobs (wral.com, ruggable.com, nike.com, etc.). The
/// generic walk recovers prose mass without needing a Next.js plugin per
/// site.
/// </para>
///
/// <para>
/// Fallback shape: same chained pattern as <see cref="JsonLdContentExtractor"/>
/// and <see cref="DiscourseRehydrationExtractor"/>. Caller checks heuristic
/// output first; the rehydration runs only when the heuristic produced
/// essentially nothing.
/// </para>
/// </summary>
public static class NextDataRehydrationExtractor
{
    private static readonly JsonDocumentOptions JdOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private const int MinStringLengthForContent = 80;

    // Cold-path walker caps. Bumped from 500 / 12 — modern Next.js __NEXT_DATA__
    // blobs (e.g. Vercel marketing pages, CMS-driven blogs) carry several
    // thousand prose-shaped strings nested up to ~20 levels deep. The earlier
    // values silently truncated those, dropping the article body. New values
    // are 4-10× the largest blob observed in dogfood — sanity stops against
    // runaway walks, not coverage limits.
    private const int MaxStringsCollected = 5_000;
    private const int MaxDepth = 32;

    public static string ExtractMainContent(IDocument document)
    {
        var script = document.QuerySelector("script#__NEXT_DATA__");
        if (script is null) return string.Empty;
        var json = script.TextContent;
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, JdOpts);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        var collected = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // Prefer props.pageProps (the canonical content root) when present;
            // fall back to walking the whole document.
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("props", out var props)
                && props.ValueKind == JsonValueKind.Object
                && props.TryGetProperty("pageProps", out var pageProps))
            {
                Walk(pageProps, collected, seen, depth: 0);
            }
            else
            {
                Walk(root, collected, seen, depth: 0);
            }
        }
        finally
        {
            doc.Dispose();
        }

        if (collected.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var s in collected)
        {
            sb.Append(s).Append('\n').Append('\n');
        }
        return sb.ToString().Trim();
    }

    private static void Walk(JsonElement el, List<string> sink, HashSet<string> seen, int depth)
    {
        if (depth > MaxDepth || sink.Count >= MaxStringsCollected) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var s = el.GetString();
                if (s is null) return;
                if (!LooksLikeProse(s)) return;
                if (seen.Add(s))
                {
                    sink.Add(s);
                }
                return;
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    // Skip well-known non-content keys to keep noise down.
                    var n = prop.Name;
                    if (n is "url" or "href" or "src" or "image" or "imageUrl"
                            or "_id" or "id" or "key" or "slug" or "handle"
                            or "className" or "style" or "css" or "html"
                            or "scripts" or "stylesheets" or "buildId"
                            or "assetPrefix" or "publicRuntimeConfig"
                            or "__N_SSP" or "__N_SSG"
                            or "createdAt" or "updatedAt" or "modifiedAt")
                        continue;
                    Walk(prop.Value, sink, seen, depth + 1);
                }
                return;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    if (sink.Count >= MaxStringsCollected) return;
                    Walk(item, sink, seen, depth + 1);
                }
                return;
        }
    }

    /// <summary>
    /// Heuristic prose filter: must be at least 80 chars, contain a space,
    /// not be a URL / data URI / CSS variable / serialised JSON / clearly
    /// machine-readable identifier.
    /// </summary>
    private static bool LooksLikeProse(string s)
    {
        if (s.Length < MinStringLengthForContent) return false;
        if (!s.Contains(' ')) return false;
        // URL / scheme prefixes
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.StartsWith("//", StringComparison.Ordinal)) return false;
        if (s.StartsWith("/", StringComparison.Ordinal) && !s.Contains(' ')) return false;
        // Serialised JSON
        var first = s[0];
        if (first == '{' || first == '[') return false;
        // CSS variable definitions / style strings — colon followed by short value
        if (first == '-' && s.Length > 2 && s[1] == '-') return false;
        return true;
    }
}
