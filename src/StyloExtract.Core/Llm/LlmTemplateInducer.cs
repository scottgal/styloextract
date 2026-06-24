using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Core.Skeleton;

namespace StyloExtract.Core.Llm;

/// <summary>
/// Composes <see cref="DomSkeletonRenderer"/> + <see cref="LlmInducerPrompts"/>
/// + an <see cref="ILlmTextProvider"/> + <see cref="YamlOperatorTemplateLoader"/>
/// into a single "induce a template for this page" operation.
///
/// <para>
/// Slow-path only. Always called from a background coordinator
/// (<c>TemplateEnrichmentCoordinator</c>, phase 3b) so the request hot
/// path never blocks on an LLM call. Per-call latency budgets in the
/// tens of seconds are acceptable here.
/// </para>
///
/// <para>
/// Failure modes (LLM error, malformed response, YAML parse failure,
/// host-name mismatch) return <c>null</c> rather than throwing. The
/// coordinator treats <c>null</c> as "keep the heuristic-induced
/// template" and logs the reason. Throwing would propagate to the
/// HostedService loop and need a top-level handler anyway.
/// </para>
/// </summary>
public sealed class LlmTemplateInducer
{
    private readonly ILlmTextProvider _llm;
    private readonly DomSkeletonRenderer _skeleton;
    private readonly ILogger<LlmTemplateInducer>? _logger;

    public LlmTemplateInducer(
        ILlmTextProvider llm,
        DomSkeletonRenderer? skeleton = null,
        ILogger<LlmTemplateInducer>? logger = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _skeleton = skeleton ?? new DomSkeletonRenderer();
        _logger = logger;
    }

    /// <summary>
    /// Render the skeleton for <paramref name="document"/>, prompt the
    /// LLM, parse the YAML reply into an <see cref="OperatorTemplate"/>
    /// keyed on <paramref name="host"/>. Returns <c>null</c> on any
    /// failure; the caller logs and falls back to the heuristic-induced
    /// template.
    /// </summary>
    public Task<OperatorTemplate?> InduceAsync(
        IDocument document,
        string host,
        CancellationToken cancellationToken = default)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host required", nameof(host));

