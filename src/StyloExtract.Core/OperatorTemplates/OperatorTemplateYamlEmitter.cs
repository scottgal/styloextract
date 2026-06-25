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
                    .Append(rule.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\n');
            }
        }
        return sb.ToString();
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
