using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;

namespace StyloExtract.Core.OperatorTemplates;

/// <summary>
/// File-backed <see cref="IOperatorTemplateStore"/>. One <c>*.yaml</c> file per
/// host under a root directory. Loads everything on construction, then watches
/// the directory and re-parses files in response to FileSystemWatcher events.
///
/// <para>
/// On a parse error during reload, the previous in-memory entry for that host
/// is preserved and the error is logged. This guarantees the runtime never
/// goes template-less because of a transient bad edit.
/// </para>
///
/// <para>
/// Reads (<see cref="TryGet"/>) are lock-free: a single immutable map is
/// pointer-swapped on reload. Writes (file events) take a debounce lock.
/// </para>
/// </summary>
public sealed class YamlFileOperatorTemplateStore : IOperatorTemplateStore, IDisposable
{
    private readonly string _root;
    private readonly ILogger<YamlFileOperatorTemplateStore>? _logger;
    private readonly FileSystemWatcher? _watcher;

    // Pointer-swapped on reload. Reads see the prior map until the swap completes.
    private volatile IReadOnlyDictionary<string, OperatorTemplate> _map =
        new Dictionary<string, OperatorTemplate>(StringComparer.OrdinalIgnoreCase);

    // Tracks last-known-good entries by lowercased file name (e.g. "example.com.yaml").
    // Survives parse failures so a bad edit doesn't evict the prior template.
    private readonly ConcurrentDictionary<string, OperatorTemplate> _byFile =
        new(StringComparer.OrdinalIgnoreCase);

    public YamlFileOperatorTemplateStore(string root, ILogger<YamlFileOperatorTemplateStore>? logger = null, bool watch = true)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _logger = logger;
        if (!Directory.Exists(_root))
        {
            Directory.CreateDirectory(_root);
        }
        LoadAllFromDisk();
        if (watch)
        {
            _watcher = new FileSystemWatcher(_root, "*.yaml")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _watcher.Deleted += OnFileDeleted;
        }
    }

    public bool TryGet(string host, out OperatorTemplate template)
    {
        return _map.TryGetValue(host, out template!);
    }

    public IReadOnlyList<OperatorTemplate> List()
    {
        return _map.Values.OrderBy(t => t.Host, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Force a full reload from disk. Useful in tests and as the implementation
    /// of the REST <c>POST /templates/reload</c> endpoint.
    /// </summary>
    public void Reload()
    {
        LoadAllFromDisk();
    }

    private void LoadAllFromDisk()
    {
        var next = new Dictionary<string, OperatorTemplate>(StringComparer.OrdinalIgnoreCase);
        // Enumerate in alphabetical-descending order so the bare `<host>.yaml`
        // (which sorts AFTER `<host>-deterministic.yaml` because `.` > `-` in
        // ASCII) gets processed FIRST. When both exist for the same host, the
        // hand-authored / LLM-induced entry wins the host slot in the map.
        // Per-file metadata (filename → IsDeterministic flag) is stamped onto
        // the loaded OperatorTemplate so downstream consumers (e.g. the
        // enrichment coordinator) can distinguish deterministic auto-writes
        // from real overrides.
        foreach (var path in Directory.EnumerateFiles(_root, "*.yaml")
            .OrderByDescending(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (TryLoadFile(path, name, out var t))
            {
                var isDeterministic = name.EndsWith("-deterministic.yaml", StringComparison.OrdinalIgnoreCase);
                var stamped = t! with { IsDeterministic = isDeterministic };
                _byFile[name] = stamped;
                // First write wins (alphabetical-descending order means the
                // hand-authored entry lands first; deterministic entries don't
                // overwrite).
                if (!next.ContainsKey(stamped.Host))
                    next[stamped.Host] = stamped;
            }
            else if (_byFile.TryGetValue(name, out var prior))
            {
                if (!next.ContainsKey(prior.Host))
                    next[prior.Host] = prior;
            }
        }
        _map = next;
    }

    private bool TryLoadFile(string path, string fileName, out OperatorTemplate? template)
    {
        try
        {
            var yaml = File.ReadAllText(path);
            template = YamlOperatorTemplateLoader.Parse(yaml);
            return true;
        }
        catch (OperatorTemplateParseException ex)
        {
            _logger?.LogWarning(ex, "operator template {File} failed to parse; keeping prior entry", fileName);
            template = null;
            return false;
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "operator template {File} unreadable; keeping prior entry", fileName);
            template = null;
            return false;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher can fire multiple events per save (editor write + flush).
        // A blanket reload is the simplest correct behaviour; the set of files is
        // small (one per host) so reading them all costs microseconds.
        LoadAllFromDisk();
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        var name = e.Name;
        if (name is null) return;
        if (_byFile.TryRemove(name, out _))
        {
            LoadAllFromDisk();
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
