using System.IO.Hashing;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class IdentityClaimExtractorTests
{
    private static readonly IClassStabilityFilter Filter = new DefaultClassStabilityFilter();

    private static IElement ParseFirst(string html, string selector)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        return doc.QuerySelector(selector)!;
    }

    private static ulong H(string s) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Extract_PopulatesTagAndTagHash()
    {
        var el = ParseFirst("<html><body><article id='x'></article></body></html>", "article");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.Tag.Should().Be("article");
        claim.TagHash.Should().Be(H("article"));
    }

    [Fact]
    public void Extract_TagIsLowercased()
    {
        var el = ParseFirst("<html><body><MAIN></MAIN></body></html>", "main");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.Tag.Should().Be("main");
        claim.TagHash.Should().Be(H("main"));
    }

    [Fact]
    public void Extract_PopulatesIdAndIdHash()
    {
        var el = ParseFirst("<html><body><div id='content'></div></body></html>", "div");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.Id.Should().Be("content");
        claim.IdHash.Should().Be(H("content"));
    }

    [Fact]
    public void Extract_OmitsIdWhenAbsent()
    {
        var el = ParseFirst("<html><body><div></div></body></html>", "div");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.Id.Should().BeNull();
        claim.IdHash.Should().BeNull();
    }

    [Fact]
    public void Extract_FiltersHashShapedClasses()
    {
        // "css-1a2b3c4" and "tx7k9q2" should be filtered out; "article-body" kept.
        var el = ParseFirst(
            "<html><body><div class='article-body css-1a2b3c4 tx7k9q2'></div></body></html>",
            "div");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.Classes.Should().BeEquivalentTo(["article-body"]);
        claim.ClassHashes.Should().HaveCount(1);
        claim.ClassHashes[0].Should().Be(H("article-body"));
    }

    [Fact]
    public void Extract_KeepsStableClasses()
    {
        var el = ParseFirst(
            "<html><body><div class='primary-nav article-body'></div></body></html>",
            "div");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.Classes.Should().BeEquivalentTo(["primary-nav", "article-body"]);
        claim.ClassHashes.Should().HaveCount(2);
    }

    [Fact]
    public void Extract_ParsesDataAttrs_WithoutPrefix()
    {
        var el = ParseFirst(
            "<html><body><div data-role='post' data-id='42'></div></body></html>",
            "div");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.DataAttrs.Should().ContainKey("role").WhoseValue.Should().Be("post");
        claim.DataAttrs.Should().ContainKey("id").WhoseValue.Should().Be("42");
        claim.DataAttrs.Should().NotContainKey("data-role");
    }

    [Fact]
    public void Extract_ParsesAriaAttrs_WithoutPrefix()
    {
        var el = ParseFirst(
            "<html><body><button aria-label='Submit' aria-pressed='false'></button></body></html>",
            "button");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.AriaAttrs.Should().ContainKey("label").WhoseValue.Should().Be("Submit");
        claim.AriaAttrs.Should().ContainKey("pressed").WhoseValue.Should().Be("false");
        claim.AriaAttrs.Should().NotContainKey("aria-label");
    }

    [Fact]
    public void Extract_PopulatesRole()
    {
        var el = ParseFirst(
            "<html><body><div role='navigation'></div></body></html>",
            "div");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.Role.Should().Be("navigation");
    }

    [Fact]
    public void Extract_RoleAbsent_StaysNull()
    {
        var el = ParseFirst("<html><body><div></div></body></html>", "div");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.Role.Should().BeNull();
    }

    [Fact]
    public void Extract_FiltersHashShapedId()
    {
        // The id "css-1a2b3c4" should be filtered out by the stability filter.
        var el = ParseFirst(
            "<html><body><div id='css-1a2b3c4'></div></body></html>",
            "div");

        var claim = IdentityClaimExtractor.Extract(el, Filter);

        claim.Id.Should().BeNull();
        claim.IdHash.Should().BeNull();
    }
}
