using StyloExtract.Abstractions;

namespace StyloExtract.Core.OperatorTemplates;

/// <summary>
/// Hand-written parser for the operator-template YAML schema. Stays AOT-clean
/// (no reflection, no source-gen dependency on a third-party YAML library) by
/// scanning the document line by line and building the record directly.
///
/// <para>
/// Supports only the schema shape documented in
/// <c>docs/operator-templates-design.md</c>:
/// </para>
/// <code>
/// host: example.com
/// description: free-form text on one line
/// version: 1
/// rules:
///   - role: MainContent
///     selectors:
///       - main.docs-body
///       - article.markdown-body
///     confidence: 0.95
///   - role: PrimaryNavigation
///     selectors:
///       - nav.sidebar
/// </code>
/// </summary>
public static class YamlOperatorTemplateLoader
{
    /// <summary>
    /// Parse <paramref name="yaml"/> into an <see cref="OperatorTemplate"/>.
    /// Throws <see cref="OperatorTemplateParseException"/> with a line number on
    /// any structural error; never throws other exception types.
    /// </summary>
    public static OperatorTemplate Parse(string yaml)
    {
        if (yaml is null) throw new ArgumentNullException(nameof(yaml));

        string? host = null;
        string description = "";
        int version = 1;
        var rules = new List<OperatorTemplateRule>();

        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = StripComment(raw).TrimEnd();
            if (trimmed.Length == 0 || IsCommentLine(trimmed))
            {
                i++;
                continue;
            }
            int indent = LeadingSpaces(trimmed);
            var keyValue = trimmed.TrimStart();

            if (indent != 0)
            {
                throw Fail(i + 1, "top-level key must not be indented");
            }

            if (TryGetKey(keyValue, out var key, out var value))
            {
                switch (key)
                {
                    case "host":
                        host = RequireScalar(value, i + 1, "host");
                        i++;
                        break;
                    case "description":
                        description = RequireScalar(value, i + 1, "description");
                        i++;
                        break;
                    case "version":
                        if (!int.TryParse(RequireScalar(value, i + 1, "version"), out version) || version < 1)
                            throw Fail(i + 1, "version must be a positive integer");
                        i++;
                        break;
                    case "rules":
                        if (value.Length != 0)
                            throw Fail(i + 1, "rules: must be followed by an indented list, not an inline value");
                        i++;
                        i = ParseRulesBlock(lines, i, rules);
                        break;
                    default:
                        throw Fail(i + 1, $"unknown top-level key '{key}'");
                }
            }
            else
            {
                throw Fail(i + 1, "expected 'key: value'");
            }
        }

        if (string.IsNullOrWhiteSpace(host)) throw Fail(0, "missing required 'host' field");
        if (rules.Count == 0) throw Fail(0, "missing required 'rules' field (or rules is empty)");

