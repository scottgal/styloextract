using StyloExtract.Abstractions;

namespace StyloExtract.Markdown;

internal static class BlockRoleRenderers
{
    public static string Render(ExtractedBlock block)
    {
        // Producers walk the block's DOM and populate Markdown with a structured rendition
        // that preserves heading levels, paragraph breaks, inline links/emphasis/code,
        // images, lists, and GFM tables. When present we use it directly. Falling back to
        // flat-text escaping only happens for blocks whose producer did not populate
        // Markdown (legacy paths and roles where the role-specific projection beats a
        // generic DOM walk — navigation lists from .Links, etc.).
        if (!string.IsNullOrEmpty(block.Markdown)) return block.Markdown.TrimEnd();

        return block.Role switch
        {
            BlockRole.MainContent or BlockRole.Article => MarkdownEscaper.Escape(block.Text),
            BlockRole.RepeatedItem => MarkdownEscaper.Escape(block.Text),
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
    }

    private static string RenderForm(ExtractedBlock block)
    {
        if (block.Text.Trim().Length == 0) return string.Empty;
        return "Form: " + (block.Text.Length > 80 ? block.Text[..80] + "..." : block.Text);
    }
}
