using System.Collections.Concurrent;

namespace StyloExtract.Streaming;

public sealed class InMemoryStreamingTemplateStore : IStreamingTemplateStore
{
    private readonly ConcurrentDictionary<Guid, StreamingTemplate> _templates = new();
    private readonly ConcurrentDictionary<string, Guid> _hostIndex =
        new(StringComparer.OrdinalIgnoreCase);

    public StreamingTemplate? TryGetHot(Guid templateId) =>
        _templates.TryGetValue(templateId, out var t) ? t : null;

    public ValueTask<StreamingTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
        new(TryGetHot(templateId));

    public ValueTask RegisterAsync(StreamingTemplate template, CancellationToken cancellationToken = default)
    {
        _templates[template.TemplateId] = template;
        if (!string.IsNullOrEmpty(template.Host))
            _hostIndex[template.Host] = template.TemplateId;
        return ValueTask.CompletedTask;
    }

    public StreamingTemplate? TryGetHotByHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        return _hostIndex.TryGetValue(host, out var id) && _templates.TryGetValue(id, out var t)
            ? t
            : null;
    }

    public ValueTask<StreamingTemplate?> GetByHostAsync(string host, CancellationToken cancellationToken = default) =>
        new(TryGetHotByHost(host));

    public ValueTask UpsertAsync(StreamingTemplate template, CancellationToken cancellationToken = default) =>
        RegisterAsync(template, cancellationToken);
}
