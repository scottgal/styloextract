namespace StyloExtract.Abstractions;

public sealed record VersionChangeEvent
{
    public required Guid TemplateId { get; init; }
    public required string HostDisplayName { get; init; }
    public required int OldVersion { get; init; }
    public required int NewVersion { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required TemplateVersionDiff Diff { get; init; }
}
