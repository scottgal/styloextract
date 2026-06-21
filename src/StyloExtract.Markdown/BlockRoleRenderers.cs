using StyloExtract.Abstractions;

namespace StyloExtract.Markdown;

internal static class BlockRoleRenderers
{
    public static string Render(ExtractedBlock block) => block.Role switch
    {
        BlockRole.MainContent or BlockRole.Article => MarkdownEscaper.Escape(block.Text),
        BlockRole.Heading => "# " + MarkdownEscaper.Escape(block.Text),
        BlockRole.PrimaryNavigation or BlockRole.SecondaryNavigation =>
            block.Links.Any() ? string.Join("\n", block.Links.Select(l => $"- [{l.Text}]({l.Href})")) : MarkdownEscaper.Escape(block.Text),
        BlockRole.Breadcrumb =>
            block.Links.Any() ? string.Join(" / ", block.Links.Select(l => $"[{l.Text}]({l.Href})")) : MarkdownEscaper.Escape(block.Text),
        BlockRole.Footer or BlockRole.Boilerplate => MarkdownEscaper.Escape(block.Text),
        BlockRole.Form => RenderForm(block),
        BlockRole.Table or BlockRole.CodeBlock => block.Text,
        _ => MarkdownEscaper.Escape(block.Text)
    };

    private static string RenderForm(ExtractedBlock block)
    {
        if (block.Text.Trim().Length == 0) return string.Empty;
        return "Form: " + (block.Text.Length > 80 ? block.Text[..80] + "..." : block.Text);
    }
}
