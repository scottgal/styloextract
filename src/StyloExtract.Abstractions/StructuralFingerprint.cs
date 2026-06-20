namespace StyloExtract.Abstractions;

public sealed record StructuralFingerprint
{
    public required uint[] StructuralMinHash { get; init; }   // 128 slots
    public required uint[] AnchorMinHash { get; init; }       // 128 slots
    public required ulong[] LshBands { get; init; }           // 16 bands
    public required IReadOnlyDictionary<string, double> PqGramCounts { get; init; }
    public required double PqGramNorm { get; init; }
    public required int ShingleCount { get; init; }
    public required string Hex { get; init; }                  // first-16-bytes hex for display
}
