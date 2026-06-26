namespace StyloExtract.Streaming;

/// <summary>
/// Sink for streaming-template lifecycle events. Consumers wire this to
/// surface refit / version-bump activity in UI telemetry. The shape is
/// intentionally minimal compared to <c>ITemplateVersionEventSink</c> in
/// <c>StyloExtract.Templates</c> — streaming templates have no PqGram /
/// rule-set / selector concepts, so the diff carries only what's meaningful
/// for the streaming path (host + old/new version + reason).
///
/// Implementations must be cheap and non-blocking — they fire from the
/// off-hot-path observer queue, but back-pressure here would still stall
/// new observations.
/// </summary>
public interface IStreamingTemplateVersionSink
{
    ValueTask OnRefittedAsync(StreamingTemplateRefitEvent evt, CancellationToken cancellationToken);
}

public sealed record StreamingTemplateRefitEvent(
    string Host,
    Guid OldTemplateId,
    Guid NewTemplateId,
    int OldVersion,
    int NewVersion,
    string Reason,
    DateTimeOffset DetectedAt);

public sealed class NoopStreamingTemplateVersionSink : IStreamingTemplateVersionSink
{
    public ValueTask OnRefittedAsync(StreamingTemplateRefitEvent evt, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
