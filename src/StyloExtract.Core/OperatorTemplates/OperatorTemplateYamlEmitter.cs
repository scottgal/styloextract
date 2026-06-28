using System.Globalization;
using System.Text;
using StyloExtract.Abstractions;

namespace StyloExtract.Core.OperatorTemplates;

/// <summary>
/// Canonical YAML emitter for <see cref="OperatorTemplate"/>. Inverse of
/// <see cref="YamlOperatorTemplateLoader"/>: the round-trip
/// <c>Emit(Parse(yaml))</c> produces a string that re-parses to the
/// same template (modulo whitespace normalisation).
///
/// <para>
/// Lives here so the CLI, the REST endpoints, and the background
/// enrichment coordinator all share one source of truth for the
/// on-disk shape. Earlier each surface carried its own private
/// emitter — caught in the architectural drift review.
/// </para>
/// </summary>
public static class OperatorTemplateYamlEmitter
{
    /// <summary>
    /// Render <paramref name="t"/> to the canonical YAML shape the
    /// parser accepts. Stable indentation (2-space per nesting level)
    /// so output stays diff-friendly across re-emits.
    /// </summary>
    public static string Emit(OperatorTemplate t)
    {
        if (t is null) throw new ArgumentNullException(nameof(t));
        var sb = new StringBuilder();
        sb.Append("host: ").Append(t.Host).Append('\n');
        if (!string.IsNullOrEmpty(t.Description))
            sb.Append("description: ").Append(t.Description).Append('\n');
        if (t.Version != 1)
            sb.Append("version: ").Append(t.Version).Append('\n');
        sb.Append("rules:\n");
        foreach (var rule in t.Rules)
        {
            sb.Append("  - role: ").Append(rule.Role).Append('\n');
            sb.Append("    selectors:\n");
            foreach (var sel in rule.Selectors)
            {
                sb.Append("      - ").Append(QuoteIfYamlSpecial(sel)).Append('\n');
            }
            if (rule.Confidence != 1.0)
            {
                sb.Append("    confidence: ")
                    .Append(rule.Confidence.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }
            if (rule.Claims is { Count: > 0 } claims)
            {
                EmitChain(sb, claims);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Emit the identity-claim ancestor chain for one rule, outermost first.
    /// Each chain hop is one YAML mapping with at minimum a <c>tag:</c> key.
    /// Optional fields (id, role, classes, data, aria) only emit when present
    /// so chains stay diff-friendly. The hot-path applicator never reads back
    /// from YAML; the loader is responsible for re-deriving the precomputed
    /// hash fields on <see cref="IdentityClaim"/>.
    /// </summary>
    private static void EmitChain(StringBuilder sb, IReadOnlyList<IdentityClaim> chain)
    {
        sb.Append("    chain:\n");
        foreach (var claim in chain)
        {
            sb.Append("      - tag: ").Append(QuoteIfYamlSpecial(claim.Tag)).Append('\n');
            if (!string.IsNullOrEmpty(claim.Id))
            {
                sb.Append("        id: ").Append(QuoteIfYamlSpecial(claim.Id)).Append('\n');
            }
            if (!string.IsNullOrEmpty(claim.Role))
            {
                sb.Append("        role: ").Append(QuoteIfYamlSpecial(claim.Role)).Append('\n');
            }
            if (claim.Classes.Count > 0)
            {
                sb.Append("        classes:\n");
                foreach (var c in claim.Classes)
                    sb.Append("          - ").Append(QuoteIfYamlSpecial(c)).Append('\n');
            }
            if (claim.DataAttrs.Count > 0)
            {
                sb.Append("        data:\n");
                foreach (var kv in claim.DataAttrs)
                {
                    sb.Append("          ")
                        .Append(QuoteIfYamlSpecial(kv.Key))
                        .Append(": ")
                        .Append(QuoteIfYamlSpecial(kv.Value))
                        .Append('\n');
                }
            }
            if (claim.AriaAttrs.Count > 0)
            {
                sb.Append("        aria:\n");
                foreach (var kv in claim.AriaAttrs)
                {
                    sb.Append("          ")
                        .Append(QuoteIfYamlSpecial(kv.Key))
                        .Append(": ")
                        .Append(QuoteIfYamlSpecial(kv.Value))
                        .Append('\n');
                }
            }
        }
    }

    /// <summary>
    /// CSS selectors that start with characters YAML treats as control tokens
    /// (#, &amp;, *, [, {, |, &gt;, ?, ', ") need to be quoted, or the parser
    /// reads them as comments / anchors / flow sequences instead of strings.
    /// `#some-id` is by far the most common case (ID selectors emitted by the
    /// LLM). Use single quotes — selectors never contain a literal single
    /// quote, so no escaping needed.
    /// </summary>
    private static string QuoteIfYamlSpecial(string selector)
    {
        if (string.IsNullOrEmpty(selector)) return "''";
        char first = selector[0];
        if (first is '#' or '&' or '*' or '[' or '{' or '|' or '>' or '?' or '\'' or '"' or '!' or '%' or '@' or '`')
            return $"'{selector}'";
        return selector;
    }
}
