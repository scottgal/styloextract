using System.IO.Hashing;
using System.Text;
using AngleSharp.Dom;
using StyloExtract.Heuristics;

namespace StyloExtract.Fingerprint;

public sealed class ShingleGenerator
{
    private readonly ClassNoiseFilter _classNoise;
    private readonly int _shingleWidth;

    public ShingleGenerator(ClassNoiseFilter classNoise, int shingleWidth = 3)
    {
        _classNoise = classNoise;
        _shingleWidth = shingleWidth;
    }

    public IReadOnlyList<ulong> Generate(IDocument document)
    {
        if (document.Body is null) return Array.Empty<ulong>();
        var nodeHashes = new List<ulong>(256);
        Walk(document.Body, ancestorPathHash: 0, nodeHashes);
        return CombineIntoShingles(nodeHashes);
    }

    private void Walk(IElement element, ulong ancestorPathHash, List<ulong> sink)
    {
        var tag = element.TagName.ToLowerInvariant();
        var nthBucket = BucketSiblingIndex(element);
        var rawClasses = (element.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = _classNoise.Filter(rawClasses);
        var classHash = HashClassTokens(filtered);
        var nodeHash = HashTuple(tag, nthBucket, classHash, ancestorPathHash);
        sink.Add(nodeHash);

        var nextAncestor = XxHash64.HashToUInt64(BitConverter.GetBytes(ancestorPathHash).Concat(Encoding.UTF8.GetBytes(tag)).ToArray());
        foreach (var child in element.Children)
        {
            Walk(child, nextAncestor, sink);
        }
    }

    private IReadOnlyList<ulong> CombineIntoShingles(List<ulong> nodeHashes)
    {
        if (nodeHashes.Count < _shingleWidth) return nodeHashes;
        var result = new List<ulong>(nodeHashes.Count - _shingleWidth + 1);
        var buf = new byte[8 * _shingleWidth];
        for (int i = 0; i <= nodeHashes.Count - _shingleWidth; i++)
        {
            for (int j = 0; j < _shingleWidth; j++)
            {
                BitConverter.GetBytes(nodeHashes[i + j]).CopyTo(buf, j * 8);
            }
            result.Add(XxHash64.HashToUInt64(buf));
        }
        return result;
    }

    private static int BucketSiblingIndex(IElement element)
    {
        int idx = 1;
        var prev = element.PreviousElementSibling;
        while (prev is not null)
        {
            if (string.Equals(prev.TagName, element.TagName, StringComparison.OrdinalIgnoreCase)) idx++;
            prev = prev.PreviousElementSibling;
        }
        return idx <= 3 ? idx : 4; // 4 = "many"
    }

    private static ulong HashClassTokens(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return 0UL;
        var sorted = tokens.OrderBy(t => t, StringComparer.Ordinal);
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(string.Join(",", sorted)));
    }

    private static ulong HashTuple(string tag, int nthBucket, ulong classHash, ulong ancestorPathHash)
    {
        Span<byte> buf = stackalloc byte[8 + 4 + 8 + 64];
        BitConverter.TryWriteBytes(buf, classHash);
        BitConverter.TryWriteBytes(buf[8..], nthBucket);
        BitConverter.TryWriteBytes(buf[12..], ancestorPathHash);
        var written = 20 + Encoding.UTF8.GetBytes(tag, buf[20..]);
        return XxHash64.HashToUInt64(buf[..written]);
    }
}
