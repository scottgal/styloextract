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
            sb.AppendLine(BlockRoleRenderers.Render(block));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static bool ShouldEmit(ExtractedBlock b, ExtractionProfile p) => p switch
    {
        ExtractionProfile.MainContentOnly => b.Role is BlockRole.MainContent or BlockRole.Article
            or BlockRole.Heading or BlockRole.Summary or BlockRole.Table or BlockRole.CodeBlock,
        ExtractionProfile.RagFull => b.Role is not (BlockRole.Footer or BlockRole.Header or BlockRole.Advertisement
            or BlockRole.CookieBanner or BlockRole.Boilerplate or BlockRole.Unknown),
        ExtractionProfile.AgentNavigation => b.Role is BlockRole.PrimaryNavigation or BlockRole.SecondaryNavigation
            or BlockRole.Breadcrumb or BlockRole.Form,
        ExtractionProfile.DebugFull => true,
        _ => true
    };
}
