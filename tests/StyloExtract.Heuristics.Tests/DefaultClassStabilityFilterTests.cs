using FluentAssertions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class DefaultClassStabilityFilterTests
{
    private static readonly DefaultClassStabilityFilter Filter = new();

    [Theory]
    [InlineData("article-body")]
    [InlineData("post-content")]
    [InlineData("primary-nav")]
    [InlineData("header__title")]
    [InlineData("main")]
    [InlineData("mw-content-text")]
    public void Accepts_ReadableTokens(string token)
    {
        Filter.IsStable(token).Should().BeTrue();
    }

    [Theory]
    [InlineData("css-1a2b3c4")]
    [InlineData("sc-abc123")]
    [InlineData("tx7k9q2")]
    [InlineData("_1ab2cd__3ef4gh")]
    [InlineData("abc12345")]
    public void Rejects_HashShapedTokens(string token)
    {
        Filter.IsStable(token).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Rejects_EmptyOrNullOrWhitespace(string? token)
    {
        // The interface contract accepts non-null strings. We test the implementation
        // tolerates the null/empty/whitespace edge cases by returning false.
        Filter.IsStable(token!).Should().BeFalse();
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("nav")]
    [InlineData("h1")]
    public void Accepts_ShortStrings(string token)
    {
        Filter.IsStable(token).Should().BeTrue();
    }
}
