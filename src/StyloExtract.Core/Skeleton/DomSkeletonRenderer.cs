using System.Text;
using AngleSharp.Dom;

namespace StyloExtract.Core.Skeleton;

/// <summary>
/// Walks a parsed (and cleaned) DOM and emits a slim tree-with-exemplars
/// text representation: ~1-4 KB / ~1-2K tokens for the median page;
/// ~12 KB worst case. Designed as the input to
/// <see cref="StyloExtract.Core.Llm.LlmTemplateInducer"/>; the LLM sees
/// page STRUCTURE plus short text exemplars, not raw HTML, so context
/// pressure stays well inside any model's window even for Wikipedia-shape
/// pages.
///
/// <para>
/// What gets in: candidate elements (semantic tags, blocky divs, anything
/// the segmenter would have considered), their tag / class tokens / id /
/// child count / text length / link density, and a ≤120-char text
/// excerpt per element.
/// </para>
///
/// <para>
/// What gets dropped: <c>&lt;script&gt;</c>, <c>&lt;style&gt;</c>,
/// <c>&lt;noscript&gt;</c>, <c>&lt;svg&gt;</c>, <c>&lt;template&gt;</c>,
/// inline styles, data-* attributes, base64 image payloads, comments,
/// class tokens that look like random hashes (Tailwind JIT / CSS-modules),
/// repeated children beyond ~3 exemplars per group, anything below a
/// configurable depth cap.
/// </para>
///
/// <para>
/// Pure C#, AOT-clean, single-StringBuilder allocation per render.
/// </para>
/// </summary>
public sealed class DomSkeletonRenderer
{
    private readonly SkeletonRenderOptions _options;

    public DomSkeletonRenderer(SkeletonRenderOptions? options = null)
    {
        _options = options ?? SkeletonRenderOptions.Default;
    }

    /// <summary>
    /// Render <paramref name="document"/>'s body into a slim skeleton.
    /// Returns an empty string if the document has no body OR the body
    /// is empty (no element children, no text). Assumes the caller has
    /// already run <c>DomCleaner</c> on the document — script / style /
    /// noscript / comments must be physically removed from the DOM
    /// before this is called, because the per-element excerpt reads
    /// <c>TextContent</c> which would otherwise include those nodes'
    /// text content via descendant aggregation.
    /// </summary>
    public string Render(IDocument document)
    {
        if (document.Body is null) return string.Empty;
        if (document.Body.ChildElementCount == 0 &&
            string.IsNullOrWhiteSpace(document.Body.TextContent))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(_options.InitialBufferBytes);
        sb.Append("ROOT body\n");
        WriteChildren(sb, document.Body, depth: 1, lastChildPath: new bool[_options.MaxDepth + 1]);
        return sb.ToString();
    }

