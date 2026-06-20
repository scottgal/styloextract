using FluentAssertions;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class AnchorPathFingerprinterTests
{
    [Fact]
    public void Sketch_TwoPagesSameNavStructure_HighJaccard()
    {
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var anchor = new AnchorPathFingerprinter(noise, sketcher);

        const string a = "<html><body><nav><a href='/home'>H</a><a href='/about'>A</a><a href='/blog'>B</a></nav></body></html>";
        const string b = "<html><body><nav><a href='/home'>H</a><a href='/about'>A</a><a href='/blog'>B</a></nav></body></html>";

        var sa = anchor.Sketch(parser.Parse(a));
        var sb = anchor.Sketch(parser.Parse(b));

        JaccardEstimator.Estimate(sa, sb).Should().BeGreaterThan(0.95);
    }

    [Fact]
    public void Sketch_DifferentNavStructure_LowJaccard()
    {
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var anchor = new AnchorPathFingerprinter(noise, sketcher);

        const string a = "<html><body><nav><a href='/home'>H</a></nav></body></html>";
        const string b = "<html><body><footer><a href='https://twitter.com/x'>T</a></footer></body></html>";

        var sa = anchor.Sketch(parser.Parse(a));
        var sb = anchor.Sketch(parser.Parse(b));

        JaccardEstimator.Estimate(sa, sb).Should().BeLessThan(0.1);
    }
}
