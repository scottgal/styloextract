using System.IO.Hashing;
using System.Text;
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
        List<IdentityClaim>? currentClaims = null;

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
                Claims = currentClaims,
            });
            currentRole = null;
            currentSelectors = null;
            currentConfidence = 1.0;
            currentClaims = null;
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
                    case "chain":
                        if (value.Length != 0)
                            throw Fail(i + 1, "chain: must be followed by an indented list");
                        currentClaims = new List<IdentityClaim>();
                        i++;
                        i = ParseChainBlock(lines, i, currentClaims);
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

    /// <summary>
    /// Parse one rule's <c>chain:</c> block. Each chain hop is a YAML mapping
    /// that opens with a <c>- tag:</c> dash and may carry optional <c>id</c>,
    /// <c>role</c>, <c>classes</c>, <c>data</c>, <c>aria</c> keys. Stops at
    /// the first line whose indent drops below the chain-hop body indent.
    /// Precomputed hashes on <see cref="IdentityClaim"/> are derived here so
    /// the apply path can match without re-hashing per element.
    /// </summary>
    private static int ParseChainBlock(string[] lines, int start, List<IdentityClaim> sink)
    {
        const int HopEntryIndent = 6;   // `- tag: X` sits 6 spaces in (under the rule's 4-space body).
        const int HopFieldIndent = 8;   // optional keys (id, role, classes:, data:, aria:) sit 8 spaces in.
        const int HopListIndent = 10;   // list items under classes / data / aria sit 10 spaces in.

        int i = start;

        string? tag = null;
        string? id = null;
        string? role = null;
        List<string>? classes = null;
        Dictionary<string, string>? data = null;
        Dictionary<string, string>? aria = null;

        void FlushHop(int lineNumber)
        {
            if (tag is null) return;
            sink.Add(BuildClaim(tag, id, role, classes, data, aria));
            tag = null;
            id = null;
            role = null;
            classes = null;
            data = null;
            aria = null;
        }

        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = StripComment(raw).TrimEnd();
            if (trimmed.Length == 0) { i++; continue; }
            int indent = LeadingSpaces(trimmed);
            if (indent < HopEntryIndent)
            {
                // De-indent below the chain entries means we're done with this chain block.
                FlushHop(i + 1);
                return i;
            }
            var stripped = trimmed.TrimStart();

            if (indent == HopEntryIndent && stripped.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushHop(i + 1);
                var afterDash = stripped[2..].TrimStart();
                if (!TryGetKey(afterDash, out var k, out var v) || k != "tag")
                    throw Fail(i + 1, "every chain hop must start with 'tag:'");
                if (v.Length == 0)
                    throw Fail(i + 1, "chain hop 'tag' is empty");
                tag = StripQuotes(v).Trim();
                i++;
                continue;
            }

            if (indent == HopFieldIndent && TryGetKey(stripped, out var key, out var value))
            {
                switch (key)
                {
                    case "tag":
                        throw Fail(i + 1, "duplicate 'tag' inside a chain hop");
                    case "id":
                        id = RequireScalar(value, i + 1, "id");
                        i++;
                        continue;
                    case "role":
                        role = RequireScalar(value, i + 1, "role");
                        i++;
                        continue;
                    case "classes":
                        if (value.Length != 0)
                            throw Fail(i + 1, "classes: must be followed by an indented list");
                        classes = new List<string>();
                        i++;
                        while (i < lines.Length)
                        {
                            var lRaw = lines[i];
                            var lTrimmed = StripComment(lRaw).TrimEnd();
                            if (lTrimmed.Length == 0) { i++; continue; }
                            int lIndent = LeadingSpaces(lTrimmed);
                            var lStripped = lTrimmed.TrimStart();
                            if (lIndent == HopListIndent && lStripped.StartsWith("- ", StringComparison.Ordinal))
                            {
                                var c = lStripped[2..].Trim();
                                if (c.Length == 0) throw Fail(i + 1, "empty class name in chain hop");
                                classes.Add(StripQuotes(c));
                                i++;
                                continue;
                            }
                            break;
                        }
                        continue;
                    case "data":
                        if (value.Length != 0)
                            throw Fail(i + 1, "data: must be followed by indented key/value pairs");
                        data = new Dictionary<string, string>();
                        i = ParseHopAttrMap(lines, i + 1, HopListIndent, data, "data");
                        continue;
                    case "aria":
                        if (value.Length != 0)
                            throw Fail(i + 1, "aria: must be followed by indented key/value pairs");
                        aria = new Dictionary<string, string>();
                        i = ParseHopAttrMap(lines, i + 1, HopListIndent, aria, "aria");
                        continue;
                    default:
                        throw Fail(i + 1, $"unknown chain-hop field '{key}'");
                }
            }
            throw Fail(i + 1, "unexpected indentation or shape inside chain block");
        }

        FlushHop(i);
        return i;
    }

    private static int ParseHopAttrMap(string[] lines, int start, int expectedIndent, Dictionary<string, string> sink, string fieldName)
    {
        int i = start;
        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = StripComment(raw).TrimEnd();
            if (trimmed.Length == 0) { i++; continue; }
            int indent = LeadingSpaces(trimmed);
            if (indent != expectedIndent) break;
            var stripped = trimmed.TrimStart();
            if (!TryGetKey(stripped, out var k, out var v))
                throw Fail(i + 1, $"{fieldName} entry must be 'name: value'");
            var name = StripQuotes(k).Trim();
            var val = StripQuotes(v).Trim();
            if (name.Length == 0) throw Fail(i + 1, $"empty {fieldName} attribute name");
            sink[name] = val;
            i++;
        }
        return i;
    }

    /// <summary>
    /// Build an <see cref="IdentityClaim"/> from the raw YAML fields,
    /// recomputing the xxHash3 fields the hot-path applicator depends on.
    /// </summary>
    private static IdentityClaim BuildClaim(
        string tag,
        string? id,
        string? role,
        IReadOnlyList<string>? classes,
        IReadOnlyDictionary<string, string>? data,
        IReadOnlyDictionary<string, string>? aria)
    {
        var classList = classes ?? Array.Empty<string>();
        var classHashes = new ulong[classList.Count];
        for (int i = 0; i < classList.Count; i++)
            classHashes[i] = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(classList[i]));

        return new IdentityClaim
        {
            Tag = tag,
            TagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag)),
            Id = string.IsNullOrEmpty(id) ? null : id,
            IdHash = string.IsNullOrEmpty(id) ? null : XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(id)),
            Classes = classList,
            ClassHashes = classHashes,
            Role = string.IsNullOrEmpty(role) ? null : role,
            DataAttrs = data ?? new Dictionary<string, string>(),
            AriaAttrs = aria ?? new Dictionary<string, string>(),
        };
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
