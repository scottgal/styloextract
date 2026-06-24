using System.Text;
using StyloExtract.Abstractions;

namespace StyloExtract.Markdown;

public sealed class TypedMarkdownRenderer : IMarkdownRenderer
{
    public string Render(IReadOnlyList<ExtractedBlock> blocks, ExtractionProfile profile)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (!ShouldEmit(block, profile)) continue;
            if (profile == ExtractionProfile.DebugFull)
            {
                sb.AppendLine($"<!-- block:{block.Role} confidence:{block.Confidence:F2} xpath:{block.XPath} -->");
            }
            // Wcxb: emit plain Text (no markdown syntax). All other profiles emit
            // the block's GFM Markdown via the role-specific renderer.
            sb.AppendLine(profile == ExtractionProfile.Wcxb
                ? block.Text
                : BlockRoleRenderers.Render(block));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static bool ShouldEmit(ExtractedBlock b, ExtractionProfile p)
    {
        if (p == ExtractionProfile.DebugFull) return true;

        // Quality gate: drop blocks with no meaningful content unless they carry links.
        if (b.Text.Trim().Length < 40 && b.Links.Count == 0) return false;

        return p switch
        {
            ExtractionProfile.MainContentOnly => b.Role is BlockRole.MainContent or BlockRole.Article
                or BlockRole.Heading or BlockRole.Summary or BlockRole.Table or BlockRole.CodeBlock
                or BlockRole.RepeatedItem,
            // RagFull is for LLM ingestion. Site navigation (primary/secondary nav) is noise,
            // not signal; drop it. Breadcrumb and RelatedLinks stay because they carry
            // article-context useful for retrieval.
            ExtractionProfile.RagFull => b.Role is not (BlockRole.Footer or BlockRole.Header
                or BlockRole.Advertisement or BlockRole.CookieBanner or BlockRole.Boilerplate
                or BlockRole.Unknown or BlockRole.PrimaryNavigation or BlockRole.SecondaryNavigation),
            // AgentNavigation: RepeatedItem is content, not navigation - exclude it.
            ExtractionProfile.AgentNavigation => b.Role is BlockRole.PrimaryNavigation or BlockRole.SecondaryNavigation
                or BlockRole.Breadcrumb or BlockRole.Form,
            // Wcxb: same role-set as MainContentOnly so we benchmark like-for-like
            // against word-overlap ground truth; the difference vs MainContentOnly
            // is in the render step (plain Text, not GFM Markdown).
            ExtractionProfile.Wcxb => b.Role is BlockRole.MainContent or BlockRole.Article
                or BlockRole.Heading or BlockRole.Summary or BlockRole.Table or BlockRole.CodeBlock
                or BlockRole.RepeatedItem,
            _ => true
        };
    }
}
