namespace StyloExtract.Streaming;

public sealed record StreamingTemplate
{
    public required Guid TemplateId { get; init; }
    public required TemplateFence PrefixFence { get; init; }
    public required TemplateFence ContentStartFence { get; init; }
    public required TemplateFence ContentEndFence { get; init; }
    public required int MinContentDepth { get; init; }
    public required int BailoutBytes { get; init; }
    public required int MaxCaptureBytes { get; init; }
    public required int WindowSize { get; init; }
}
