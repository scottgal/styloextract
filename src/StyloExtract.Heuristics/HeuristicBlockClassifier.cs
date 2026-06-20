using System.Reflection;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using StyloExtract.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StyloExtract.Heuristics;

public sealed class HeuristicBlockClassifier : IBlockClassifier
{
    private readonly string[] _footerPhrases;
    private readonly Regex[] _copyrightPatterns;
    private readonly string[] _cookiePhrases;
    private readonly HashSet<string> _navHints;
    private readonly HashSet<string> _adHints;

    private HeuristicBlockClassifier(
        string[] footerPhrases,
        Regex[] copyrightPatterns,
        string[] cookiePhrases,
        HashSet<string> navHints,
        HashSet<string> adHints)
    {
        _footerPhrases = footerPhrases;
        _copyrightPatterns = copyrightPatterns;
        _cookiePhrases = cookiePhrases;
        _navHints = navHints;
        _adHints = adHints;
    }

    public static HeuristicBlockClassifier LoadFromEmbeddedResources()
    {
        var assembly = typeof(HeuristicBlockClassifier).Assembly;
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

        T Load<T>(string name)
        {
            var resName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
            using var s = assembly.GetManifestResourceStream(resName)!;
            using var r = new StreamReader(s);
            return deserializer.Deserialize<T>(r.ReadToEnd());
        }

        var footer = Load<PhraseList>("footer-phrases.yaml");
        var copyright = Load<PatternList>("copyright-patterns.yaml");
        var cookie = Load<PhraseList>("cookie-banner-phrases.yaml");
        var nav = Load<HintList>("nav-class-hints.yaml");
        var ad = Load<HintList>("ad-class-hints.yaml");

        return new HeuristicBlockClassifier(
            footer.Phrases.ToArray(),
            copyright.Patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray(),
            cookie.Phrases.ToArray(),
            new HashSet<string>(nav.Hints, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(ad.Hints, StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ExtractedBlock> Classify(IReadOnlyList<IElement> blocks)
    {
        var result = new List<ExtractedBlock>(blocks.Count);
        int i = 0;
        foreach (var element in blocks)
        {
            var (role, confidence) = ClassifyOne(element);
            result.Add(new ExtractedBlock
            {
                Id = $"b{i:D4}",
                Role = role,
                Confidence = confidence,
                Text = element.TextContent.Trim(),
                Markdown = "",
                XPath = ComputeXPath(element),
                CssSelector = null,
                TextLength = element.TextContent.Length,
                LinkDensity = ComputeLinkDensity(element),
                Links = ExtractLinks(element)
            });
            i++;
        }
        return result;
    }

    private (BlockRole Role, double Confidence) ClassifyOne(IElement element)
    {
        var tag = element.TagName.ToLowerInvariant();
        var classTokens = (element.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = element.TextContent;
        var linkDensity = ComputeLinkDensity(element);

        bool ClassMatches(HashSet<string> hints) => classTokens.Any(c =>
            hints.Any(h => c.Contains(h, StringComparison.OrdinalIgnoreCase)));

        bool TextContainsAny(IEnumerable<string> phrases) =>
            phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

        bool TextMatchesAny(Regex[] patterns) => patterns.Any(r => r.IsMatch(text));

        if (tag is "nav" || ClassMatches(_navHints))
        {
            if (linkDensity > 0.7)
            {
                var isPrimary = HasAncestorTag(element, "header") || GetDepth(element) <= 3;
                return (isPrimary ? BlockRole.PrimaryNavigation : BlockRole.SecondaryNavigation, 0.85);
            }
        }

        if (tag == "footer" || classTokens.Any(c => c.Contains("footer", StringComparison.OrdinalIgnoreCase)))
        {
            if (TextContainsAny(_footerPhrases) || TextMatchesAny(_copyrightPatterns))
            {
                return (BlockRole.Footer, 0.9);
            }
            return (BlockRole.Footer, 0.6);
        }

        if (tag == "header") return (BlockRole.Header, 0.7);

        if (tag is "main" or "article" && text.Length > 200)
        {
            return (BlockRole.MainContent, 0.92);
        }

        if (tag == "aside")
        {
            return (linkDensity > 0.5 ? BlockRole.RelatedLinks : BlockRole.Sidebar, 0.75);
        }

        if (tag == "form" || element.QuerySelectorAll("input").Length >= 2)
        {
            return (BlockRole.Form, 0.85);
        }

        if (tag == "table") return (BlockRole.Table, 0.95);

        if (TextContainsAny(_cookiePhrases) && element.QuerySelector("button") is not null)
        {
            return (BlockRole.CookieBanner, 0.9);
        }

        if (ClassMatches(_adHints) && linkDensity > 0.5)
        {
            return (BlockRole.Advertisement, 0.8);
        }

        return text.Length > 200 ? (BlockRole.MainContent, 0.5) : (BlockRole.Boilerplate, 0.3);
    }

    private static double ComputeLinkDensity(IElement element)
    {
        var totalText = element.TextContent.Length;
        if (totalText == 0) return 0;
        var linkText = element.QuerySelectorAll("a").Sum(a => a.TextContent.Length);
        return (double)linkText / totalText;
    }

    private static IReadOnlyList<ExtractedLink> ExtractLinks(IElement element)
    {
        return element.QuerySelectorAll("a")
            .Select(a => new ExtractedLink
            {
                Text = a.TextContent.Trim(),
                Href = a.GetAttribute("href") ?? "",
                IsExternal = (a.GetAttribute("href") ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
    }

    private static bool HasAncestorTag(IElement element, string tag)
    {
        var current = element.ParentElement;
        while (current is not null)
        {
            if (current.TagName.Equals(tag, StringComparison.OrdinalIgnoreCase)) return true;
            current = current.ParentElement;
        }
        return false;
    }

    private static int GetDepth(IElement element)
    {
        int depth = 0;
        var current = element.ParentElement;
        while (current is not null) { depth++; current = current.ParentElement; }
        return depth;
    }

    private static string ComputeXPath(IElement element)
    {
        var parts = new Stack<string>();
        var current = (IElement?)element;
        while (current is not null && current.ParentElement is not null)
        {
            var idx = 1;
            var sibling = current.PreviousElementSibling;
            while (sibling is not null)
            {
                if (sibling.TagName == current.TagName) idx++;
                sibling = sibling.PreviousElementSibling;
            }
            parts.Push($"{current.TagName.ToLowerInvariant()}[{idx}]");
            current = current.ParentElement;
        }
        return "/" + string.Join("/", parts);
    }

    private sealed class PhraseList { public List<string> Phrases { get; set; } = new(); }
    private sealed class PatternList { public List<string> Patterns { get; set; } = new(); }
    private sealed class HintList { public List<string> Hints { get; set; } = new(); }
}
