namespace StyloExtract.Abstractions;

public sealed record NewTemplateEvent
{
    public required Guid TemplateId { get; init; }
    public required string HostDisplayName { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required string FingerprintHex { get; init; }
    public required int InitialBlockCount { get; init; }
}
