using System.Collections.Concurrent;

namespace StyloExtract.Streaming;

public sealed class InMemoryStreamingTemplateStore : IStreamingTemplateStore
{
    private readonly ConcurrentDictionary<Guid, StreamingTemplate> _templates = new();
    // alpha.21: per-host version chain. The SortedDictionary maps version → template.
    private readonly ConcurrentDictionary<string, SortedDictionary<int, StreamingTemplate>> _versionsByHost =
        new(StringComparer.OrdinalIgnoreCase);

    public StreamingTemplate? TryGetHot(Guid templateId) =>
        _templates.TryGetValue(templateId, out var t) ? t : null;

    public ValueTask<StreamingTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
        new(TryGetHot(templateId));

    public ValueTask RegisterAsync(StreamingTemplate template, CancellationToken cancellationToken = default) =>
        UpsertAsync(template, cancellationToken);

    public StreamingTemplate? TryGetHotByHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        if (!_versionsByHost.TryGetValue(host, out var versions)) return null;
        lock (versions)
        {
            if (versions.Count == 0) return null;
            // Last key = highest version = latest.
            var latestVersion = versions.Keys.Max();
            return versions[latestVersion];
        }
    }

    public ValueTask<StreamingTemplate?> GetByHostAsync(string host, CancellationToken cancellationToken = default) =>
        new(TryGetHotByHost(host));

    public ValueTask UpsertAsync(StreamingTemplate template, CancellationToken cancellationToken = default)
    {
        _templates[template.TemplateId] = template;
        if (!string.IsNullOrEmpty(template.Host))
        {
            var versions = _versionsByHost.GetOrAdd(template.Host, _ => new SortedDictionary<int, StreamingTemplate>());
            lock (versions)
            {
                versions[template.Version] = template;
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<StreamingTemplate?> GetByHostAtVersionAsync(
        string host,
        int version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(host)) return new((StreamingTemplate?)null);
        if (!_versionsByHost.TryGetValue(host, out var versions)) return new((StreamingTemplate?)null);
        lock (versions)
        {
            return new(versions.TryGetValue(version, out var t) ? t : null);
        }
    }

    public ValueTask<IReadOnlyList<int>> ListVersionsByHostAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(host)) return new((IReadOnlyList<int>)Array.Empty<int>());
        if (!_versionsByHost.TryGetValue(host, out var versions)) return new((IReadOnlyList<int>)Array.Empty<int>());
        lock (versions)
        {
            return new((IReadOnlyList<int>)versions.Keys.ToArray());
        }
    }
}
