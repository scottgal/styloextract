using System.IO.Hashing;
using System.Text;
using AngleSharp.Dom;
using StyloExtract.Heuristics;

namespace StyloExtract.Fingerprint;

public sealed class AnchorPathFingerprinter
{
    private readonly ClassNoiseFilter _classNoise;
    private readonly MinHashSketcher _sketcher;

    public AnchorPathFingerprinter(ClassNoiseFilter classNoise, MinHashSketcher sketcher)
    {
        _classNoise = classNoise;
        _sketcher = sketcher;
    }

    public uint[] Sketch(IDocument document)
    {
        if (document.Body is null) return _sketcher.Sketch(Array.Empty<ulong>());
        var anchors = document.QuerySelectorAll("a");
        var elements = new List<ulong>(anchors.Length);
        foreach (var a in anchors)
        {
            var tagPathHash = TagPathHash(a);
            var href = a.GetAttribute("href") ?? "";
            var domain = ExtractDomain(href);
            var hasHash = href.Contains('#') ? 1 : 0;
            var classes = (a.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var classHash = HashClasses(_classNoise.Filter(classes));
            var sb = new StringBuilder();
            sb.Append(tagPathHash); sb.Append('|');
            sb.Append(domain); sb.Append('|');
            sb.Append(hasHash); sb.Append('|');
            sb.Append(classHash);
            elements.Add(XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(sb.ToString())));
        }
        return _sketcher.Sketch(elements);
    }

    private static ulong TagPathHash(IElement element)
    {
        var sb = new StringBuilder();
        var current = element.ParentElement;
        while (current is not null)
        {
            sb.Append(current.LocalName);
            sb.Append('/');
            current = current.ParentElement;
        }
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string ExtractDomain(string href)
    {
        if (string.IsNullOrEmpty(href)) return "";
        if (href.StartsWith("/") || href.StartsWith("#")) return "";
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return uri.Host.ToLowerInvariant();
        }
        return "";
    }

    private static ulong HashClasses(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return 0UL;
        var sorted = string.Join(",", tokens.OrderBy(t => t, StringComparer.Ordinal));
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(sorted));
    }
}
