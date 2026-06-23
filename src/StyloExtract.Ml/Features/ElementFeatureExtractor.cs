using AngleSharp.Dom;

namespace StyloExtract.Ml.Features;

/// <summary>
/// Computes the 45-dimensional feature vector for a single DOM element. The
/// extractor takes a destination <c>Span&lt;float&gt;</c> and writes into it
/// directly, so the hot-path call allocates nothing beyond what AngleSharp's
/// own DOM traversal does. See <see cref="FeatureNames"/> for the slot layout.
///
/// <para>
/// Feature semantics match the design document
/// (<c>docs/ml-classifier-v2-design.md</c>). Anything that's "log-scale" is
/// <c>log(1 + count)</c> so zero is a real value and the distribution
/// compresses for the long tail.
/// </para>
/// </summary>
public sealed class ElementFeatureExtractor
{
    /// <summary>
    /// Write the feature vector for <paramref name="element"/> into
    /// <paramref name="dest"/>. <paramref name="dest"/> must be at least
    /// <see cref="FeatureNames.Dim"/> floats long; only the first
    /// <c>Dim</c> slots are written.
    /// </summary>
    public void Extract(IElement element, Span<float> dest)
    {
        if (dest.Length < FeatureNames.Dim)
            throw new ArgumentException($"dest must have at least {FeatureNames.Dim} slots", nameof(dest));
        dest[..FeatureNames.Dim].Clear();

        WriteTagOneHot(element, dest);
        WriteClassHashBuckets(element, dest);
        WriteDensityAndText(element, dest);
        WritePositionFeatures(element, dest);
        WriteSiblingShape(element, dest);
        WriteAncestorPresence(element, dest);
    }

    private static void WriteTagOneHot(IElement el, Span<float> dest)
    {
        var slot = el.LocalName switch
        {
            "main"    => FeatureNames.TagMain,
            "article" => FeatureNames.TagArticle,
            "section" => FeatureNames.TagSection,
            "aside"   => FeatureNames.TagAside,
            "nav"     => FeatureNames.TagNav,
            "header"  => FeatureNames.TagHeader,
            "footer"  => FeatureNames.TagFooter,
            "form"    => FeatureNames.TagForm,
            "table"   => FeatureNames.TagTable,
            "pre"     => FeatureNames.TagPre,
            "div"     => FeatureNames.TagDiv,
            _         => FeatureNames.TagOther,
        };
        dest[slot] = 1f;
    }

    private static void WriteClassHashBuckets(IElement el, Span<float> dest)
    {
        // 8-bucket presence of class tokens. Each token's stable hash maps
        // to one bucket; the bucket slot fires (1.0). The model learns
        // co-occurrence patterns across buckets without us needing to
        // enumerate every theme's class names. Bumping to 32 buckets is
        // a one-line change if recall demands it.
        var classAttr = el.GetAttribute("class");
        if (string.IsNullOrEmpty(classAttr)) return;
        ReadOnlySpan<char> remaining = classAttr.AsSpan();
        while (remaining.Length > 0)
        {
            int spaceIdx = remaining.IndexOf(' ');
            ReadOnlySpan<char> token = spaceIdx < 0 ? remaining : remaining[..spaceIdx];
            if (token.Length > 0)
            {
                int bucket = HashToBucket(token, buckets: 8);
                dest[FeatureNames.ClassBucket0 + bucket] = 1f;
            }
            if (spaceIdx < 0) break;
            remaining = remaining[(spaceIdx + 1)..];
        }
    }

    // FNV-1a-style hash on a ReadOnlySpan<char>. Stable across runs and
    // platforms (we'd hate to silently differ between training and inference).
    // Returns a bucket in [0, buckets).
    private static int HashToBucket(ReadOnlySpan<char> s, int buckets)
    {
        const uint Offset = 2166136261u;
        const uint Prime = 16777619u;
        uint h = Offset;
        for (int i = 0; i < s.Length; i++)
        {
            // Case-fold so "BUTTON" and "button" hash identically. Critical
            // because some sites uppercase utility class names.
            char c = s[i];
            if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
            h ^= c;
            h *= Prime;
        }
        return (int)(h % (uint)buckets);
    }

    private static void WriteDensityAndText(IElement el, Span<float> dest)
    {
        var text = el.TextContent;
        int textLen = text.Length;
        dest[FeatureNames.LogTextLength] = Log1p(textLen);

        int wordCount = 0;
        bool prevSpace = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool isSpace = c == ' ' || c == '\t' || c == '\n' || c == '\r';
            if (!isSpace && prevSpace) wordCount++;
            prevSpace = isSpace;
        }
        dest[FeatureNames.LogWordCount] = Log1p(wordCount);

