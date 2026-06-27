using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;

namespace StyloExtract.Streaming;

/// <summary>
/// Streaming-template inducer rewritten in Task 13 of Phase 1 to emit
/// <see cref="BytePattern"/>s instead of <see cref="IdentityClaim"/>
/// tripwires. The inducer still parses the example page once with AngleSharp
/// (induction is a per-host operation, not the per-request hot path) and
/// reuses <see cref="DefaultClassStabilityFilter"/> for the class/id
/// stability gate.
///
/// Output shape (per template):
/// <list type="bullet">
///   <item><b>PrefixPattern</b>: open tag of the first page-chrome anchor
///   (header, nav, body) — the simplest pattern that fires before content.</item>
///   <item><b>ContentStartPattern</b>: open tag of the content element (
///   article / main / paragraph cluster parent) with the minimum stable
///   attributes needed to distinguish it from other tags of the same type
///   on the page.</item>
///   <item><b>ContentEndPattern</b>: close tag of the content element. The
///   scanner counts nested opens of the same tag name so a quoted inline
///   <c>&lt;article&gt;example&lt;/article&gt;</c> doesn't terminate the
///   capture early.</item>
/// </list>
///
/// Returns null when no plausible content target exists (plain text,
/// SVG-only, image-only pages). Callers should treat null as "leave
/// NoTemplate alone, try again next visit".
/// </summary>
public sealed class StreamingTemplateInducer
{
    private static readonly HtmlParser s_parser = BuildParser();
    private readonly IClassStabilityFilter _classFilter;

    public StreamingTemplateInducer()
        : this(new DefaultClassStabilityFilter())
    {
    }

    public StreamingTemplateInducer(IClassStabilityFilter classFilter)
    {
        _classFilter = classFilter;
    }

    private static HtmlParser BuildParser()
    {
        var context = BrowsingContext.New(Configuration.Default);
        return new HtmlParser(new HtmlParserOptions(), context);
    }

