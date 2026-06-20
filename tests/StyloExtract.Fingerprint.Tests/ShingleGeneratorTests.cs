using FluentAssertions;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class ShingleGeneratorTests
{
    [Fact]
    public void Generate_TwoIdenticalDocuments_ProduceIdenticalShingleSequences()
    {
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var gen = new ShingleGenerator(noise);
        const string html = "<html><body><header><nav class='nav main-menu'><a>x</a></nav></header></body></html>";

        var a = gen.Generate(parser.Parse(html));
        var b = gen.Generate(parser.Parse(html));

        a.Should().Equal(b);
        a.Should().NotBeEmpty();
    }

    [Fact]
    public void Generate_DifferentNoiseClassesOnly_ProduceIdenticalShingles()
    {
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var gen = new ShingleGenerator(noise);
        const string htmlA = "<html><body><header class='dark-mode'><nav class='nav is-open'><a>x</a></nav></header></body></html>";
        const string htmlB = "<html><body><header class='light-mode'><nav class='nav is-closed'><a>x</a></nav></header></body></html>";

        var a = gen.Generate(parser.Parse(htmlA));
        var b = gen.Generate(parser.Parse(htmlB));

        a.Should().Equal(b);
    }

    [Fact]
    public void Generate_DifferentStructure_ProducesDifferentShingles()
    {
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var gen = new ShingleGenerator(noise);
        const string htmlA = "<html><body><header><nav><a>x</a></nav></header></body></html>";
        const string htmlB = "<html><body><main><article><p>x</p></article></main></body></html>";

        var a = gen.Generate(parser.Parse(htmlA));
        var b = gen.Generate(parser.Parse(htmlB));

        a.Should().NotEqual(b);
    }
}