        return new OperatorTemplate
        {
            Host = host.Trim(),
            Description = description.Trim(),
            Version = version,
            Rules = rules,
        };
    }

    // Parses the body of a `rules:` block: a sequence of `- role: X` entries with
    // nested selectors / confidence keys. Returns the index after the block.
    private static int ParseRulesBlock(string[] lines, int start, List<OperatorTemplateRule> sink)
    {
        const int RuleEntryIndent = 2;     // `- role: X` sits at 2 spaces.
        const int RuleFieldIndent = 4;     // `selectors:` and `confidence:` sit at 4 spaces.
        const int SelectorItemIndent = 6;  // `- main.docs` sits at 6 spaces.

        int i = start;
        BlockRole? currentRole = null;
        List<string>? currentSelectors = null;
        double currentConfidence = 1.0;

        void Flush(int lineNumber)
        {
            if (currentRole is null) return;
            if (currentSelectors is null || currentSelectors.Count == 0)
                throw Fail(lineNumber, "rule has no selectors");
            sink.Add(new OperatorTemplateRule
            {
                Role = currentRole.Value,
                Selectors = currentSelectors,
                Confidence = currentConfidence,
            });
            currentRole = null;
            currentSelectors = null;
            currentConfidence = 1.0;
        }

        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = StripComment(raw).TrimEnd();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }
            int indent = LeadingSpaces(trimmed);
            if (indent == 0)
            {
                // Back to a top-level key — rules block done.
                Flush(i + 1);
                break;
            }
            var stripped = trimmed.TrimStart();

            if (indent == RuleEntryIndent && stripped.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush(i + 1);
                // The first key of the new rule entry follows the dash.
                var afterDash = stripped[2..].TrimStart();
                if (!TryGetKey(afterDash, out var k, out var v) || k != "role")
                    throw Fail(i + 1, "every rule must start with 'role:'");
                currentRole = ParseRole(v, i + 1);
                currentSelectors = null;
                currentConfidence = 1.0;
                i++;
                continue;
            }

            if (indent == RuleFieldIndent && TryGetKey(stripped, out var key, out var value))
            {
                switch (key)
                {
                    case "role":
                        // A second role key inside one rule entry: invalid.
                        throw Fail(i + 1, "duplicate 'role' inside a rule");
                    case "confidence":
                        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var conf)
                            || conf < 0 || conf > 1)
                            throw Fail(i + 1, "confidence must be between 0.0 and 1.0");
                        currentConfidence = conf;
                        i++;
                        continue;
                    case "selectors":
                        if (value.Length != 0)
                            throw Fail(i + 1, "selectors: must be followed by an indented list");
                        currentSelectors = new List<string>();
                        i++;
                        while (i < lines.Length)
                        {
                            var sRaw = lines[i];
                            var sTrimmed = StripComment(sRaw).TrimEnd();
                            if (sTrimmed.Length == 0) { i++; continue; }
                            int sIndent = LeadingSpaces(sTrimmed);
                            var sStripped = sTrimmed.TrimStart();
                            if (sIndent == SelectorItemIndent && sStripped.StartsWith("- ", StringComparison.Ordinal))
                            {
                                var sel = sStripped[2..].Trim();
                                if (sel.Length == 0) throw Fail(i + 1, "empty selector");
                                currentSelectors.Add(StripQuotes(sel));
                                i++;
                                continue;
                            }
                            break;
                        }
                        continue;
                    default:
                        throw Fail(i + 1, $"unknown rule field '{key}'");
                }
            }
            throw Fail(i + 1, "unexpected indentation or shape");
        }

        Flush(i);
        return i;
    }

    private static BlockRole ParseRole(string value, int lineNumber)
    {
        var v = value.Trim();
        if (v.Length == 0) throw Fail(lineNumber, "role is empty");
        if (Enum.TryParse<BlockRole>(v, ignoreCase: true, out var role)) return role;
        throw Fail(lineNumber, $"unknown role '{v}'");
    }

    private static bool TryGetKey(string line, out string key, out string value)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0) { key = ""; value = ""; return false; }
        key = line[..colon].Trim();
        value = line[(colon + 1)..].Trim();
        return key.Length > 0;
    }

    private static string RequireScalar(string value, int lineNumber, string fieldName)
    {
        if (value.Length == 0) throw Fail(lineNumber, $"{fieldName}: requires a value");
        return StripQuotes(value);
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }

    private static bool IsCommentLine(string s) => s.TrimStart().StartsWith('#');

    private static string StripComment(string s)
    {
        // YAML inline comments start with " # " (space before hash). We don't try
        // to be subtle: anything after " #" or at column 0 with `#` is a comment.
        // Inside a double-quoted value we'd need to be careful, but we don't allow
        // multi-line quoted strings, so the cheap rule is fine for our schema.
        int idx = s.IndexOf(" #", StringComparison.Ordinal);
        return idx >= 0 ? s[..idx] : s;
    }

    private static int LeadingSpaces(string s)
    {
        int n = 0;
        while (n < s.Length && s[n] == ' ') n++;
        return n;
    }

    private static OperatorTemplateParseException Fail(int lineNumber, string message)
        => new($"operator template YAML parse error at line {lineNumber}: {message}");
}

public sealed class OperatorTemplateParseException : Exception
{
    public OperatorTemplateParseException(string message) : base(message) { }
}
