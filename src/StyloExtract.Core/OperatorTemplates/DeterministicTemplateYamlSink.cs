using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;

namespace StyloExtract.Core.OperatorTemplates;

/// <summary>
/// Best-effort persistence of heuristic-induced templates to YAML files alongside the
/// LLM-induced templates the <c>TemplateEnrichmentCoordinator</c> writes. Each freshly
/// induced <see cref="LearnedExtractor"/> is rendered through the canonical
/// <see cref="OperatorTemplateYamlEmitter"/> and saved as
/// <c>&lt;host&gt;-deterministic.yaml</c> under the configured root directory.
///
/// <para>
/// Filenames are suffixed <c>-deterministic</c> so they coexist with LLM-induced
/// <c>&lt;host&gt;.yaml</c> files. The SQLite store remains the authoritative source
/// for match-time selection; the YAML files exist purely for auditing, diffing, and
/// hand-editing — operators can see what the deterministic inducer chose without
/// dumping the SQLite extractor_blob column.
/// </para>
///
/// <para>
/// All writes are wrapped in try/catch and never throw out of <see cref="Persist"/>;
/// a failed write produces a log warning but does not block the extraction pipeline.
/// The sink is registered as an optional singleton consumed by
/// <see cref="LayoutExtractor"/>; when not registered, no YAML files are written.
/// </para>
/// </summary>
public sealed class DeterministicTemplateYamlSink
{
    private readonly string _root;
    private readonly ILogger<DeterministicTemplateYamlSink>? _logger;

    public DeterministicTemplateYamlSink(string root, ILogger<DeterministicTemplateYamlSink>? logger = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _logger = logger;
    }

    /// <summary>
    /// Render <paramref name="extractor"/> to YAML and save to
    /// <c>{root}/{host}-deterministic.yaml</c>. Swallows IO/permission failures
    /// and logs a warning. Returns the file path when the write succeeded, or
    /// <c>null</c> on any failure (including a blank host).
    /// </summary>
    public string? Persist(string host, LearnedExtractor extractor)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (extractor.Rules.Count == 0) return null;
        try
        {
            Directory.CreateDirectory(_root);
            var safeHost = SanitizeHost(host);
            var path = Path.Combine(_root, safeHost + "-deterministic.yaml");
            var template = new OperatorTemplate
            {
                Host = host,
                Description = $"Deterministic heuristic-induced template, version {extractor.Version}, " +
                              $"{extractor.Rules.Count} rule(s), captured {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z.",
                Version = extractor.Version,
                Rules = extractor.Rules.Select(r => new OperatorTemplateRule
                {
                    Role = r.Role,
                    Selectors = r.CssSelectors,
                    Confidence = r.MeanConfidence,
                }).ToList(),
            };
            var yaml = OperatorTemplateYamlEmitter.Emit(template);
            File.WriteAllText(path, yaml);
            _logger?.LogDebug(
                "deterministic template YAML written for {Host}: {RuleCount} rule(s) to {Path}",
                host, template.Rules.Count, path);
            return path;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "failed to write deterministic template YAML for {Host} under {Root}; SQLite write is unaffected",
                host, _root);
            return null;
        }
    }

    /// <summary>
    /// Strip path separators and reserved characters from a host so it's safe to
    /// embed in a filename. Hosts the wild can include port suffixes (<c>:8080</c>)
    /// or — on test fixtures — synthetic prefixes (<c>file:abc</c>).
    /// </summary>
    private static string SanitizeHost(string host)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = host.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
