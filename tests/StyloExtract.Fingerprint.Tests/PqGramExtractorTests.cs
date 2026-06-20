using FluentAssertions;
using StyloExtract.Fingerprint;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class PqGramExtractorTests
{
    [Fact]
    public void Extract_TwoIdenticalDocs_HighCosine()
    {
        var parser = new AngleSharpHtmlDomParser();
        var pq = new PqGramExtractor(p: 2, q: 3, topK: 256);
        const string html = "<html><body><main><article><h1>x</h1><p>y</p><p>z</p></article></main></body></html>";

        var (ca, na) = pq.Extract(parser.Parse(html));
        var (cb, nb) = pq.Extract(parser.Parse(html));

        Cosine(ca, na, cb, nb).Should().BeGreaterThan(0.99);
    }

    [Fact]
    public void Extract_StructurallyDifferentDocs_LowCosine()
    {
        var parser = new AngleSharpHtmlDomParser();
        var pq = new PqGramExtractor(p: 2, q: 3, topK: 256);
        const string a = "<html><body><nav><a>x</a><a>y</a></nav></body></html>";
        const string b = "<html><body><table><tr><td>1</td><td>2</td></tr></table></body></html>";

        var (ca, na) = pq.Extract(parser.Parse(a));
        var (cb, nb) = pq.Extract(parser.Parse(b));

        Cosine(ca, na, cb, nb).Should().BeLessThan(0.3);
    }

    private static double Cosine(IReadOnlyDictionary<string, double> ca, double na, IReadOnlyDictionary<string, double> cb, double nb)
    {
        if (na == 0 || nb == 0) return 0;
        double dot = 0;
        foreach (var kv in ca)
        {
            if (cb.TryGetValue(kv.Key, out var v)) dot += kv.Value * v;
        }
        return dot / (na * nb);
    }
}