        int linkTextLen = 0;
        int imgCount = 0;
        int hCount = 0;
        int pCount = 0;
        int liCount = 0;
        int inputCount = 0;
        int buttonCount = 0;
        foreach (var d in el.QuerySelectorAll("*"))
        {
            switch (d.LocalName)
            {
                case "a": linkTextLen += d.TextContent.Length; break;
                case "img": imgCount++; break;
                case "h1": case "h2": case "h3":
                case "h4": case "h5": case "h6": hCount++; break;
                case "p": pCount++; break;
                case "li": liCount++; break;
                case "input": inputCount++; break;
                case "button": buttonCount++; break;
            }
        }
        dest[FeatureNames.LinkDensity] = textLen == 0 ? 0f : (float)linkTextLen / textLen;
        dest[FeatureNames.ImageDensity] = textLen == 0 ? 0f : (float)imgCount / (1 + textLen / 100);
        dest[FeatureNames.LogHeadingCount] = Log1p(hCount);
        dest[FeatureNames.LogParagraphCount] = Log1p(pCount);
        dest[FeatureNames.LogListItemCount] = Log1p(liCount);
        dest[FeatureNames.ParaToHeadingRatio] = hCount == 0 ? Math.Min(pCount, 10f) : (float)pCount / hCount;
        dest[FeatureNames.InputCount] = Math.Min(inputCount, 20f);
        dest[FeatureNames.ButtonCount] = Math.Min(buttonCount, 20f);
    }

    private static void WritePositionFeatures(IElement el, Span<float> dest)
    {
        // Walk to root counting depth.
        int depth = 0;
        IElement? walk = el.ParentElement;
        while (walk is not null) { depth++; walk = walk.ParentElement; }
        dest[FeatureNames.Depth] = Math.Min(depth, 20f);

        var parent = el.ParentElement;
        if (parent is null)
        {
            dest[FeatureNames.PositionFromStart] = 0f;
            dest[FeatureNames.PositionFromEnd] = 0f;
            dest[FeatureNames.ParentChildCount] = 0f;
            dest[FeatureNames.SiblingTextFraction] = 0f;
            return;
        }

        int siblingTotal = parent.ChildElementCount;
        int siblingIndex = 0;
        int parentTextLen = parent.TextContent.Length;
        int siblingTextLen = parent.TextContent.Length - el.TextContent.Length;
        var s = parent.FirstElementChild;
        while (s is not null && !ReferenceEquals(s, el))
        {
            siblingIndex++;
            s = s.NextElementSibling;
        }
        dest[FeatureNames.PositionFromStart] = siblingTotal <= 1 ? 0f : (float)siblingIndex / (siblingTotal - 1);
        dest[FeatureNames.PositionFromEnd]   = siblingTotal <= 1 ? 0f : (float)(siblingTotal - 1 - siblingIndex) / (siblingTotal - 1);
        dest[FeatureNames.ParentChildCount]  = Math.Min(siblingTotal, 50f);
        dest[FeatureNames.SiblingTextFraction] = parentTextLen == 0 ? 0f : (float)siblingTextLen / parentTextLen;
    }

    private static void WriteSiblingShape(IElement el, Span<float> dest)
    {
        var parent = el.ParentElement;
        if (parent is null) return;

        int sameTagSiblings = 0;
        int siblingsWithText = 0;
        var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int largestSiblingText = 0;
        foreach (var s in parent.Children)
        {
            if (ReferenceEquals(s, el)) continue;
            if (s.LocalName == el.LocalName) sameTagSiblings++;
            var stxt = s.TextContent.Length;
            if (stxt > 0) siblingsWithText++;
            if (stxt > largestSiblingText) largestSiblingText = stxt;
            tagCounts.TryGetValue(s.LocalName, out var c);
            tagCounts[s.LocalName] = c + 1;
        }
        dest[FeatureNames.RepeatedSiblingCount] = Math.Min(sameTagSiblings, 20f);
        // Shape score: 1.0 when the candidate sits inside a homogeneous run
        // of N same-tag siblings (repeated-item shape); falls off when it's
        // an outlier in its parent.
        int sib = parent.ChildElementCount;
        dest[FeatureNames.RepeatedShapeScore] = sib <= 1 ? 0f : (float)sameTagSiblings / (sib - 1);
        dest[FeatureNames.SiblingTagEntropy] = ShannonEntropy(tagCounts, sib);
        dest[FeatureNames.IsLargestSiblingByText] = el.TextContent.Length >= largestSiblingText ? 1f : 0f;
        dest[FeatureNames.SiblingsWithDescendantText] = Math.Min(siblingsWithText, 20f);
    }

    private static float ShannonEntropy(Dictionary<string, int> counts, int _)
    {
        // Normalise by the sum of observed counts, not by the parent's child
        // count. The caller excludes the candidate from `counts`, so using
        // ChildElementCount would produce a non-zero entropy on a homogeneous
        // sibling set (every count would underweight by one slot).
        int total = 0;
        foreach (var kv in counts) total += kv.Value;
        if (total <= 1) return 0f;
        double h = 0;
        foreach (var kv in counts)
        {
            if (kv.Value == 0) continue;
            double p = (double)kv.Value / total;
            h += -p * Math.Log2(p);
        }
        return (float)h;
    }

    private static void WriteAncestorPresence(IElement el, Span<float> dest)
    {
        var walk = el.ParentElement;
        while (walk is not null)
        {
            switch (walk.LocalName)
            {
                case "main":    dest[FeatureNames.AncestorMain] = 1f; break;
                case "article": dest[FeatureNames.AncestorArticle] = 1f; break;
                case "nav":     dest[FeatureNames.AncestorNav] = 1f; break;
                case "form":    dest[FeatureNames.AncestorForm] = 1f; break;
                case "aside":   dest[FeatureNames.AncestorAside] = 1f; break;
            }
            walk = walk.ParentElement;
        }
    }

    private static float Log1p(int v) => (float)Math.Log(1 + (double)Math.Max(0, v));
}
