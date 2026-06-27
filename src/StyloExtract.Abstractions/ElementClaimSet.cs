namespace StyloExtract.Abstractions;

/// <summary>
/// Full identity-attribute snapshot of an element. Built by the extractor;
/// consumed by the matcher (and by the corpus miner — Phase 2).
/// </summary>
public sealed record ElementClaimSet
{
    public required string Tag { get; init; }
    public required ulong TagHash { get; init; }
    public string? Id { get; init; }
    public ulong? IdHash { get; init; }
    public IReadOnlyList<string> Classes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ulong> ClassHashes { get; init; } = Array.Empty<ulong>();
    public IReadOnlyDictionary<string, string> DataAttrs { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> AriaAttrs { get; init; } =
        new Dictionary<string, string>();
    public string? Role { get; init; }
}
