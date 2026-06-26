using System.Collections.Concurrent;

namespace StyloExtract.Streaming;

public sealed class InMemoryStreamingTemplateStore : IStreamingTemplateStore
{
    private readonly ConcurrentDictionary<Guid, StreamingTemplate> _templates = new();

    public StreamingTemplate? TryGetHot(Guid templateId) =>
        _templates.TryGetValue(templateId, out var t) ? t : null;

    public ValueTask<StreamingTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
        new(TryGetHot(templateId));

    public ValueTask RegisterAsync(StreamingTemplate template, CancellationToken cancellationToken = default)
    {
        _templates[template.TemplateId] = template;
        return ValueTask.CompletedTask;
    }
}