    /// <summary>
    /// Build a <see cref="StreamingTemplate"/> from observed page bytes for
    /// <paramref name="host"/>. Returns null when no plausible targets can
    /// be found.
    /// </summary>
    public StreamingTemplate? Induce(string host, ReadOnlySpan<byte> html)
    {
        if (string.IsNullOrEmpty(host)) return null;
        if (html.Length == 0) return null;

        var source = Encoding.UTF8.GetString(html);
        var doc = s_parser.ParseDocument(source);
        if (doc.Body is null) return null;

        var prefixEl = FindPrefixElement(doc);
        if (prefixEl is null) return null;

        var contentEl = FindContentElement(doc);
        if (contentEl is null) return null;

        var prefixPattern = BuildOpenPattern(prefixEl, doc, requireAttrs: false);
        var contentStartPattern = BuildOpenPattern(contentEl, doc, requireAttrs: true);
        var contentEndPattern = BuildClosePattern(contentEl);

        return new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = host,
            PrefixPattern = prefixPattern,
            ContentStartPattern = contentStartPattern,
            ContentEndPattern = contentEndPattern,
            BailoutBytes = 5_000_000,
            MaxCaptureBytes = 5_000_000,
        };
    }

    /// <summary>
    /// Diagnostic counterpart of <see cref="Induce"/> — returns a
    /// human-readable summary of which elements the heuristic picks.
    /// </summary>
    public InducedSummary? Describe(ReadOnlySpan<byte> html)
    {
        if (html.Length == 0) return null;
        var source = Encoding.UTF8.GetString(html);
        var doc = s_parser.ParseDocument(source);
        if (doc.Body is null) return null;

        var prefixEl = FindPrefixElement(doc);
        if (prefixEl is null) return null;
        var contentEl = FindContentElement(doc);
        if (contentEl is null) return null;

        return new InducedSummary(
            DescribeElement(prefixEl),
            DescribeElement(contentEl),
            DescribeElement(contentEl) + " (close)");
    }

    public readonly record struct InducedSummary(
        string PrefixMarker,
        string ContentStartMarker,
        string ContentEndMarker);

    private static IElement? FindPrefixElement(IDocument doc)
    {
        var header = doc.QuerySelector("header");
        if (header is not null) return header;
        var nav = doc.QuerySelector("nav");
        if (nav is not null) return nav;
        return doc.Body;
    }

    private static IElement? FindContentElement(IDocument doc)
    {
        var article = doc.QuerySelector("article");
        if (article is not null) return article;
        var main = doc.QuerySelector("main");
        if (main is not null) return main;

        var paragraphs = doc.QuerySelectorAll("p");
        IElement? lastParent = null;
        int run = 0;
        foreach (var p in paragraphs)
        {
            if (p.ParentElement is null) continue;
            if (ReferenceEquals(p.ParentElement, lastParent))
            {
                run++;
                if (run >= 2) return lastParent;
            }
            else
            {
                lastParent = p.ParentElement;
                run = 1;
            }
        }
        return null;
    }

    /// <summary>
    /// Build the open-tag <see cref="BytePattern"/> for <paramref name="element"/>.
    /// When <paramref name="requireAttrs"/> is true, picks the minimum stable
    /// attributes needed to distinguish this element from other tags of the
    /// same name on the page.
    /// </summary>
    private BytePattern BuildOpenPattern(IElement element, IDocument doc, bool requireAttrs)
    {
        var tagName = Encoding.UTF8.GetBytes(element.LocalName.ToLowerInvariant());
        var attrs = requireAttrs ? PickDisambiguatingAttrs(element, doc) : Array.Empty<AttrConstraint>();
        return new BytePattern
        {
            TagName = tagName,
            Attrs = attrs,
            IsClose = false,
            MaxScanBytes = 512,
        };
    }

    private static BytePattern BuildClosePattern(IElement element)
    {
        var tagName = Encoding.UTF8.GetBytes(element.LocalName.ToLowerInvariant());
        return new BytePattern
        {
            TagName = tagName,
            Attrs = Array.Empty<AttrConstraint>(),
            IsClose = true,
            MaxScanBytes = 64,
        };
    }

    /// <summary>
    /// Choose the minimum set of attribute constraints that would let the
    /// byte pattern distinguish <paramref name="element"/> from the other
    /// same-tag elements on the page.
    ///
    /// Preference order:
    /// 1. <c>id</c> alone if present and stable (the strongest discriminator).
    /// 2. First stable class if present and disambiguating.
    /// 3. Empty (the tag name alone is unique on the page).
    /// </summary>
    private AttrConstraint[] PickDisambiguatingAttrs(IElement element, IDocument doc)
    {
        // If only one element with this tag exists, no attrs needed.
        var sameTag = doc.QuerySelectorAll(element.LocalName);
        if (sameTag.Length <= 1) return Array.Empty<AttrConstraint>();

        var id = element.Id;
        if (!string.IsNullOrEmpty(id) && _classFilter.IsStable(id))
        {
            return new[]
            {
                new AttrConstraint
                {
                    Name = "id"u8.ToArray(),
                    Value = Encoding.UTF8.GetBytes(id),
                },
            };
        }

        if (!string.IsNullOrWhiteSpace(element.ClassName))
        {
            foreach (var cls in element.ClassName.Split(' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!_classFilter.IsStable(cls)) continue;
                return new[]
                {
                    new AttrConstraint
                    {
                        Name = "class"u8.ToArray(),
                        Value = Encoding.UTF8.GetBytes(cls),
                    },
                };
            }
        }

        return Array.Empty<AttrConstraint>();
    }

    private static string DescribeElement(IElement el)
    {
        var sb = new StringBuilder();
        sb.Append(el.LocalName);
        if (!string.IsNullOrEmpty(el.Id)) sb.Append('#').Append(el.Id);
        var cls = el.ClassName;
        if (!string.IsNullOrWhiteSpace(cls))
        {
            foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                sb.Append('.').Append(c);
        }
        return sb.ToString();
    }
}
