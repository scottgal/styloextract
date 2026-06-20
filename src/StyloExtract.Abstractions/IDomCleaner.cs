using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IDomCleaner
{
    void Clean(IDocument document);
}