    private void WriteChildren(StringBuilder sb, IElement parent, int depth, bool[] lastChildPath)
    {
        if (depth > _options.MaxDepth) return;
        var visibleChildren = CollectVisibleChildren(parent);
        if (visibleChildren.Count == 0) return;

        var groups = GroupRepeated(visibleChildren);
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            bool isLastGroup = gi == groups.Count - 1;
            if (g.IsRepeated)
            {
                WriteRepeatedGroup(sb, g, depth, lastChildPath, isLastGroup);
            }
            else
            {
                // One element per group of size 1.
                var el = g.Elements[0];
                WriteElement(sb, el, depth, lastChildPath, isLastGroup);
                // Recurse into children.
                if (depth + 1 <= _options.MaxDepth)
                {
                    lastChildPath[depth] = isLastGroup;
                    WriteChildren(sb, el, depth + 1, lastChildPath);
                    lastChildPath[depth] = false;
                }
            }
        }
    }

    private void WriteElement(StringBuilder sb, IElement el, int depth, bool[] lastChildPath, bool isLast)
    {
        AppendIndent(sb, depth, lastChildPath, isLast);
        AppendElementSummary(sb, el);
        sb.Append('\n');
    }

    private void WriteRepeatedGroup(StringBuilder sb, ElementGroup g, int depth, bool[] lastChildPath, bool isLastGroup)
    {
        var total = g.Elements.Count;
        var exemplars = Math.Min(_options.ExemplarsPerRepeatedGroup, total);
        AppendIndent(sb, depth, lastChildPath, isLastGroup);
        sb.Append("… ");
        sb.Append(total);
        sb.Append(" repeated ");
        sb.Append(SimpleTagDescriptor(g.Elements[0]));
        sb.Append(" children (");
        sb.Append(exemplars);
        sb.Append(" exemplar");
        sb.Append(exemplars == 1 ? "" : "s");
        sb.Append(" below)\n");

        lastChildPath[depth] = isLastGroup;
        for (int i = 0; i < exemplars; i++)
        {
            bool exLast = i == exemplars - 1;
            AppendIndent(sb, depth + 1, lastChildPath, exLast);
            AppendElementSummary(sb, g.Elements[i]);
            sb.Append('\n');
        }
        lastChildPath[depth] = false;
    }

    // ---------- summary line ----------

    private void AppendElementSummary(StringBuilder sb, IElement el)
    {
        // tag.class#id — text="…" childcount linkDensity textLen
        sb.Append(el.LocalName);
        AppendClassTokens(sb, el);
        AppendId(sb, el);

        int childCount = el.ChildElementCount;
        int textLen = el.TextContent.Length;
        if (childCount > 0)
        {
            sb.Append(" children=").Append(childCount);
        }
        if (textLen > 0)
        {
            sb.Append(" textLen=").Append(textLen);
        }
        double density = ComputeLinkDensity(el);
        if (density > 0.05)
        {
            sb.Append(" linkDensity=").Append(density.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }
        var excerpt = ExtractExcerpt(el, _options.MaxExcerptChars);
        if (excerpt.Length > 0)
        {
            sb.Append(" — \"").Append(excerpt).Append('"');
        }
    }

    private void AppendClassTokens(StringBuilder sb, IElement el)
    {
        var classAttr = el.GetAttribute("class");
        if (string.IsNullOrEmpty(classAttr)) return;
        // Emit at most _options.MaxClassTokensPerElement tokens; drop tokens
        // that look like random hashes (Tailwind JIT / CSS-modules / build-
        // hash class names) — they're noise for an LLM trying to infer the
        // page template. A "hash-looking" token is alphanumeric with mixed
        // case AND no semantic separators, ≥8 chars long.
        int emitted = 0;
        ReadOnlySpan<char> remaining = classAttr.AsSpan();
        while (remaining.Length > 0 && emitted < _options.MaxClassTokensPerElement)
        {
            int spaceIdx = remaining.IndexOf(' ');
            ReadOnlySpan<char> token = spaceIdx < 0 ? remaining : remaining[..spaceIdx];
            if (token.Length > 0 && !LooksLikeHashClassName(token))
            {
                sb.Append('.');
                sb.Append(token);
                emitted++;
            }
            if (spaceIdx < 0) break;
            remaining = remaining[(spaceIdx + 1)..];
        }
    }

    private void AppendId(StringBuilder sb, IElement el)
    {
        var id = el.GetAttribute("id");
        if (string.IsNullOrEmpty(id)) return;
        if (LooksLikeHashClassName(id.AsSpan())) return;
        sb.Append('#').Append(id);
    }

    private static bool LooksLikeHashClassName(ReadOnlySpan<char> token)
    {
        // Conservative: only call something a hash if it's long AND every char
        // is alphanumeric (no '-' / '_' / ':' etc which appear in real
        // utility-class systems like Tailwind's bg-blue-500) AND it mixes
        // upper + lower case OR contains a run of digits ≥3.
        if (token.Length < 8) return false;
        bool hasUpper = false, hasLower = false;
        int digitRun = 0, maxDigitRun = 0;
        foreach (var c in token)
        {
            if (c == '-' || c == '_' || c == ':' || c == '/') return false;
            if (!char.IsLetterOrDigit(c)) return false;
            if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsLower(c)) hasLower = true;
            if (char.IsDigit(c)) { digitRun++; if (digitRun > maxDigitRun) maxDigitRun = digitRun; }
            else digitRun = 0;
        }
        return (hasUpper && hasLower) || maxDigitRun >= 3;
    }

    // ---------- children / grouping ----------

    private List<IElement> CollectVisibleChildren(IElement parent)
    {
        var list = new List<IElement>(parent.ChildElementCount);
        foreach (var child in parent.Children)
        {
            if (IsDropped(child)) continue;
            list.Add(child);
        }
        return list;
    }

    private static bool IsDropped(IElement el)
    {
        return el.LocalName switch
        {
            "script" or "style" or "noscript" or "template" or "svg" or "link" or "meta" => true,
            _ => false,
        };
    }

    private List<ElementGroup> GroupRepeated(List<IElement> children)
    {
        var groups = new List<ElementGroup>();
        if (children.Count == 0) return groups;
        // Single-pass run-length grouping: contiguous siblings with the same
        // LocalName collapse into one group when the run length crosses the
        // threshold. Non-contiguous repetitions (e.g. <h2> interleaved with
        // <div>) stay separate because that's not a "repeated list" pattern.
        int i = 0;
        while (i < children.Count)
        {
            int runEnd = i + 1;
            while (runEnd < children.Count &&
                   children[runEnd].LocalName == children[i].LocalName)
            {
                runEnd++;
            }
            int runLength = runEnd - i;
            if (runLength >= _options.RepeatedRunMinSize)
            {
                var run = new List<IElement>(runLength);
                for (int k = i; k < runEnd; k++) run.Add(children[k]);
                groups.Add(new ElementGroup(run, IsRepeated: true));
            }
            else
            {
                for (int k = i; k < runEnd; k++)
                    groups.Add(new ElementGroup(new[] { children[k] }, IsRepeated: false));
            }
            i = runEnd;
        }
        return groups;
    }

    private readonly record struct ElementGroup(IReadOnlyList<IElement> Elements, bool IsRepeated);

    // ---------- helpers ----------

    private static string SimpleTagDescriptor(IElement el)
    {
        var classAttr = el.GetAttribute("class");
        if (string.IsNullOrEmpty(classAttr)) return el.LocalName;
        // First non-hash token.
        ReadOnlySpan<char> remaining = classAttr.AsSpan();
        while (remaining.Length > 0)
        {
            int sp = remaining.IndexOf(' ');
            ReadOnlySpan<char> token = sp < 0 ? remaining : remaining[..sp];
            if (token.Length > 0 && !LooksLikeHashClassName(token))
                return $"{el.LocalName}.{token.ToString()}";
            if (sp < 0) break;
            remaining = remaining[(sp + 1)..];
        }
        return el.LocalName;
    }

    private static double ComputeLinkDensity(IElement el)
    {
        int total = el.TextContent.Length;
        if (total == 0) return 0;
        int linkText = 0;
        foreach (var a in el.QuerySelectorAll("a")) linkText += a.TextContent.Length;
        return (double)linkText / total;
    }

    private static string ExtractExcerpt(IElement el, int maxChars)
    {
        var raw = el.TextContent;
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new StringBuilder(Math.Min(raw.Length, maxChars));
        bool prevWs = true;
        for (int i = 0; i < raw.Length && sb.Length < maxChars; i++)
        {
            var c = raw[i];
            if (c == '\n' || c == '\r' || c == '\t' || c == ' ')
            {
                if (!prevWs && sb.Length > 0) { sb.Append(' '); prevWs = true; }
                continue;
            }
            if (c == '"') sb.Append('\''); else sb.Append(c);
            prevWs = false;
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        if (sb.Length >= maxChars && raw.Length > maxChars) sb.Append('…');
        return sb.ToString();
    }

    private void AppendIndent(StringBuilder sb, int depth, bool[] lastChildPath, bool isLast)
    {
        // Tree-drawing characters borrowed from /tree: │ ├ └ ─
        for (int i = 1; i < depth; i++)
        {
            sb.Append(lastChildPath[i] ? "    " : "│   ");
        }
        sb.Append(isLast ? "└─ " : "├─ ");
    }
}

/// <summary>
/// Tunables for <see cref="DomSkeletonRenderer"/>. Defaults match the
/// design doc's "median page ~1-4 KB" target.
/// </summary>
public sealed record SkeletonRenderOptions
{
    public int MaxDepth { get; init; } = 8;
    public int MaxExcerptChars { get; init; } = 120;
    public int MaxClassTokensPerElement { get; init; } = 3;
    public int RepeatedRunMinSize { get; init; } = 5;
    public int ExemplarsPerRepeatedGroup { get; init; } = 3;
    public int InitialBufferBytes { get; init; } = 4096;

    public static SkeletonRenderOptions Default { get; } = new();
}
