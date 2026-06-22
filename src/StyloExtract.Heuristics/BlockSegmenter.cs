using System.Text.Json;
using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class BlockSegmenter : IBlockSegmenter
{
    // Semantic HTML5 block tags always promoted to segmentation candidates. Data lives
    // in Definitions/block-segmenter-tags.json so adding tags does not require a recompile.
    private static readonly HashSet<string> SemanticTags = LoadSemanticTags();

    private static HashSet<string> LoadSemanticTags()
    {
        var assembly = typeof(BlockSegmenter).Assembly;
        var resName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("block-segmenter-tags.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resName)!;
        var dto = JsonSerializer.Deserialize(stream, HeuristicsJsonContext.Default.HintList)!;
        return new HashSet<string>(dto.Hints, StringComparer.OrdinalIgnoreCase);
    }

    private const int BlockyDivMinTextLength = 80;
    private const int BlockyDivMinChildCount = 3;

    public IReadOnlyList<IElement> Segment(IDocument document)
    {
        if (document.Body is null) return Array.Empty<IElement>();
        var result = new List<IElement>();
        Walk(document.Body, result);
        return result;
    }

    private static void Walk(IElement element, List<IElement> sink)
    {
        if (SemanticTags.Contains(element.TagName))
        {
            sink.Add(element);
        }
        else if (IsBlockyDiv(element))
        {
            sink.Add(element);
        }
        foreach (var child in element.Children)
        {
            Walk(child, sink);
        }
    }

    private static bool IsBlockyDiv(IElement element)
    {
        if (!string.Equals(element.TagName, "div", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(element.TagName, "section", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return element.TextContent.Length >= BlockyDivMinTextLength
               || element.ChildElementCount >= BlockyDivMinChildCount;
    }
}
