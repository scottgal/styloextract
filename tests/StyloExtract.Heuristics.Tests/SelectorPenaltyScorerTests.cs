using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class SelectorPenaltyScorerTests
{
    private static ulong H(string s) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(s));

    private static ElementClaimSet TagOnly(string tag) => new()
    {
        Tag = tag,
        TagHash = H(tag),
    };

    [Fact]
    public void PickBest_TagOnly_Penalty5()
    {
        var (claim, penalty) = SelectorPenaltyScorer.PickBest(TagOnly("div"));

        penalty.Should().Be(SelectorPenaltyScorer.PenaltyTagOnly).And.Be(5);
        claim.Tag.Should().Be("div");
        claim.Id.Should().BeNull();
        claim.Classes.Should().BeEmpty();
    }

    [Fact]
    public void PickBest_RoleBeatsTag_Penalty2()
    {
        var el = TagOnly("div") with { Role = "navigation" };

        var (claim, penalty) = SelectorPenaltyScorer.PickBest(el);

        penalty.Should().Be(SelectorPenaltyScorer.PenaltyRole).And.Be(2);
        claim.Role.Should().Be("navigation");
    }

    [Fact]
    public void PickBest_AriaBeatsRole_Penalty2()
    {
        var el = TagOnly("button") with
        {
            AriaAttrs = new Dictionary<string, string> { ["label"] = "Submit" },
            Role = "button",
        };

        var (claim, penalty) = SelectorPenaltyScorer.PickBest(el);

        penalty.Should().Be(SelectorPenaltyScorer.PenaltyAttribute).And.Be(2);
        claim.AriaAttrs.Should().ContainKey("label");
    }

    [Fact]
    public void PickBest_DataBeatsAria_Penalty2()
    {
        var el = TagOnly("div") with
        {
            DataAttrs = new Dictionary<string, string> { ["role"] = "post" },
            AriaAttrs = new Dictionary<string, string> { ["label"] = "Post" },
        };

        var (claim, penalty) = SelectorPenaltyScorer.PickBest(el);

        penalty.Should().Be(SelectorPenaltyScorer.PenaltyAttribute);
        claim.DataAttrs.Should().ContainKey("role").WhoseValue.Should().Be("post");
        // The aria branch should not have fired - data-* is preferred at the same penalty tier.
        claim.AriaAttrs.Should().BeEmpty();
    }

    [Fact]
    public void PickBest_ClassBeatsData_Penalty1()
    {
        var el = TagOnly("div") with
        {
            Classes = new[] { "article-body" },
            ClassHashes = new[] { H("article-body") },
            DataAttrs = new Dictionary<string, string> { ["role"] = "post" },
        };

        var (claim, penalty) = SelectorPenaltyScorer.PickBest(el);

        penalty.Should().Be(SelectorPenaltyScorer.PenaltyClass).And.Be(1);
        claim.Classes.Should().BeEquivalentTo(["article-body"]);
        claim.DataAttrs.Should().BeEmpty();
    }

    [Fact]
    public void PickBest_IdBeatsClass_Penalty0()
    {
        var el = TagOnly("div") with
        {
            Id = "content",
            IdHash = H("content"),
            Classes = new[] { "article-body" },
            ClassHashes = new[] { H("article-body") },
        };

        var (claim, penalty) = SelectorPenaltyScorer.PickBest(el);

        penalty.Should().Be(SelectorPenaltyScorer.PenaltyId).And.Be(0);
        claim.Id.Should().Be("content");
        claim.Classes.Should().BeEmpty();
    }

    [Fact]
    public void PickBest_HashShapedIdAlreadyFiltered_FallsBackToNextTier()
    {
        // The stability filter would have nulled the hash-shaped id upstream; here
        // we simulate that by feeding a claim set with no Id but a class present.
        // PickBest must NOT invent an id from thin air; it must drop to the class tier.
        var el = TagOnly("div") with
        {
            Classes = new[] { "post-content" },
            ClassHashes = new[] { H("post-content") },
            // Id and IdHash deliberately omitted (filter rejected the hash-shaped id).
        };

        var (claim, penalty) = SelectorPenaltyScorer.PickBest(el);

        penalty.Should().Be(SelectorPenaltyScorer.PenaltyClass);
        claim.Id.Should().BeNull();
    }

    [Fact]
    public void PickBest_RespectsBestClassOverride()
    {
        // Multiple stable classes - the caller pre-computed that "main-content"
        // is the most-specific by document frequency. PickBest must honour that.
        var el = TagOnly("div") with
        {
            Classes = new[] { "wrapper", "main-content", "padded" },
            ClassHashes = new[] { H("wrapper"), H("main-content"), H("padded") },
        };

        var (claim, _) = SelectorPenaltyScorer.PickBest(el, bestClass: "main-content");

        claim.Classes.Should().BeEquivalentTo(["main-content"]);
        claim.ClassHashes.Should().HaveCount(1);
        claim.ClassHashes[0].Should().Be(H("main-content"));
    }
}
