using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IStructuralFingerprinter
{
    StructuralFingerprint Compute(IDocument document);
}
