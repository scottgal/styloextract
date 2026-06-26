using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;
using StyloExtract.Core.OperatorTemplates;

namespace StyloExtract.Core;

/// <summary>
/// DI helpers for wiring operator-template overrides into the extraction
/// pipeline. Calling <see cref="AddStyloExtractOperatorTemplates"/> registers
/// a YAML-file-backed store keyed on the configured root directory. The
/// <see cref="LayoutExtractor"/> resolves <see cref="IOperatorTemplateStore"/>
/// from DI as an optional dependency and consults it before every
/// fingerprint when the registration is present.
/// </summary>
public static class StyloExtractOperatorTemplatesExtensions
{
    /// <summary>
    /// Register a <see cref="YamlFileOperatorTemplateStore"/> rooted at
    /// <paramref name="root"/>. The directory is created if it doesn't exist;
    /// every <c>*.yaml</c> file under it is loaded on startup and the directory
    /// is watched for changes thereafter (a parse failure on reload preserves
    /// the prior in-memory entry).
    /// </summary>
    public static IServiceCollection AddStyloExtractOperatorTemplates(
        this IServiceCollection services,
        string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root must be non-empty", nameof(root));
        services.TryAddSingleton<IOperatorTemplateStore>(sp =>
            new YamlFileOperatorTemplateStore(root, sp.GetService<ILogger<YamlFileOperatorTemplateStore>>()));
        // Also register the deterministic-template YAML sink, sharing the same
        // root. LayoutExtractor consumes it as an optional dependency and writes
        // <host>-deterministic.yaml alongside the LLM-induced <host>.yaml files
        // every time the heuristic inducer fires. Best-effort; failures don't
        // affect the SQLite write that is the source of truth at match time.
        services.TryAddSingleton(sp =>
            new DeterministicTemplateYamlSink(root, sp.GetService<ILogger<DeterministicTemplateYamlSink>>()));
        return services;
    }
}
