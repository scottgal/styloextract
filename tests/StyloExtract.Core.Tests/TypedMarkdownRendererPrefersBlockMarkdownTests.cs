using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Markdown;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Asserts the <see cref="TypedMarkdownRenderer"/> prefers
/// <see cref="ExtractedBlock.Markdown"/> when populated (as the heuristic
/// classifier and applicator now do via <c>DomMarkdownWalker</c>) and falls
/// back to the role-specific projection when not. This is the seam that lets
/// the structured DOM-walk output reach the consumer without disturbing
/// existing nav/breadcrumb/footer projections.
/// </summary>
public class TypedMarkdownRendererPrefersBlockMarkdownTests
{
    private static ExtractedBlock Block(BlockRole role, string text, string markdown = "", IReadOnlyList<ExtractedLink>? links = null) =>
        new()
        {
            Id = "b0000",
            Role = role,
            Confidence = 0.9,
            Text = text,
            Markdown = markdown,
            XPath = "/html/body",
            CssSelector = null,
            TextLength = text.Length,
            LinkDensity = 0.0,
            Links = links ?? Array.Empty<ExtractedLink>(),
        };

    [Fact]
    public void Renderer_Uses_BlockMarkdown_Verbatim_When_NonEmpty()
    {
        var b = Block(BlockRole.MainContent,
            text: "Plain flattened text that the legacy escape path would emit.",
            markdown: "# Real Heading\n\nReal body with [a link](/x) and **bold**.");
        var output = new TypedMarkdownRenderer().Render(new[] { b }, ExtractionProfile.MainContentOnly);
        output.Should().Contain("# Real Heading");
        output.Should().Contain("[a link](/x)");
        output.Should().Contain("**bold**");
        // The legacy fall-through would escape and emit "Plain flattened text..." verbatim;
        // when Markdown is preferred the Text field should not appear at all.
        output.Should().NotContain("Plain flattened");
    }

    [Fact]
    public void Renderer_Falls_Back_To_Legacy_Projection_When_Markdown_Empty()
    {
        // Long enough to clear the renderer's 40-char content gate.
        var b = Block(BlockRole.MainContent,
            text: "Legacy projection: this text comes from .Text because Markdown is empty.");
        var output = new TypedMarkdownRenderer().Render(new[] { b }, ExtractionProfile.MainContentOnly);
        output.Should().Contain("Legacy projection");
    }

    [Fact]
    public void Renderer_Uses_Links_Projection_For_Navigation_Even_When_Markdown_Empty()
    {
        // Nav blocks have empty Markdown by design (the classifier's ShouldRenderMarkdown
        // gate). The legacy Links-based projection must still run.
        var links = new[]
        {
            new ExtractedLink { Text = "Home", Href = "/", IsExternal = false },
            new ExtractedLink { Text = "Blog", Href = "/blog", IsExternal = false },
            new ExtractedLink { Text = "About", Href = "/about", IsExternal = false },
        };
        var b = Block(BlockRole.PrimaryNavigation, text: "ignored", markdown: "", links: links);
        var output = new TypedMarkdownRenderer().Render(new[] { b }, ExtractionProfile.AgentNavigation);
        output.Should().Contain("- [Home](/)");
        output.Should().Contain("- [Blog](/blog)");
        output.Should().Contain("- [About](/about)");
    }

    [Fact]
    public void Renderer_Uses_Breadcrumb_Projection_When_Markdown_Empty()
    {
        var links = new[]
        {
            new ExtractedLink { Text = "Home", Href = "/", IsExternal = false },
            new ExtractedLink { Text = "Docs", Href = "/docs", IsExternal = false },
            new ExtractedLink { Text = "Markdown", Href = "/docs/markdown", IsExternal = false },
        };
        var b = Block(BlockRole.Breadcrumb, text: "ignored", markdown: "", links: links);
        var output = new TypedMarkdownRenderer().Render(new[] { b }, ExtractionProfile.AgentNavigation);
        output.Should().Contain("[Home](/) / [Docs](/docs) / [Markdown](/docs/markdown)");
    }

    [Fact]
    public void Renderer_TrimsTrailingWhitespace_On_BlockMarkdown()
    {
        // The walker terminates its output with a single newline; the renderer trims
        // trailing whitespace from each block's projection so blank-line spacing
        // is owned by the renderer, not the walker.
        var b = Block(BlockRole.MainContent,
            text: "Plain text long enough to clear the renderer's 40-char emission gate.",
            markdown: "# Title\n\nbody paragraph long enough to be meaningful\n\n\n");
        var output = new TypedMarkdownRenderer().Render(new[] { b }, ExtractionProfile.MainContentOnly);
        output.Should().NotContain("\n\n\n");
        output.Should().Contain("body paragraph");
    }

    [Fact]
    public void Renderer_Emits_Blocks_With_Blank_Line_Between_Them()
    {
        var a = Block(BlockRole.MainContent,
            text: "Text-length stand-in for the renderer's emission quality gate of forty characters.",
            markdown: "# Article one\n\nbody one paragraph emit");
        var c = Block(BlockRole.MainContent,
            text: "Second block also exceeding the renderer's emission quality gate of forty characters.",
            markdown: "# Article two\n\nbody two paragraph emit");
        var output = new TypedMarkdownRenderer().Render(new[] { a, c }, ExtractionProfile.MainContentOnly);
        output.Should().Contain("body one paragraph emit");
        output.Should().Contain("# Article two");
        // The renderer's AppendLine() + blank-line pattern means at least one blank
        // line separates the two block bodies.
        output.IndexOf("body one paragraph emit").Should().BeLessThan(output.IndexOf("# Article two"));
    }
}
