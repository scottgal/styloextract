using System.Collections.Concurrent;

namespace StyloExtract.Streaming;

public sealed class InMemoryStreamingTemplateStore : IStreamingTemplateStore
{
    private readonly ConcurrentDictionary<Guid, StreamingTemplate> _templates = new();

    public StreamingTemplate? Get(Guid templateId) =>
        _templates.TryGetValue(templateId, out var t) ? t : null;

    public void Register(StreamingTemplate template) =>
        _templates[template.TemplateId] = template;
}
