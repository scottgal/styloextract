namespace StyloExtract.Streaming;

public sealed record StreamingTemplate
{
    public required Guid TemplateId { get; init; }

    /// <summary>
    /// Host the template was induced or registered for. Lookup key for
    /// <see cref="IStreamingTemplateStore.GetByHostAsync"/>. Empty string for
    /// pre-host-keyed templates (e.g. hand-built fences from before alpha.17 or
    /// templates only ever resolved by GUID). Lowercase + canonical form.
    /// </summary>
    public required string Host { get; init; }

    public required TemplateFence PrefixFence { get; init; }
    public required TemplateFence ContentStartFence { get; init; }
    public required TemplateFence ContentEndFence { get; init; }
    public required int MinContentDepth { get; init; }
    public required int BailoutBytes { get; init; }
    public required int MaxCaptureBytes { get; init; }
    public required int WindowSize { get; init; }
    public required int MaxEventsWithoutTransition { get; init; }
}
