using FluentAssertions;
using StyloExtract.Fingerprint;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class MinHashTests
{
    [Fact]
    public void Sketch_ProducesFixedSizeSignature()
    {
        var sketcher = new MinHashSketcher(signatureSize: 128);
        var shingles = Enumerable.Range(0, 50).Select(i => (ulong)i).ToArray();

        var sig = sketcher.Sketch(shingles);

        sig.Length.Should().Be(128);
    }

    [Fact]
    public void Jaccard_OfIdenticalSignatures_IsOne()
    {
        var sketcher = new MinHashSketcher(signatureSize: 128);
        var shingles = Enumerable.Range(0, 100).Select(i => (ulong)i).ToArray();

        var a = sketcher.Sketch(shingles);
        var b = sketcher.Sketch(shingles);

        JaccardEstimator.Estimate(a, b).Should().Be(1.0);
    }

    [Fact]
    public void Jaccard_OfDisjointSets_IsApproximatelyZero()
    {
        var sketcher = new MinHashSketcher(signatureSize: 128);
        var a = sketcher.Sketch(Enumerable.Range(0, 200).Select(i => (ulong)i).ToArray());
        var b = sketcher.Sketch(Enumerable.Range(10_000, 200).Select(i => (ulong)i).ToArray());

        JaccardEstimator.Estimate(a, b).Should().BeLessThan(0.1);
    }

    [Fact]
    public void Jaccard_OfHalfOverlap_IsApproximatelyHalf()
    {
        var sketcher = new MinHashSketcher(signatureSize: 128);
        var a = sketcher.Sketch(Enumerable.Range(0, 200).Select(i => (ulong)i).ToArray());
        var b = sketcher.Sketch(Enumerable.Range(100, 200).Select(i => (ulong)i).ToArray());

        var j = JaccardEstimator.Estimate(a, b);
        j.Should().BeInRange(0.25, 0.45);
    }
}
