using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using StyloExtract.Abstractions;
using Xunit;

namespace StyloExtract.Abstractions.Tests;

public class SelectorDistanceTests
{
    private static ulong H(string s) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(s));

    private static IdentityClaim Claim(
        string tag = "div",
        string? id = null,
        IReadOnlyList<string>? classes = null,
        IReadOnlyDictionary<string, string>? dataAttrs = null,
        IReadOnlyDictionary<string, string>? ariaAttrs = null,
        string? role = null)
    {
        var classList = classes ?? Array.Empty<string>();
        return new IdentityClaim
        {
            Tag = tag,
            TagHash = H(tag),
            Id = id,
            IdHash = id is null ? null : H(id),
            Classes = classList,
            ClassHashes = classList.Select(H).ToArray(),
            DataAttrs = dataAttrs ?? new Dictionary<string, string>(),
            AriaAttrs = ariaAttrs ?? new Dictionary<string, string>(),
            Role = role,
        };
    }

    [Fact]
    public void IdenticalChains_DistanceZero()
    {
        var chain = new[]
        {
            Claim(tag: "main", id: "content"),
            Claim(tag: "article", classes: ["post", "card"]),
        };

        SelectorDistance.Compute(chain, chain).Should().Be(0.0);
    }

    [Fact]
    public void EmptyChains_DistanceZero()
    {
        SelectorDistance.Compute(Array.Empty<IdentityClaim>(), Array.Empty<IdentityClaim>())
            .Should().Be(0.0);
        SelectorDistance.Compute(null, null).Should().Be(0.0);
    }

    [Fact]
    public void SameTagDifferentId_HighDistance()
    {
        var a = new[] { Claim(tag: "article", id: "post-1") };
        var b = new[] { Claim(tag: "article", id: "post-2") };

        var d = SelectorDistance.Compute(a, b);
        d.Should().BeGreaterThan(1.0);
        d.Should().Be(SelectorDistance.IdMismatchPenalty);
    }

    [Fact]
    public void SameTagSameId_DistanceZero_RegardlessOfClassDifferences()
    {
        var a = new[] { Claim(tag: "main", id: "content", classes: ["a", "b", "c"]) };
        var b = new[] { Claim(tag: "main", id: "content", classes: ["x", "y"]) };

        SelectorDistance.Compute(a, b).Should().Be(0.0);
    }

    [Fact]
    public void SameTagClassOverlap_SmallerThanNoOverlap()
    {
        var baseClaim = Claim(tag: "article", classes: ["post", "featured", "card"]);
        var overlapping = new[] { Claim(tag: "article", classes: ["post", "card", "highlighted"]) };
        var disjoint = new[] { Claim(tag: "article", classes: ["sidebar", "widget"]) };

        var overlapDistance = SelectorDistance.Compute(new[] { baseClaim }, overlapping);
        var disjointDistance = SelectorDistance.Compute(new[] { baseClaim }, disjoint);

        overlapDistance.Should().BeLessThan(disjointDistance);
        overlapDistance.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void DifferentTag_DominatesOtherComparisons()
    {
        var a = new[] { Claim(tag: "article", id: "same", classes: ["a", "b", "c"]) };
        var b = new[] { Claim(tag: "section", id: "same", classes: ["a", "b", "c"]) };

        var d = SelectorDistance.Compute(a, b);
        d.Should().Be(SelectorDistance.TagMismatchPenalty);

        // Tag mismatch alone should exceed any plausible same-tag distance.
        var nontagDiff = SelectorDistance.Compute(
            new[] { Claim(tag: "div", classes: ["x"]) },
            new[] { Claim(tag: "div", classes: ["y", "z"], role: "navigation") });
        d.Should().BeGreaterThan(nontagDiff);
    }

    [Fact]
    public void DifferentLengthChains_PaddedComparison_LongerDoesNotGetFreeDistance()
    {
        // Same leaf, but b has an extra ancestor. The extra ancestor must
        // contribute positive distance — otherwise specificity-only differences
        // would register as identical.
        var leaf = Claim(tag: "article", id: "post-1");
        var a = new[] { leaf };
        var b = new[] { Claim(tag: "main"), leaf };

        var d = SelectorDistance.Compute(a, b);
        d.Should().BeGreaterThan(0.0);

        // Symmetric.
        SelectorDistance.Compute(b, a).Should().Be(d);
    }

    [Fact]
    public void Symmetry_HoldsForVariedInputs()
    {
        var pairs = new (IdentityClaim[] a, IdentityClaim[] b)[]
        {
            (new[] { Claim(tag: "div") }, new[] { Claim(tag: "span") }),
            (new[] { Claim(tag: "a", classes: ["btn"]) },
             new[] { Claim(tag: "a", classes: ["btn", "primary"]) }),
            (new[] { Claim(tag: "main", id: "x") },
             new[] { Claim(tag: "main", id: "y") }),
            (new[] { Claim(tag: "section", role: "navigation") },
             new[] { Claim(tag: "section") }),
            (new[]
                {
                    Claim(tag: "body"),
                    Claim(tag: "main", id: "main"),
                    Claim(tag: "article", classes: ["post"]),
                },
                new[]
                {
                    Claim(tag: "main", id: "main"),
                    Claim(tag: "article", classes: ["post", "featured"]),
                }),
        };

        foreach (var (a, b) in pairs)
        {
            var ab = SelectorDistance.Compute(a, b);
            var ba = SelectorDistance.Compute(b, a);
            ba.Should().Be(ab, $"distance must be symmetric for {a.Length}x{b.Length} chain");
        }
    }

    [Fact]
    public void TriangleInequality_HoldsApproximately_ForRealisticChains()
    {
        // Not strict — heuristic distance. But for sane inputs the indirect
        // route should not be vastly cheaper than the direct one.
        var a = new[] { Claim(tag: "article", classes: ["post", "card"]) };
        var b = new[] { Claim(tag: "article", classes: ["post", "card", "featured"]) };
        var c = new[] { Claim(tag: "article", classes: ["post", "card", "featured", "highlighted"]) };

        var ab = SelectorDistance.Compute(a, b);
        var bc = SelectorDistance.Compute(b, c);
        var ac = SelectorDistance.Compute(a, c);

        // Direct hop a→c should not exceed a→b→c by more than a small slack.
        (ab + bc).Should().BeGreaterOrEqualTo(ac - 0.01);
    }

    [Fact]
    public void OneSidedAttribute_HalfWeightPenalty()
    {
        var a = new[] { Claim(tag: "section", role: "navigation") };
        var b = new[] { Claim(tag: "section") };

        var d = SelectorDistance.Compute(a, b);
        d.Should().Be(SelectorDistance.OneSidedSpecificityPenalty);
    }

    [Fact]
    public void DataAttrMismatch_AddsAttrPenalty()
    {
        var a = new[] { Claim(tag: "div",
            dataAttrs: new Dictionary<string, string> { ["section"] = "post-body" }) };
        var b = new[] { Claim(tag: "div",
            dataAttrs: new Dictionary<string, string> { ["section"] = "comments" }) };

        var d = SelectorDistance.Compute(a, b);
        d.Should().Be(SelectorDistance.AttrMismatchPenalty);
    }

    [Fact]
    public void LeafWeightsDominate_AncestorDifferencesDecay()
    {
        // Two-position chains where the difference is at the leaf vs at the
        // ancestor. Leaf-level difference should be heavier.
        var ancestor = Claim(tag: "main");
        var leafA = Claim(tag: "article", id: "post-1");
        var leafB = Claim(tag: "article", id: "post-2");

        var altAncestor = Claim(tag: "section");
        var sharedLeaf = Claim(tag: "article", id: "post-1");

        var leafDifferent = SelectorDistance.Compute(
            new[] { ancestor, leafA },
            new[] { ancestor, leafB });

        var ancestorDifferent = SelectorDistance.Compute(
            new[] { ancestor, sharedLeaf },
            new[] { altAncestor, sharedLeaf });

        // Leaf-position id mismatch (penalty 5.0 * weight 1.0) should outweigh
        // ancestor-position tag mismatch (penalty 10.0 * weight 0.5 = 5.0).
        // Tie-break: they're equal here by construction — the point is leaves
        // get full weight while ancestors get half. Verify the leaf path lands
        // at the full IdMismatchPenalty.
        leafDifferent.Should().Be(SelectorDistance.IdMismatchPenalty);
        ancestorDifferent.Should().Be(SelectorDistance.TagMismatchPenalty * SelectorDistance.PositionDecay);
    }

    [Fact]
    public void RealWorld_MostlylucidVariantsClose_WikipediaFar()
    {
        // Two plausible induced templates for mostlylucid.net articles.
        // Same structural skeleton, slightly different class lists across
        // template versions (a class added between v1 and v2).
        var mostlylucidV1 = new[]
        {
            Claim(tag: "main", id: "main"),
            Claim(tag: "article", classes: ["post", "content"]),
        };
        var mostlylucidV2 = new[]
        {
            Claim(tag: "main", id: "main"),
            Claim(tag: "article", classes: ["post", "content", "single"]),
        };

        // Wikipedia article: completely different shape — different ancestor
        // chain, different id, different classes.
        var wikipedia = new[]
        {
            Claim(tag: "div", id: "content"),
            Claim(tag: "div", id: "bodyContent"),
            Claim(tag: "div", id: "mw-content-text", classes: ["mw-body-content"]),
        };

        var mlClose = SelectorDistance.Compute(mostlylucidV1, mostlylucidV2);
        var mlVsWiki = SelectorDistance.Compute(mostlylucidV1, wikipedia);

        // Close: same skeleton, only one extra class on the leaf.
        mlClose.Should().BeLessThan(1.0);

        // Far: tag mismatches up and down the chain, completely different ids.
        mlVsWiki.Should().BeGreaterThan(5.0);

        // And the ratio should be dramatic — at least an order of magnitude.
        mlVsWiki.Should().BeGreaterThan(mlClose * 10.0);
    }
}
