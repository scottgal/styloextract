using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Markdown;
using Xunit;

namespace StyloExtract.Core.Tests;

public class MarkdownRendererTests
{
    private static ExtractedBlock Block(BlockRole role, string text, double linkDensity = 0.0) => new()
    {
        Id = "b", Role = role, Confidence = 0.9, Text = text, Markdown = "",
        XPath = "/", TextLength = text.Length, LinkDensity = linkDensity,
        Links = Array.Empty<ExtractedLink>()
    };

    [Fact]
    public void Render_MainContentOnly_DropsNavAndFooter()
    {
        IMarkdownRenderer r = new TypedMarkdownRenderer();
        var blocks = new[]
        {
            Block(BlockRole.PrimaryNavigation, "Home About"),
            Block(BlockRole.MainContent, "The article body."),
            Block(BlockRole.Footer, "© 2026")
        };

        var md = r.Render(blocks, ExtractionProfile.MainContentOnly);

        md.Should().Contain("The article body.");
        md.Should().NotContain("Home About");
        md.Should().NotContain("© 2026");
    }

    [Fact]
    public void Render_DebugFull_AnnotatesEveryBlock()
    {
        IMarkdownRenderer r = new TypedMarkdownRenderer();
        var blocks = new[] { Block(BlockRole.MainContent, "hello"), Block(BlockRole.Footer, "bye") };

        var md = r.Render(blocks, ExtractionProfile.DebugFull);

        md.Should().Contain("<!-- block:MainContent");
        md.Should().Contain("<!-- block:Footer");
    }

    [Fact]
    public void Render_AgentNavigation_KeepsNavDropsBody()
    {
        IMarkdownRenderer r = new TypedMarkdownRenderer();
        var blocks = new[]
        {
            Block(BlockRole.PrimaryNavigation, "Home About"),
            Block(BlockRole.MainContent, "Article body")
        };

        var md = r.Render(blocks, ExtractionProfile.AgentNavigation);

        md.Should().Contain("Home About");
        md.Should().NotContain("Article body");
    }
}
