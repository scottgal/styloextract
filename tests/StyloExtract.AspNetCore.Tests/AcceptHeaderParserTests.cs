using FluentAssertions;
using StyloExtract.AspNetCore.Markdown;
using Xunit;

// Access internal type via InternalsVisibleTo or via reflection.
// Since AcceptHeaderParser is internal we use a thin test-access shim.
// The simplest approach: expose tests via a public wrapper defined in this project.

namespace StyloExtract.AspNetCore.Tests;

public class AcceptHeaderParserTests
{
    // ----- GetQuality -----

    [Fact]
    public void GetQuality_NullHeader_ReturnsOneForAnyType()
    {
        AcceptHeaderParserAccessor.GetQuality(null, "text/markdown").Should().Be(1.0);
        AcceptHeaderParserAccessor.GetQuality(null, "text/html").Should().Be(1.0);
    }

    [Fact]
    public void GetQuality_EmptyHeader_ReturnsOneForAnyType()
    {
        AcceptHeaderParserAccessor.GetQuality(string.Empty, "text/markdown").Should().Be(1.0);
        AcceptHeaderParserAccessor.GetQuality("   ", "text/markdown").Should().Be(1.0);
    }

    [Fact]
    public void GetQuality_ExactMatch_ReturnsOne()
    {
        AcceptHeaderParserAccessor.GetQuality("text/markdown", "text/markdown").Should().Be(1.0);
    }

    [Fact]
    public void GetQuality_ExactMatch_IsCaseInsensitive()
    {
        AcceptHeaderParserAccessor.GetQuality("TEXT/MARKDOWN", "text/markdown").Should().Be(1.0);
        AcceptHeaderParserAccessor.GetQuality("text/markdown", "TEXT/MARKDOWN").Should().Be(1.0);
    }

    [Fact]
    public void GetQuality_NotPresent_ReturnsZero()
    {
        AcceptHeaderParserAccessor.GetQuality("text/html", "text/markdown").Should().Be(0.0);
    }

    [Fact]
    public void GetQuality_WithExplicitQValue()
    {
        AcceptHeaderParserAccessor.GetQuality("text/markdown;q=0.9", "text/markdown").Should().BeApproximately(0.9, 0.001);
    }

    [Fact]
    public void GetQuality_MultipleValues_MarkdownHigher()
    {
        // text/html,text/markdown;q=0.9
        AcceptHeaderParserAccessor.GetQuality("text/html,text/markdown;q=0.9", "text/markdown").Should().BeApproximately(0.9, 0.001);
        AcceptHeaderParserAccessor.GetQuality("text/html,text/markdown;q=0.9", "text/html").Should().Be(1.0);
    }

    [Fact]
    public void GetQuality_MarkdownPreferred()
    {
        // text/markdown,text/html;q=0.5
        AcceptHeaderParserAccessor.GetQuality("text/markdown,text/html;q=0.5", "text/markdown").Should().Be(1.0);
        AcceptHeaderParserAccessor.GetQuality("text/markdown,text/html;q=0.5", "text/html").Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void GetQuality_FullWildcard_ReturnsConfiguredQ()
    {
        AcceptHeaderParserAccessor.GetQuality("*/*;q=0.1", "text/markdown").Should().BeApproximately(0.1, 0.001);
        AcceptHeaderParserAccessor.GetQuality("*/*;q=0.1", "application/json").Should().BeApproximately(0.1, 0.001);
    }

    [Fact]
    public void GetQuality_SubtypeWildcard_MatchesCorrectly()
    {
        AcceptHeaderParserAccessor.GetQuality("text/*", "text/markdown").Should().Be(1.0);
        AcceptHeaderParserAccessor.GetQuality("text/*", "text/html").Should().Be(1.0);
        AcceptHeaderParserAccessor.GetQuality("text/*", "application/json").Should().Be(0.0);
    }

    [Fact]
    public void GetQuality_ExplicitOverridesWildcard()
    {
        // Explicit match should beat wildcard regardless of order.
        AcceptHeaderParserAccessor.GetQuality("*/*;q=0.1, text/markdown;q=0.8", "text/markdown").Should().BeApproximately(0.8, 0.001);
    }

    [Fact]
    public void GetQuality_WhitespaceTolerance()
    {
        AcceptHeaderParserAccessor.GetQuality("text/html , text/markdown ; q = 0.9", "text/markdown").Should().BeApproximately(0.9, 0.001);
    }

    // ----- Prefers -----

    [Fact]
    public void Prefers_MarkdownOverHtml_WhenMarkdownHigher()
    {
        AcceptHeaderParserAccessor.Prefers("text/markdown,text/html;q=0.5", "text/markdown", "text/html").Should().BeTrue();
    }

    [Fact]
    public void Prefers_HtmlOverMarkdown_WhenHtmlHigher()
    {
        AcceptHeaderParserAccessor.Prefers("text/html,text/markdown;q=0.9", "text/markdown", "text/html").Should().BeFalse();
    }

    [Fact]
    public void Prefers_ReturnsFalse_WhenEqualQuality()
    {
        AcceptHeaderParserAccessor.Prefers("text/html, text/markdown", "text/markdown", "text/html").Should().BeFalse();
    }
}
