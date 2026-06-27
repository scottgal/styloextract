using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using StyloExtract.Abstractions;
using Xunit;

namespace StyloExtract.Abstractions.Tests;

public class IdentityClaimMatcherTests
{
    private static ulong H(string s) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(s));

    private static ElementClaimSet BuildElement(
        string tag = "div",
        string? id = null,
        IReadOnlyList<string>? classes = null,
        IReadOnlyDictionary<string, string>? dataAttrs = null,
        IReadOnlyDictionary<string, string>? ariaAttrs = null,
        string? role = null)
    {
        var classList = classes ?? Array.Empty<string>();
        return new ElementClaimSet
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

    private static IdentityClaim BuildClaim(
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
    public void EmptyClaim_MatchesAnyElement_OfSameTag()
    {
        var element = BuildElement(tag: "article", id: "post-1", classes: ["foo", "bar"]);
        var claim = BuildClaim(tag: "article");

        IdentityClaimMatcher.Matches(element, claim).Should().BeTrue();
    }

    [Fact]
    public void TagMismatch_ReturnsFalse()
    {
        var element = BuildElement(tag: "div");
        var claim = BuildClaim(tag: "article");

        IdentityClaimMatcher.Matches(element, claim).Should().BeFalse();
    }

    [Fact]
    public void IdClaim_MatchesWhenIdEqual()
    {
        var element = BuildElement(tag: "main", id: "content");
        var claim = BuildClaim(tag: "main", id: "content");

        IdentityClaimMatcher.Matches(element, claim).Should().BeTrue();
    }

    [Fact]
    public void IdClaim_FailsWhenIdMismatch()
    {
        var element = BuildElement(tag: "main", id: "sidebar");
        var claim = BuildClaim(tag: "main", id: "content");

        IdentityClaimMatcher.Matches(element, claim).Should().BeFalse();
    }

    [Fact]
    public void IdClaim_FailsWhenElementHasNoId()
    {
        var element = BuildElement(tag: "main");
        var claim = BuildClaim(tag: "main", id: "content");

        IdentityClaimMatcher.Matches(element, claim).Should().BeFalse();
    }

    [Fact]
    public void ClassClaim_IsOrderInsensitive()
    {
        var element = BuildElement(tag: "div", classes: ["beta", "gamma", "alpha"]);
        var claim = BuildClaim(tag: "div", classes: ["alpha", "beta"]);

        IdentityClaimMatcher.Matches(element, claim).Should().BeTrue();
    }

    [Fact]
    public void ClassClaim_AllRequired_FailsWhenOneMissing()
    {
        var element = BuildElement(tag: "div", classes: ["alpha"]);
        var claim = BuildClaim(tag: "div", classes: ["alpha", "beta"]);

        IdentityClaimMatcher.Matches(element, claim).Should().BeFalse();
    }

    [Fact]
    public void DataAttrs_AllRequired_FailsWhenOneMissing()
    {
        var element = BuildElement(
            tag: "div",
            dataAttrs: new Dictionary<string, string> { ["role"] = "post" });
        var claim = BuildClaim(
            tag: "div",
            dataAttrs: new Dictionary<string, string>
            {
                ["role"] = "post",
                ["id"] = "42",
            });

        IdentityClaimMatcher.Matches(element, claim).Should().BeFalse();
    }

    [Fact]
    public void DataAttrs_AllRequired_PassesWhenAllPresent()
    {
        var attrs = new Dictionary<string, string>
        {
            ["role"] = "post",
            ["id"] = "42",
        };
        var element = BuildElement(tag: "div", dataAttrs: attrs);
        var claim = BuildClaim(tag: "div", dataAttrs: attrs);

        IdentityClaimMatcher.Matches(element, claim).Should().BeTrue();
    }

    [Fact]
    public void DataAttrs_ValueMismatch_ReturnsFalse()
    {
        var element = BuildElement(
            tag: "div",
            dataAttrs: new Dictionary<string, string> { ["role"] = "post" });
        var claim = BuildClaim(
            tag: "div",
            dataAttrs: new Dictionary<string, string> { ["role"] = "comment" });

        IdentityClaimMatcher.Matches(element, claim).Should().BeFalse();
    }

    [Fact]
    public void AriaAttrs_AllRequired_FailsWhenOneMissing()
    {
        var element = BuildElement(
            tag: "button",
            ariaAttrs: new Dictionary<string, string> { ["label"] = "Submit" });
        var claim = BuildClaim(
            tag: "button",
            ariaAttrs: new Dictionary<string, string>
            {
                ["label"] = "Submit",
                ["pressed"] = "false",
            });

        IdentityClaimMatcher.Matches(element, claim).Should().BeFalse();
    }

    [Fact]
    public void Role_MatchesCaseInsensitively()
    {
        var element = BuildElement(tag: "div", role: "BUTTON");
        var claim = BuildClaim(tag: "div", role: "button");

        IdentityClaimMatcher.Matches(element, claim).Should().BeTrue();
    }

    [Fact]
    public void Role_FailsWhenElementHasNoRole()
    {
        var element = BuildElement(tag: "div");
        var claim = BuildClaim(tag: "div", role: "button");

        IdentityClaimMatcher.Matches(element, claim).Should().BeFalse();
    }

    [Fact]
    public void Conjunction_TagIdClassData_AllChecked_Passes()
    {
        var element = BuildElement(
            tag: "article",
            id: "post-1",
            classes: ["article", "featured"],
            dataAttrs: new Dictionary<string, string> { ["category"] = "news" });
        var claim = BuildClaim(
            tag: "article",
            id: "post-1",
            classes: ["article", "featured"],
            dataAttrs: new Dictionary<string, string> { ["category"] = "news" });

        IdentityClaimMatcher.Matches(element, claim).Should().BeTrue();
    }

    [Fact]
    public void Conjunction_OneFieldMismatch_Fails()
    {
        var element = BuildElement(
            tag: "article",
            id: "post-1",
            classes: ["article", "featured"],
            dataAttrs: new Dictionary<string, string> { ["category"] = "sports" });
        var claim = BuildClaim(
            tag: "article",
            id: "post-1",
            classes: ["article", "featured"],
            dataAttrs: new Dictionary<string, string> { ["category"] = "news" });

        IdentityClaimMatcher.Matches(element, claim).Should().BeFalse();
    }
}
