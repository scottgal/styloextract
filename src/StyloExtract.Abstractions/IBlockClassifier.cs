using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IBlockClassifier
{
    IReadOnlyList<ExtractedBlock> Classify(IReadOnlyList<IElement> blocks);
}
