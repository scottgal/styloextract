using StyloExtract.Abstractions;

namespace StyloExtract.Templates;

public sealed class DefaultNoopVersionEventSink : ITemplateVersionEventSink
{
    public ValueTask OnNewTemplateAsync(NewTemplateEvent evt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
