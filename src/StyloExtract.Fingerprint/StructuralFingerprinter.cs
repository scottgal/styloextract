using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Fingerprint;

public sealed class StructuralFingerprinter : IStructuralFingerprinter
{
    private readonly ShingleGenerator _shingles;
    private readonly MinHashSketcher _sketcher;
    private readonly LshBander _bander;
    private readonly AnchorPathFingerprinter _anchorSig;
    private readonly PqGramExtractor _pqGram;

    public StructuralFingerprinter(
        ShingleGenerator shingles,
        MinHashSketcher sketcher,
        LshBander bander,
        AnchorPathFingerprinter anchorSig,
        PqGramExtractor pqGram)
    {
        _shingles = shingles;
        _sketcher = sketcher;
        _bander = bander;
        _anchorSig = anchorSig;
        _pqGram = pqGram;
    }

    public StructuralFingerprint Compute(IDocument document)
    {
        var shingleList = _shingles.Generate(document);
        var structural = _sketcher.Sketch(shingleList);
        var bands = _bander.BandHashes(structural);
        var anchor = _anchorSig.Sketch(document);
        var (pq, norm) = _pqGram.Extract(document);
        var hex = ToHex(structural);
        return new StructuralFingerprint
        {
            StructuralMinHash = structural,
            AnchorMinHash = anchor,
            LshBands = bands,
            PqGramCounts = pq,
            PqGramNorm = norm,
            ShingleCount = shingleList.Count,
            Hex = hex
        };
    }

    private static string ToHex(uint[] sig)
    {
        var bytes = new byte[Math.Min(16, sig.Length * 4)];
        for (int i = 0; i < Math.Min(4, sig.Length); i++)
        {
            BitConverter.GetBytes(sig[i]).CopyTo(bytes, i * 4);
        }
        return Convert.ToHexString(bytes);
    }
}
