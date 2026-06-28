using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core.OperatorTemplates;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Pins <see cref="OperatorTemplateAdapter.ToLearnedExtractor"/> on Claims
/// pass-through. Before Phase 1 Task 3 the adapter built <see cref="BlockRule"/>
/// with <c>Claims = null</c> and operator overrides ran on the CSS-string
/// applicator. With the new claim-chain emit on the YAML side, the adapter
/// has to thread Claims into the BlockRule so the IdentityClaimApplicator
/// actually runs.
/// </summary>
public class OperatorTemplateAdapterClaimsTests
{
    private static IdentityClaim TagOnly(string tag) => new()
    {
        Tag = tag,
        TagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag)),
    };

    [Fact]
    public void ToLearnedExtractor_PassesClaimsThrough_WhenRuleCarriesThem()
    {
        var template = new OperatorTemplate
        {
            Host = "with-claims.example.com",
            Rules = new List<OperatorTemplateRule>
            {
                new()
                {
                    Role = BlockRole.MainContent,
                    Selectors = new List<string> { "main" },
                    Confidence = 0.9,
                    Claims = new List<IdentityClaim> { TagOnly("body"), TagOnly("main") },
                },
            },
        };

        var extractor = OperatorTemplateAdapter.ToLearnedExtractor(template);

        extractor.Rules.Should().HaveCount(1);
        extractor.Rules[0].Claims.Should().NotBeNull();
        extractor.Rules[0].Claims!.Should().HaveCount(2);
        extractor.Rules[0].Claims![1].Tag.Should().Be("main");
    }

    [Fact]
    public void ToLearnedExtractor_LeavesClaimsNull_WhenRuleDoesNotCarryThem()
    {
        // Legacy operator templates without a chain block should keep flowing
        // to the CSS-string apply path so existing operator YAMLs don't break.
        var template = new OperatorTemplate
        {
            Host = "legacy.example.com",
            Rules = new List<OperatorTemplateRule>
            {
                new()
                {
                    Role = BlockRole.MainContent,
                    Selectors = new List<string> { "main.docs-body" },
                    Confidence = 1.0,
                    Claims = null,
                },
            },
        };

        var extractor = OperatorTemplateAdapter.ToLearnedExtractor(template);

        extractor.Rules.Should().HaveCount(1);
        extractor.Rules[0].Claims.Should().BeNull();
        extractor.Rules[0].CssSelectors.Should().BeEquivalentTo(new[] { "main.docs-body" });
    }
}