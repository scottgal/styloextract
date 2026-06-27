using AngleSharp.Dom;
using System.IO.Hashing;
using System.Text;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public static class IdentityClaimExtractor
{
    /// <summary>
    /// Build a full identity-attribute snapshot of an element. Used by:
    /// - The inducer when emitting claims into a template.
    /// - The layout-extractor apply path when evaluating claims at request time.
    /// - The corpus miner (Phase 2) when persisting observations.
    /// </summary>
    public static ElementClaimSet Extract(IElement element, IClassStabilityFilter classFilter)
    {
        var tag = element.LocalName.ToLowerInvariant();
        var tagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag));

        string? id = null; ulong? idHash = null;
        var rawId = element.Id;
        if (!string.IsNullOrEmpty(rawId) && classFilter.IsStable(rawId))
        {
            id = rawId;
            idHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(rawId));
        }

        var classes = new List<string>();
        var classHashes = new List<ulong>();
        if (element.ClassName is { } classAttr && !string.IsNullOrWhiteSpace(classAttr))
        {
            foreach (var c in classAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!classFilter.IsStable(c)) continue;
                classes.Add(c);
                classHashes.Add(XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(c)));
            }
        }

        var dataAttrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ariaAttrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? role = null;

        foreach (var attr in element.Attributes)
        {
            var name = attr.Name;
            if (name.StartsWith("data-", StringComparison.OrdinalIgnoreCase))
                dataAttrs[name[5..]] = attr.Value;
            else if (name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase))
                ariaAttrs[name[5..]] = attr.Value;
            else if (string.Equals(name, "role", StringComparison.OrdinalIgnoreCase))
                role = attr.Value;
        }

        return new ElementClaimSet
        {
            Tag = tag, TagHash = tagHash,
            Id = id, IdHash = idHash,
            Classes = classes, ClassHashes = classHashes,
            DataAttrs = dataAttrs, AriaAttrs = ariaAttrs,
            Role = role,
        };
    }
}
