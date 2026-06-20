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
        var ancestorStem = new Queue<string>(_p);
        for (int i = 0; i < _p; i++) ancestorStem.Enqueue(NullLabel);
        Walk(document.Body, ancestorStem, counts);

        // Truncate to topK by count, then normalise.
        var top = counts.OrderByDescending(kv => kv.Value).Take(_topK).ToDictionary(kv => kv.Key, kv => (double)kv.Value);
        var norm = Math.Sqrt(top.Values.Sum(v => v * v));
        return (top, norm);
    }

    private void Walk(IElement element, Queue<string> ancestorStem, Dictionary<string, int> counts)
    {
        var label = element.TagName.ToLowerInvariant();
        var nextStem = new Queue<string>(ancestorStem);
        nextStem.Dequeue();
        nextStem.Enqueue(label);

        // Emit pq-grams over this node's children with the _q sliding window of children.
        var children = element.Children;
        var siblingWindow = new Queue<string>(_q);
        for (int i = 0; i < _q; i++) siblingWindow.Enqueue(NullLabel);

        foreach (var child in children)
        {
            siblingWindow.Dequeue();
            siblingWindow.Enqueue(child.TagName.ToLowerInvariant());
            EmitPqGram(nextStem, siblingWindow, counts);
        }
        // Flush final window with trailing nulls.
        for (int i = 0; i < _q - 1; i++)
        {
            siblingWindow.Dequeue();
            siblingWindow.Enqueue(NullLabel);
            EmitPqGram(nextStem, siblingWindow, counts);
        }

        foreach (var child in children)
        {
            Walk(child, nextStem, counts);
        }
    }

    private static void EmitPqGram(Queue<string> stem, Queue<string> window, Dictionary<string, int> counts)
    {
        var key = string.Join(",", stem.Concat(window));
        counts[key] = counts.GetValueOrDefault(key, 0) + 1;
    }
}
