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
    public required int BailoutBytes { get; init; }
    public required int MaxCaptureBytes { get; init; }
    public required int WindowSize { get; init; }
    /// <summary>
    /// alpha.23: deprecated. The scanner now uses bytes-since-state-change
    /// against <see cref="BailoutBytes"/> instead of an event counter (the
    /// alpha.21 structural-tag filter throttled events too aggressively for
    /// this field to be reliable on real pages). Kept on the record so
    /// alpha.16–alpha.22 persisted templates still deserialise; new
    /// templates can set any value.
    /// </summary>
    public required int MaxEventsWithoutTransition { get; init; }

    /// <summary>
    /// Monotonically increasing version for this host's streaming template.
    /// Starts at 1 on initial induction; <see cref="StreamingRefitOrchestrator"/>
    /// bumps it whenever drift triggers a re-induction. Defaults to 1 so
    /// pre-alpha.18 templates loaded from persistence read as version 1.
    /// </summary>
    public int Version { get; init; } = 1;
}
