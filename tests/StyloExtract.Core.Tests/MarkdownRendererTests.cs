using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Markdown;
using Xunit;

namespace StyloExtract.Core.Tests;

public class MarkdownRendererTests
{
    // Helper produces a block that always clears the quality gate (>= 40 chars).
    // Use sufficiently long text so profile-based role filtering is the only thing
    // under test here; the quality gate itself is tested separately below.
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
            Block(BlockRole.PrimaryNavigation, "Home About Blog Archive Contact Us Pages"),
            Block(BlockRole.MainContent, "The article body contains substantial prose text for a proper extraction."),
            Block(BlockRole.Footer, "Copyright 2026 Example Corp. All rights reserved worldwide.")
        };

        var md = r.Render(blocks, ExtractionProfile.MainContentOnly);

        md.Should().Contain("The article body contains substantial prose text for a proper extraction.");
        md.Should().NotContain("Home About Blog Archive Contact Us Pages");
        md.Should().NotContain("Copyright 2026 Example Corp.");
    }

    [Fact]
    public void Render_DebugFull_AnnotatesEveryBlock()
    {
        IMarkdownRenderer r = new TypedMarkdownRenderer();
        // DebugFull bypasses the quality gate so short text is fine.
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
            Block(BlockRole.PrimaryNavigation, "Home About Blog Archive Contact Pages More Links Here"),
            Block(BlockRole.MainContent, "Article body with a substantial amount of text that exceeds the quality gate.")
        };

        var md = r.Render(blocks, ExtractionProfile.AgentNavigation);

        md.Should().Contain("Home About Blog Archive Contact Pages More Links Here");
        md.Should().NotContain("Article body with a substantial amount of text");
    }

    [Fact]
    public void Render_QualityGate_DropsShortContentlessBlocks()
    {
        IMarkdownRenderer r = new TypedMarkdownRenderer();
        // Short block with no links must be dropped by the quality gate for all non-DebugFull profiles.
        var shortBlock = Block(BlockRole.MainContent, "Too short.");
        var longBlock = Block(BlockRole.MainContent, "This block has enough text to pass the quality gate and should appear in output.");

        var mdRag = r.Render(new[] { shortBlock, longBlock }, ExtractionProfile.RagFull);
        mdRag.Should().NotContain("Too short.");
        mdRag.Should().Contain("This block has enough text");

        // DebugFull bypasses the quality gate.
        var mdDebug = r.Render(new[] { shortBlock }, ExtractionProfile.DebugFull);
        mdDebug.Should().Contain("Too short.");
    }
}
