using System.Text;
using AngleSharp.Dom;

namespace StyloExtract.Fingerprint;

public sealed class PqGramExtractor
{
    private readonly int _p;
    private readonly int _q;
    private readonly int _topK;
    private const string NullLabel = "*";

    public PqGramExtractor(int p = 2, int q = 3, int topK = 256)
    {
        _p = p;
        _q = q;
        _topK = topK;
    }

    public (IReadOnlyDictionary<string, double> Counts, double Norm) Extract(IDocument document)
    {
        if (document.Body is null)
        {
            return (new Dictionary<string, double>(), 0);
        }
        var counts = new Dictionary<string, int>();
        // The stem ring buffer is mutated as we descend (push label at write-head, advance)
        // and rolled back on ascent. No per-call queue clones. _stemHead is the index of
        // the OLDEST entry; entries are read from oldest to newest in EmitPqGram.
        var stem = new string[_p];
        for (int i = 0; i < _p; i++) stem[i] = NullLabel;
        // Sibling window also fixed-size. Reused across nodes; reset on entering each parent.
        var window = new string[_q];
        // StringBuilder is reused across every EmitPqGram emit. Final ToString() per emit
        // is the only unavoidable allocation because the dictionary keys are strings (the
        // template-store serialisation format requires it; switching to long-hash keys
        // would break on-disk fingerprint compatibility).
        var sb = new StringBuilder(_p * 8 + _q * 8 + (_p + _q));
        Walk(document.Body, stem, stemHead: 0, window, counts, sb);

        // Truncate to topK by count, then L2-normalise. Use partial sort via OrderByDescending
        // because counts.Count is small (~hundreds), so the LINQ-heavy path is negligible
        // versus the walk above and changing this would buy nothing.
        var top = counts.OrderByDescending(kv => kv.Value).Take(_topK).ToDictionary(kv => kv.Key, kv => (double)kv.Value);
        var norm = Math.Sqrt(top.Values.Sum(v => v * v));
        return (top, norm);
    }

    // Walk pushes the current element's tag into the stem ring at stemHead, recurses through
    // children, then rewinds the stem. The window array is local to this call (reset per
    // parent because siblings of different parents share no window state).
    private void Walk(IElement element, string[] stem, int stemHead, string[] window,
        Dictionary<string, int> counts, StringBuilder sb)
    {
        var label = element.LocalName;
        // Push label at stemHead (which is the index of the OLDEST entry — overwriting it
        // is equivalent to Dequeue + Enqueue), then advance stemHead by one so the next
        // recursion writes into the next slot.
        var savedAtHead = stem[stemHead];
        stem[stemHead] = label;
        var nextHead = (stemHead + 1) % _p;

        // Initialise the sibling window with nulls per parent — windows do not carry across
        // siblings of different parents.
        for (int i = 0; i < _q; i++) window[i] = NullLabel;
        int windowHead = 0;

        // Emit pq-grams over this node's children with the _q-wide sliding window.
        foreach (var child in element.Children)
        {
            window[windowHead] = child.LocalName;
            windowHead = (windowHead + 1) % _q;
            EmitPqGram(stem, nextHead, window, windowHead, counts, sb);
        }
        // Flush final window with trailing nulls so the last (_q - 1) child positions are
        // recorded against the (_q - 1) imaginary trailing siblings.
        for (int i = 0; i < _q - 1; i++)
        {
            window[windowHead] = NullLabel;
            windowHead = (windowHead + 1) % _q;
            EmitPqGram(stem, nextHead, window, windowHead, counts, sb);
        }

        foreach (var child in element.Children)
        {
            Walk(child, stem, nextHead, window, counts, sb);
        }

        // Rewind: restore the slot we overwrote so the caller's stem view is unchanged.
        stem[stemHead] = savedAtHead;
    }

    private static void EmitPqGram(string[] stem, int stemHead, string[] window, int windowHead,
        Dictionary<string, int> counts, StringBuilder sb)
    {
        // Write stem oldest-to-newest then window oldest-to-newest, joined by commas.
        // Both arrays are ring buffers — start reading at the head (oldest entry).
        sb.Clear();
        for (int i = 0; i < stem.Length; i++)
        {
            sb.Append(stem[(stemHead + i) % stem.Length]);
            sb.Append(',');
        }
        for (int i = 0; i < window.Length; i++)
        {
            sb.Append(window[(windowHead + i) % window.Length]);
            if (i < window.Length - 1) sb.Append(',');
        }
        var key = sb.ToString();
        counts[key] = counts.GetValueOrDefault(key, 0) + 1;
    }
}
