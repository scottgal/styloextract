using System.Text.Json;
using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

/// <summary>
/// Schema.org JSON-LD content extractor. Schema-aware fallback for pages where the
/// heuristic block classifier emits little or no content because the actual content is
/// not present in the static HTML (Discourse forums where post bodies are API-fetched,
/// SPAs that hydrate client-side, paywalled sites that ship only a teaser DOM, etc.).
///
/// Recognises the schema.org types that publishers most commonly use to surface their
/// real content for crawlers: Article, BlogPosting, NewsArticle, QAPage, FAQPage,
/// DiscussionForumPosting, ItemList, WebPage. For each, extracts the textual fields
/// (articleBody, text, mainEntity[].text, itemListElement[].name + description, etc.)
/// and joins them with paragraph separators.
///
/// Empirical: WCXB diagnostic surfaced 246 pages with F1 &lt; 0.3. Of those:
///   - 11 QAPage / 4 DiscussionForumPosting / 13 FAQPage / 5 ItemList / 3 NewsArticle /
///     2 BlogPosting / 2 Article have substantial schema.org blobs that the heuristic
///     misses because the content lives in JSON not in the DOM. Recovering even a
///     portion of each blob lifts F1 by 0.5-0.95 per affected page.
///
/// Pattern is intentionally a FALLBACK, not additive: the caller checks whether the
/// heuristic produced meaningful content first and only invokes this extractor when
/// the heuristic returned essentially nothing. The schema.org content is typically a
/// subset of what the heuristic finds on well-structured pages, so additive merging
/// would risk duplication.
/// </summary>
public static class JsonLdContentExtractor
{
    private static readonly JsonDocumentOptions JdOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Extract plain-text content from any recognised schema.org JSON-LD blobs in the
    /// document. Returns null when no recognised schema is found or all blobs are
    /// missing the expected text fields.
    /// </summary>
    public static string? ExtractMainContent(IDocument doc)
    {
        var scripts = doc.QuerySelectorAll("script[type='application/ld+json']");
        if (scripts.Length == 0) return null;

        var parts = new List<string>();
        foreach (var script in scripts)
        {
            var raw = script.TextContent.Trim();
            if (raw.Length == 0) continue;
            JsonDocument? jd = null;
            try { jd = JsonDocument.Parse(raw, JdOpts); }
            catch { continue; }
            using (jd)
            {
                ExtractFromValue(jd.RootElement, parts);
            }
        }

        if (parts.Count == 0) return null;
        var joined = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    private static void ExtractFromValue(JsonElement el, List<string> parts)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) ExtractFromValue(item, parts);
                break;
            case JsonValueKind.Object:
                ExtractFromObject(el, parts);
                break;
        }
    }

    private static void ExtractFromObject(JsonElement obj, List<string> parts)
    {
        var atType = ReadTypeName(obj);
        switch (atType)
        {
            case "Article":
            case "BlogPosting":
            case "NewsArticle":
            case "TechArticle":
            case "Report":
                EmitName(obj, parts);
                EmitField(obj, "headline", parts);
                EmitField(obj, "description", parts);
                EmitField(obj, "articleBody", parts);
                break;

            case "QAPage":
            case "Question":
                EmitName(obj, parts);
                EmitField(obj, "text", parts);
                EmitField(obj, "description", parts);
                if (obj.TryGetProperty("mainEntity", out var qme))
                    ExtractFromValue(qme, parts);
                EmitAnswers(obj, parts);
                break;

            case "Answer":
                EmitField(obj, "text", parts);
                break;

            case "FAQPage":
                EmitName(obj, parts);
                if (obj.TryGetProperty("mainEntity", out var faqme))
                    ExtractFromValue(faqme, parts);
                break;

            case "DiscussionForumPosting":
            case "SocialMediaPosting":
                EmitName(obj, parts);
                EmitField(obj, "headline", parts);
                EmitField(obj, "articleBody", parts);
                EmitField(obj, "text", parts);
                EmitComments(obj, parts);
                break;

            case "Comment":
                EmitField(obj, "text", parts);
                break;

            case "ItemList":
                EmitName(obj, parts);
                EmitField(obj, "description", parts);
                if (obj.TryGetProperty("itemListElement", out var ile))
                    ExtractFromValue(ile, parts);
                break;

            case "ListItem":
                EmitField(obj, "name", parts);
                EmitField(obj, "description", parts);
                if (obj.TryGetProperty("item", out var listItemInner))
                    ExtractFromValue(listItemInner, parts);
                break;

            case "WebPage":
                EmitField(obj, "name", parts);
                EmitField(obj, "description", parts);
                EmitField(obj, "text", parts);
                if (obj.TryGetProperty("mainEntity", out var wpme))
                    ExtractFromValue(wpme, parts);
                break;

            case "Recipe":
                EmitName(obj, parts);
                EmitField(obj, "description", parts);
                EmitField(obj, "recipeInstructions", parts);
                break;

            case "Event":
                EmitName(obj, parts);
                EmitField(obj, "description", parts);
                break;

            case "Product":
                // Products usually have only short descriptions in JSON-LD; the gold is
                // mostly review/spec content not in the schema. Skip to avoid emitting
                // shopping-cart metadata that would hurt precision.
                break;

            default:
                // Unknown schema: probe a few common content-bearing fields just in case.
                EmitField(obj, "articleBody", parts);
                EmitField(obj, "description", parts);
                break;
        }
    }

    private static string? ReadTypeName(JsonElement obj)
    {
        if (!obj.TryGetProperty("@type", out var t)) return null;
        if (t.ValueKind == JsonValueKind.String) return t.GetString();
        if (t.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in t.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
        }
        return null;
    }

    private static void EmitName(JsonElement obj, List<string> parts)
    {
        EmitField(obj, "name", parts);
    }

    private static void EmitField(JsonElement obj, string field, List<string> parts)
    {
        if (!obj.TryGetProperty(field, out var v)) return;
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) parts.Add(StripHtml(s));
                break;
            case JsonValueKind.Array:
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s2 = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s2)) parts.Add(StripHtml(s2));
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        // Some schemas wrap text in HowToStep / HowToSection / etc.
                        EmitField(item, "text", parts);
                        EmitField(item, "name", parts);
                    }
                }
                break;
        }
    }

    private static void EmitAnswers(JsonElement obj, List<string> parts)
    {
        if (obj.TryGetProperty("acceptedAnswer", out var aa)) ExtractFromValue(aa, parts);
        if (obj.TryGetProperty("suggestedAnswer", out var sa)) ExtractFromValue(sa, parts);
    }

    private static void EmitComments(JsonElement obj, List<string> parts)
    {
        if (obj.TryGetProperty("comment", out var c)) ExtractFromValue(c, parts);
        if (obj.TryGetProperty("comments", out var cs)) ExtractFromValue(cs, parts);
    }

    private static string StripHtml(string s)
    {
        // Lightweight: schema.org text fields commonly contain inline HTML from the CMS
        // (e.g. Discourse's mainEntity.text is the original Markdown rendered to HTML).
        // Strip tags and collapse whitespace for F1-friendly bag-of-words output.
        if (s.IndexOf('<') < 0) return s.Trim();
        var sb = new System.Text.StringBuilder(s.Length);
        bool inTag = false;
        foreach (var ch in s)
        {
            if (ch == '<') { inTag = true; sb.Append(' '); continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
