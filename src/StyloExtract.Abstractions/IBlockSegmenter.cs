using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IBlockSegmenter
{
    IReadOnlyList<IElement> Segment(IDocument document);
}
