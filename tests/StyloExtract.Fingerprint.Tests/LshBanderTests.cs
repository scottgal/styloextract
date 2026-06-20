using FluentAssertions;
using StyloExtract.Fingerprint;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class LshBanderTests
{
    [Fact]
    public void BandHashes_IdenticalSignatures_ProduceIdenticalBands()
    {
        var sketcher = new MinHashSketcher(128);
        var bander = new LshBander(16, 8);
        var sig = sketcher.Sketch(Enumerable.Range(0, 100).Select(i => (ulong)i).ToArray());

        var a = bander.BandHashes(sig);
        var b = bander.BandHashes(sig);

        a.Should().Equal(b);
        a.Length.Should().Be(16);
    }

    [Fact]
    public void BandHashes_HighSimilarity_ShareSomeBands()
    {
        var sketcher = new MinHashSketcher(128);
        var bander = new LshBander(16, 8);
        var a = sketcher.Sketch(Enumerable.Range(0, 200).Select(i => (ulong)i).ToArray());
        var b = sketcher.Sketch(Enumerable.Range(0, 199).Select(i => (ulong)i).ToArray());

        var ba = bander.BandHashes(a);
        var bb = bander.BandHashes(b);

        ba.Intersect(bb).Should().NotBeEmpty();
    }
}
