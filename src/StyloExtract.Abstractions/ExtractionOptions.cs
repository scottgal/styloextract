namespace StyloExtract.Abstractions;

public sealed record ExtractionOptions
{
    public ExtractionProfile Profile { get; init; } = ExtractionProfile.RagFull;
    public bool LearnNewTemplates { get; init; } = true;
    public bool EmitDebugMetadata { get; init; }
    public string? HostOverride { get; init; }
}
