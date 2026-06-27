using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;

namespace StyloExtract.Streaming;

/// <summary>
/// First-pass streaming-template inducer rewritten in Task 4 (alpha.24) to
/// emit <see cref="IdentityClaim"/> tripwires instead of MinHash fences.
///
/// Parses the page with AngleSharp once, picks three target elements
/// (prefix / content-start / content-end) using the same shape heuristics
/// as alpha.16..23, and calls
/// <see cref="IdentityClaimExtractor.Extract(IElement, IClassStabilityFilter)"/>
/// on each. The resulting <see cref="ElementClaimSet"/>s are projected
/// into stable <see cref="IdentityClaim"/>s the scanner will match exactly
/// against the tokenizer's per-event hash data.
///
/// Heuristic targets:
/// <list type="bullet">
///   <item><description>Prefix: first <c>&lt;header&gt;</c>, fallback to first
///   <c>&lt;nav&gt;</c>, last-ditch <c>&lt;body&gt;</c>.</description></item>
///   <item><description>Content start: first ancestor of the paragraph
///   cluster — prefer <c>&lt;article&gt;</c>, then <c>&lt;main&gt;</c>,
///   then the first <c>&lt;p&gt;</c> in a 2+ paragraph cluster.</description></item>
///   <item><description>Content end: same element as content start (the
///   scanner watches for its CLOSE event). On the wire this collapses to
///   "fire the same tripwire on close".</description></item>
/// </list>
///
/// Returns null when no plausible targets exist (plain text, SVG-only,
/// image-only pages). Callers should treat null as "leave NoTemplate
/// alone, try again next visit".
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

        // AngleSharp's HtmlParser takes a string — decode UTF-8 once.
        // Induction is a per-host operation (not the per-request hot path),
        // so the allocation cost is fine.
        var source = Encoding.UTF8.GetString(html);
        var doc = s_parser.ParseDocument(source);
        if (doc.Body is null) return null;

        var prefixEl = FindPrefixElement(doc);
        if (prefixEl is null) return null;

        var contentEl = FindContentElement(doc);
        if (contentEl is null) return null;

        var endEl = FindContentEndElement(doc, contentEl);
        if (endEl is null) return null;

        var prefixClaim = ToIdentityClaim(IdentityClaimExtractor.Extract(prefixEl, _classFilter));
        var contentStartClaim = ToIdentityClaim(IdentityClaimExtractor.Extract(contentEl, _classFilter));
        // ContentEnd fires on the CLOSE event for the content element. Close
        // tags carry no attributes (no id, no classes), so the tripwire must
        // rely on tag-name alone. Collapse the full claim to its tag-only
        // form for this slot.
        var contentEndClaim = TagOnlyClaim(IdentityClaimExtractor.Extract(endEl, _classFilter));

        return new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = host,
            PrefixTripwire = prefixClaim,
            ContentStartTripwire = contentStartClaim,
            ContentEndTripwire = contentEndClaim,
            BailoutBytes = 5_000_000,
            MaxCaptureBytes = 5_000_000,
        };
    }

    /// <summary>
    /// Diagnostic counterpart of <see cref="Induce"/> — returns a
    /// human-readable summary of which elements the heuristic would pick.
    /// Cheap (one parse pass).
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
        var endEl = FindContentEndElement(doc, contentEl);
        if (endEl is null) return null;

        return new InducedSummary(
            DescribeElement(prefixEl),
            DescribeElement(contentEl),
            DescribeElement(endEl));
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

        // Paragraph-cluster fallback: at least two consecutive <p> siblings
        // somewhere in the document. Return their nearest common parent so
        // the content-end tripwire fires on the parent's close (which the
        // scanner's depth tracking can identify cleanly).
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

    private static IElement? FindContentEndElement(IDocument doc, IElement contentEl)
    {
        // The scanner watches for the CLOSE event of the content element by
        // default — same identity claim, fired on close. Per the task design
        // (prefix/content-start/content-end as three independent tripwires)
        // we still emit a distinct claim for the end target. The natural
        // pick is the same element as content-start (close of content =
        // close of region). If a downstream <footer> exists we could in
        // principle use it as a coarser end marker, but firing on the
        // content element's own close is more precise.
        return contentEl;
    }

    /// <summary>
    /// Project the full identity snapshot into a streaming-friendly tripwire.
    ///
    /// The scanner's per-event hash data is bounded (see
    /// <see cref="TagEvent.MaxClassesPerEvent"/> /
    /// <see cref="TagEvent.MaxAttrPairsPerEvent"/>): a tripwire that requires
    /// 20 classes will never satisfy because TagEvent only carries 8. So
    /// the inducer narrows the claim:
    ///
    /// - If the element has a stable id, the tripwire is tag + id (single
    ///   strong discriminator, matches the layout-side preference).
    /// - Otherwise: tag + at most the first two stable classes. Two is
    ///   enough to disambiguate on most real pages while staying well
    ///   under the per-event cap. Going wider would risk losing the
    ///   match on pages where utility-class ordering shifts between
    ///   sessions.
    /// </summary>
    private static IdentityClaim ToIdentityClaim(ElementClaimSet claimSet)
    {
        if (claimSet.Id is not null)
        {
            return new IdentityClaim
            {
                Tag = claimSet.Tag,
                TagHash = claimSet.TagHash,
                Id = claimSet.Id,
                IdHash = claimSet.IdHash,
            };
        }

        const int maxClasses = 2;
        var keepClasses = claimSet.Classes.Count <= maxClasses
            ? claimSet.Classes
            : (IReadOnlyList<string>)claimSet.Classes.Take(maxClasses).ToArray();
        var keepHashes = claimSet.ClassHashes.Count <= maxClasses
            ? claimSet.ClassHashes
            : (IReadOnlyList<ulong>)claimSet.ClassHashes.Take(maxClasses).ToArray();

        return new IdentityClaim
        {
            Tag = claimSet.Tag,
            TagHash = claimSet.TagHash,
            Classes = keepClasses,
            ClassHashes = keepHashes,
        };
    }

    private static IdentityClaim TagOnlyClaim(ElementClaimSet claimSet) => new()
    {
        Tag = claimSet.Tag,
        TagHash = claimSet.TagHash,
    };

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
