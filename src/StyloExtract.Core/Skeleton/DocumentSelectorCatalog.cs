using System.Text;
using AngleSharp.Dom;

namespace StyloExtract.Core.Skeleton;

/// <summary>
/// Enumerate a closed set of CSS selectors that actually exist on a cleaned
/// document, ranked by "looks like a content container" — tag+id+class for
/// elements that have substantial text mass. The LLM inducer pairs this with
/// the slim skeleton in its user prompt so the model picks from a real list
/// instead of inventing classes that don't exist. Combined with the post-parse
/// validator on <see cref="LlmTemplateInducer"/>, two-layer defence against
/// selector hallucination.
/// </summary>
public sealed class DocumentSelectorCatalog
{
    /// <summary>
    /// Build the catalog string the LLM sees. Format is one selector per line,
    /// most-promising first, capped to <paramref name="maxSelectors"/>.
    /// Selectors are de-duplicated and never include hash-shaped tokens
    /// (Tailwind JIT, CSS modules) that change page-to-page.
    /// </summary>
    public string Render(IDocument document, int maxSelectors = 40)
    {
        var body = document.Body;
        if (body is null) return string.Empty;

        // Score elements by text mass: TextContent length less link text density.
        // Same intuition as the heuristic block classifier — content-bearing
        // elements have substantial non-link text.
        var ranked = new List<(IElement Element, int Score)>();
        foreach (var el in body.Descendants<IElement>())
        {
            var text = el.TextContent;
            if (text.Length < 80) continue;
            // Skip well-known non-content tags so we don't waste catalog slots
            // on <script>/<style> equivalents (DomCleaner should have stripped
            // them, but defensive).
            var tag = el.LocalName;
            if (tag is "script" or "style" or "noscript" or "svg" or "select") continue;
            // Cheap link-density proxy: count <a> descendants.
            var linkCount = el.QuerySelectorAll("a").Length;
            var score = text.Length - linkCount * 20;
            if (score <= 0) continue;
            ranked.Add((el, score));
        }

        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var emitted = 0;
        foreach (var (el, _) in ranked.OrderByDescending(x => x.Score))
        {
            if (emitted >= maxSelectors) break;
            foreach (var selector in EnumerateSelectors(el))
            {
                if (!seen.Add(selector)) continue;
                sb.Append("  ").AppendLine(selector);
                if (++emitted >= maxSelectors) break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Yield CSS-selector candidates for a single element, in decreasing
    /// specificity: tag#id, tag.class1.class2, #id, .class1.class2, tag.
    /// Hash-shaped class tokens (8+ chars of /^[a-z0-9_-]{6,}$/ with no vowels)
    /// are filtered — those are Tailwind JIT / CSS-modules cache-busters that
    /// won't survive a deploy.
    /// </summary>
    private static IEnumerable<string> EnumerateSelectors(IElement el)
    {
        var tag = el.LocalName;
        var id = el.GetAttribute("id");
        var classAttr = el.GetAttribute("class");
        var classes = SplitClasses(classAttr);

        if (!string.IsNullOrEmpty(id))
        {
            yield return tag + "#" + id;
            yield return "#" + id;
        }
        if (classes.Count > 0)
        {
            var classPart = "." + string.Join(".", classes.Take(3));
            yield return tag + classPart;
            yield return classPart;
        }
    }

    private static List<string> SplitClasses(string? classAttr)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(classAttr)) return result;
        foreach (var token in classAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsHashShaped(token)) continue;
            if (!IsValidCssIdent(token)) continue;
            result.Add(token);
        }
        return result;
    }

    private static bool IsValidCssIdent(string s)
    {
        if (s.Length == 0) return false;
        // CSS identifier: letter, then letters/digits/hyphen/underscore.
        if (!(char.IsLetter(s[0]) || s[0] == '_' || s[0] == '-')) return false;
        for (int i = 1; i < s.Length; i++)
        {
            var c = s[i];
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }
        return true;
    }

    /// <summary>
    /// Hash-shaped token heuristic: long, no vowels, looks random. Matches
    /// Tailwind JIT cache-buster classes ("_2x3kbf-9", "a1b2c3d4") that change
    /// every deploy and would tie the template to a specific build.
    /// </summary>
    private static bool IsHashShaped(string s)
    {
        if (s.Length < 6) return false;
        int vowels = 0;
        int digits = 0;
        for (int i = 0; i < s.Length; i++)
        {
            var c = char.ToLowerInvariant(s[i]);
            if (c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y') vowels++;
            if (char.IsDigit(c)) digits++;
        }
        // Long token with no vowels and several digits: cache-buster.
        if (s.Length >= 8 && vowels == 0 && digits >= 1) return true;
        // Underscore-prefixed digits: CSS-modules style.
        if (s[0] == '_' && digits >= 2) return true;
        return false;
    }
}
