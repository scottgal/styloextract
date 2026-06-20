using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class StructuralFingerprinterTests
{
    private static IStructuralFingerprinter Build()
    {
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        return new StructuralFingerprinter(
            new ShingleGenerator(noise),
            sketcher,
            new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher),
            new PqGramExtractor());
    }

    [Fact]
    public void Compute_ReturnsFullyPopulatedFingerprint()
    {
        var parser = new AngleSharpHtmlDomParser();
        var fp = Build().Compute(parser.Parse("<html><body><main><p>x</p></main></body></html>"));

        fp.StructuralMinHash.Length.Should().Be(128);
        fp.AnchorMinHash.Length.Should().Be(128);
        fp.LshBands.Length.Should().Be(16);
        fp.Hex.Should().NotBeNullOrEmpty();
    }
}
