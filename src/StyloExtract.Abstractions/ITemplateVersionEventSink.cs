namespace StyloExtract.Abstractions;

public interface ITemplateVersionEventSink
{
    ValueTask OnNewTemplateAsync(NewTemplateEvent evt, CancellationToken cancellationToken);
    ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken cancellationToken);
}