        var skeleton = _skeleton.Render(document);
        if (string.IsNullOrEmpty(skeleton))
        {
            _logger?.LogDebug("induce skipped for {Host}: empty skeleton", host);
            return Task.FromResult<OperatorTemplate?>(null);
        }
        return InduceFromSkeletonAsync(skeleton, host, cancellationToken);
    }

    /// <summary>
    /// Variant for callers (the background coordinator) that already have
    /// a rendered skeleton in hand. Skips the renderer call entirely.
    /// </summary>
    public async Task<OperatorTemplate?> InduceFromSkeletonAsync(
        string skeleton,
        string host,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(skeleton)) throw new ArgumentException("skeleton required", nameof(skeleton));
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host required", nameof(host));

        var userPrompt = LlmInducerPrompts.BuildUserPrompt(host, skeleton);
        string response;
        try
        {
            response = await _llm.CompleteAsync(
                LlmInducerPrompts.System, userPrompt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LlmProviderException ex)
        {
            _logger?.LogWarning(ex, "induce backend failed for {Host}", host);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "induce backend threw unexpected exception for {Host}", host);
            return null;
        }

        var yaml = ExtractYamlBlock(response);
        if (yaml is null)
        {
            _logger?.LogWarning(
                "induce response for {Host} contained no fenced YAML block; head=\"{Head}\"",
                host, Snip(response, 200));
            return null;
        }

        OperatorTemplate parsed;
        try
        {
            parsed = YamlOperatorTemplateLoader.Parse(yaml);
        }
        catch (OperatorTemplateParseException ex)
        {
            _logger?.LogWarning(ex,
                "induce for {Host} produced YAML that failed validation; yaml=\"{Yaml}\"",
                host, Snip(yaml, 400));
            return null;
        }

        // Defensive: if the model used a different host than we asked for,
        // rewrite to the caller's host (and log). Don't reject — the rules
        // are still useful for OUR host, the model just hallucinated the
        // host field. Operator-template hard-override path keys on host
        // alone, so getting this right matters.
        if (!string.Equals(parsed.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation(
                "induce for {Host} rewrote model-supplied host \"{ModelHost}\"",
                host, parsed.Host);
            parsed = parsed with { Host = host };
        }

        return parsed;
    }

    /// <summary>
    /// Repair an existing template that produced poor output for a page. Send
    /// the LLM the page skeleton + the failing template's YAML + (optionally)
    /// a short sample of its bad output, and get back a corrected template.
    /// Same failure semantics as <see cref="InduceFromSkeletonAsync"/>: returns
    /// <c>null</c> on any failure; caller falls back to the existing template.
    /// </summary>
    public async Task<OperatorTemplate?> RepairFromSkeletonAsync(
        string skeleton,
        string host,
        string existingTemplateYaml,
        string? badMarkdownSample = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(skeleton)) throw new ArgumentException("skeleton required", nameof(skeleton));
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host required", nameof(host));
        if (string.IsNullOrWhiteSpace(existingTemplateYaml))
            throw new ArgumentException("existing template required", nameof(existingTemplateYaml));

        var userPrompt = LlmInducerPrompts.BuildRepairPrompt(
            host, skeleton, existingTemplateYaml, badMarkdownSample ?? string.Empty);
        string response;
        try
        {
            response = await _llm.CompleteAsync(
                LlmInducerPrompts.SystemRepair, userPrompt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LlmProviderException ex)
        {
            _logger?.LogWarning(ex, "repair backend failed for {Host}", host);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "repair backend threw unexpected exception for {Host}", host);
            return null;
        }

        var yaml = ExtractYamlBlock(response);
        if (yaml is null)
        {
            _logger?.LogWarning(
                "repair response for {Host} contained no fenced YAML block; head=\"{Head}\"",
                host, Snip(response, 200));
            return null;
        }

        OperatorTemplate parsed;
        try
        {
            parsed = YamlOperatorTemplateLoader.Parse(yaml);
        }
        catch (OperatorTemplateParseException ex)
        {
            _logger?.LogWarning(ex,
                "repair for {Host} produced YAML that failed validation; yaml=\"{Yaml}\"",
                host, Snip(yaml, 400));
            return null;
        }

        if (!string.Equals(parsed.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation(
                "repair for {Host} rewrote model-supplied host \"{ModelHost}\"",
                host, parsed.Host);
            parsed = parsed with { Host = host };
        }

        return parsed;
    }

    /// <summary>
    /// Extract the YAML body from a fenced code block. The model is
    /// instructed to wrap its reply in a single ```yaml ... ``` fence;
    /// if it complies, return the body. Otherwise try to recover by
    /// looking for "host:" anywhere in the response. Returns null if
    /// neither attempt finds anything plausible. Public so the CLI
    /// `template induce` command can use it on inspected output, and
    /// for direct testing.
    /// </summary>
    public static string? ExtractYamlBlock(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        // Look for the canonical fenced block first.
        int fenceStart = response.IndexOf("```yaml", StringComparison.OrdinalIgnoreCase);
        if (fenceStart < 0) fenceStart = response.IndexOf("```yml", StringComparison.OrdinalIgnoreCase);
        if (fenceStart < 0) fenceStart = response.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            int bodyStart = response.IndexOf('\n', fenceStart);
            if (bodyStart < 0) return null;
            bodyStart++;
            int fenceEnd = response.IndexOf("```", bodyStart, StringComparison.Ordinal);
            if (fenceEnd < 0) return null;
            var body = response[bodyStart..fenceEnd].Trim();
            return body.Length > 0 ? body : null;
        }
        // Fallback: bare YAML, no fence. Accept it if it looks like a host: line
        // is present early. Cheaper than parsing optimistically and catching.
        int hostIdx = response.IndexOf("host:", StringComparison.OrdinalIgnoreCase);
        if (hostIdx >= 0 && hostIdx < 400)
        {
            return response[hostIdx..].Trim();
        }
        return null;
    }

    private static string Snip(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
