using System.Text;
using System.Text.Json;
using AngleSharp.Dom;

namespace StyloExtract.Heuristics;

/// <summary>
/// Rehydration content extractor for the Discourse forum framework.
///
/// <para>
/// Discourse renders every page as an Ember.js single-page app. The static HTML
/// the server ships contains essentially zero post content; the actual topic +
/// posts live in a JSON blob in the <c>data-preloaded</c> attribute on a hidden
/// <c>&lt;div id="data-preloaded"&gt;</c>. The Ember runtime parses it after the
/// document loads and renders the post-stream.
/// </para>
///
/// <para>
/// For our purposes the JSON IS the content. Find the element, decode the
/// attribute, walk the topic_NNN entries' <c>post_stream.posts[*].cooked</c>
/// HTML strings, and concatenate. That's the topic body the heuristic
/// classifier can't reach.
/// </para>
///
/// <para>
/// Empirical: WCXB diagnostic surfaced 13 catastrophic forum pages (pred_chars=1)
/// all sharing the Discourse <c>data-preloaded</c> pattern (forums.eveonline.com,
/// forum.level1techs.com, forum.lingq.com, boards.straightdope.com,
/// forum.mssociety.org.uk, forum.hifiguides.com, etc.). Discourse powers
/// 5 000+ public forums; one extractor covers them all.
/// </para>
///
/// <para>
/// Fallback shape: same as <see cref="JsonLdContentExtractor"/>. Caller checks
/// whether the heuristic produced meaningful content first, and only invokes
/// this extractor when the heuristic returned essentially nothing. The
/// rehydrated content would otherwise duplicate well-structured pages where
/// the heuristic already finds posts.
/// </para>
/// </summary>
public static class DiscourseRehydrationExtractor
{
    private static readonly JsonDocumentOptions JdOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Extract concatenated post-body text from the page's Discourse
    /// <c>data-preloaded</c> blob, if any. Returns empty when the page is not
    /// Discourse or the blob is malformed.
    /// </summary>
    public static string ExtractMainContent(IDocument document)
    {
        var el = document.QuerySelector("div#data-preloaded");
        if (el is null) return string.Empty;
        var attr = el.GetAttribute("data-preloaded");
        if (string.IsNullOrWhiteSpace(attr)) return string.Empty;

        JsonDocument outer;
        try
        {
            outer = JsonDocument.Parse(attr, JdOpts);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        try
        {
            if (outer.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            foreach (var topLevel in outer.RootElement.EnumerateObject())
            {
                // Discourse keys: "topic_NNN" (the topic we want), plus a
                // bunch of "site", "preloadStore", "siteSettings" etc. that
                // are user/config payloads not topic content. Skip the
                // non-topic keys.
                if (!topLevel.Name.StartsWith("topic_", StringComparison.Ordinal)) continue;
                if (topLevel.Value.ValueKind != JsonValueKind.String) continue;

                var innerJson = topLevel.Value.GetString();
                if (string.IsNullOrEmpty(innerJson)) continue;

                JsonDocument inner;
                try
                {
                    inner = JsonDocument.Parse(innerJson, JdOpts);
                }
                catch (JsonException)
                {
                    continue;
                }
                using (inner)
                {
                    AppendTopicTitle(inner.RootElement, sb);
                    AppendPostStreamPosts(inner.RootElement, sb);
                }
            }
        }
        finally
        {
            outer.Dispose();
        }

        return sb.ToString().Trim();
    }

    private static void AppendTopicTitle(JsonElement topic, StringBuilder sb)
    {
        if (topic.ValueKind != JsonValueKind.Object) return;
        if (topic.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
        {
            var s = title.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                sb.Append(s).Append('\n').Append('\n');
            }
        }
    }

    private static void AppendPostStreamPosts(JsonElement topic, StringBuilder sb)
    {
        if (topic.ValueKind != JsonValueKind.Object) return;
        if (!topic.TryGetProperty("post_stream", out var stream)) return;
        if (stream.ValueKind != JsonValueKind.Object) return;
        if (!stream.TryGetProperty("posts", out var posts)) return;
        if (posts.ValueKind != JsonValueKind.Array) return;

        foreach (var post in posts.EnumerateArray())
        {
            if (post.ValueKind != JsonValueKind.Object) continue;
            if (!post.TryGetProperty("cooked", out var cooked)) continue;
            if (cooked.ValueKind != JsonValueKind.String) continue;
            var cookedHtml = cooked.GetString();
            if (string.IsNullOrWhiteSpace(cookedHtml)) continue;
            // The cooked field is HTML — Discourse server-side markdown render.
            // Strip tags to plain text for the fallback content block; the
            // renderer paragraph-separates so the result reads naturally.
            var text = StripTagsToText(cookedHtml);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(text).Append('\n').Append('\n');
            }
        }
    }

    /// <summary>
    /// Strip HTML tags from a snippet of server-rendered Discourse post HTML
    /// (Discourse calls this the "cooked" field). Keep the textContent
    /// intact, separate adjacent text by spaces, drop tag markup. Simple
    /// state machine — no DOM parser allocation for what is typically &lt;
    /// 10 KB of HTML per post.
    /// </summary>
    private static string StripTagsToText(string html)
    {
        var sb = new StringBuilder(html.Length);
        bool inTag = false;
        bool lastWasSpace = false;
        for (int i = 0; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '<')
            {
                inTag = true;
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }
            if (c == '>')
            {
                inTag = false;
                continue;
            }
            if (inTag) continue;
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }
            sb.Append(c);
            lastWasSpace = false;
        }
        return sb.ToString().Trim();
    }
}
