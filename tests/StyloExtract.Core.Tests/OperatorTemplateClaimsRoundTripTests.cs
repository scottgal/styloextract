using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core.OperatorTemplates;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Pins the additive `chain:` block on operator-template YAML. The deterministic
/// inducer carries a <see cref="BlockRule.Claims"/> chain at induction time; the
/// runtime applicator (post Phase 1 Task 3) runs that chain instead of parsing
/// CSS-string selectors. The YAML side-files have to round-trip the chain so
/// hand-authored operator templates and audit snapshots both end up on the new
/// apply path.
///
/// These tests gate the emit + parse half of that surface; the adapter +
/// sink wire-up lives in their own tests.
/// </summary>
public class OperatorTemplateClaimsRoundTripTests
{
    private static IdentityClaim Claim(
        string tag,
        string? id = null,
        IReadOnlyList<string>? classes = null,
        string? role = null,
        IReadOnlyDictionary<string, string>? data = null,
        IReadOnlyDictionary<string, string>? aria = null)
    {
        var cls = classes ?? Array.Empty<string>();
        var clsHashes = cls.Select(c => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(c))).ToList();
        return new IdentityClaim
        {
            Tag = tag,
            TagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag)),
            Id = id,
            IdHash = id is null ? null : XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(id)),
            Classes = cls,
            ClassHashes = clsHashes,
            Role = role,
            DataAttrs = data ?? new Dictionary<string, string>(),
            AriaAttrs = aria ?? new Dictionary<string, string>(),
        };
    }

    [Fact]
    public void Emit_WithoutClaims_OmitsChainBlock_Preserves_Legacy_Shape()
    {
        var template = new OperatorTemplate
        {
            Host = "example.com",
            Description = "legacy template, no chain",
            Rules = new List<OperatorTemplateRule>
            {
                new()
                {
                    Role = BlockRole.MainContent,
                    Selectors = new List<string> { "main.docs-body" },
                    Confidence = 0.9,
                    Claims = null,
                },
            },
        };
        var yaml = OperatorTemplateYamlEmitter.Emit(template);
        yaml.Should().NotContain("chain:",
            "rules without claims must not emit a chain block so existing operator templates round-trip unchanged");
    }

    [Fact]
    public void Emit_WithClaims_Writes_Chain_Block_Per_Rule()
    {
        var template = new OperatorTemplate
        {
            Host = "www.bbc.co.uk",
            Description = "induced",
            Rules = new List<OperatorTemplateRule>
            {
                new()
                {
                    Role = BlockRole.MainContent,
                    Selectors = new List<string> { "body > div > main" },
                    Confidence = 0.92,
                    Claims = new List<IdentityClaim>
                    {
                        Claim("body"),
                        Claim("div", classes: new[] { "page" }),
                        Claim("main", id: "main-content"),
                    },
                },
            },
        };
        var yaml = OperatorTemplateYamlEmitter.Emit(template);
        yaml.Should().Contain("chain:",
            "rules with claims must emit a chain block");
        yaml.Should().Contain("tag: body");
        yaml.Should().Contain("tag: main");
        yaml.Should().Contain("id: main-content");
        yaml.Should().Contain("- page", "single-class chain hop should surface the class name");
    }

    [Fact]
    public void Parse_Then_Emit_RoundTrips_Claim_Chain_Exactly()
    {
        var template = new OperatorTemplate
        {
            Host = "www.bbc.co.uk",
            Rules = new List<OperatorTemplateRule>
            {
                new()
                {
                    Role = BlockRole.MainContent,
                    Selectors = new List<string> { "body > div > main" },
                    Confidence = 0.92,
                    Claims = new List<IdentityClaim>
                    {
                        Claim("body"),
                        Claim("div", classes: new[] { "page", "container" }),
                        Claim("main", id: "main-content", role: "main"),
                    },
                },
            },
        };
        var yaml = OperatorTemplateYamlEmitter.Emit(template);
        var parsed = YamlOperatorTemplateLoader.Parse(yaml);

        parsed.Rules.Should().HaveCount(1);
        parsed.Rules[0].Claims.Should().NotBeNull();
        parsed.Rules[0].Claims!.Should().HaveCount(3);

        var leaf = parsed.Rules[0].Claims![2];
        leaf.Tag.Should().Be("main");
        leaf.Id.Should().Be("main-content");
        leaf.Role.Should().Be("main");

        var middle = parsed.Rules[0].Claims![1];
        middle.Tag.Should().Be("div");
        middle.Classes.Should().BeEquivalentTo(new[] { "page", "container" });
    }

    [Fact]
    public void Parse_Recomputes_Hashes_From_String_Fields()
    {
        // The hot-path applicator never re-hashes per element; it expects the
        // claim's TagHash / IdHash / ClassHashes to be populated. Loaded
        // templates must therefore recompute them from the YAML strings or
        // the apply path silently mismatches.
        var template = new OperatorTemplate
        {
            Host = "h.example.com",
            Rules = new List<OperatorTemplateRule>
            {
                new()
                {
                    Role = BlockRole.MainContent,
                    Selectors = new List<string> { "article" },
                    Confidence = 1.0,
                    Claims = new List<IdentityClaim>
                    {
                        Claim("article", id: "post-body", classes: new[] { "prose" }),
                    },
                },
            },
        };

        var yaml = OperatorTemplateYamlEmitter.Emit(template);
        var parsed = YamlOperatorTemplateLoader.Parse(yaml);

        var leaf = parsed.Rules[0].Claims![0];
        leaf.TagHash.Should().Be(XxHash3.HashToUInt64("article"u8));
        leaf.IdHash.Should().Be(XxHash3.HashToUInt64("post-body"u8));
        leaf.ClassHashes.Should().HaveCount(1);
        leaf.ClassHashes[0].Should().Be(XxHash3.HashToUInt64("prose"u8));
    }

    [Fact]
    public void Parse_Existing_Yaml_Without_Chain_Returns_Null_Claims()
    {
        // Existing operator templates that pre-date the chain emit must keep
        // parsing cleanly. Their rules end up with Claims == null so the
        // adapter falls back to the CSS-string applicator.
        const string yaml = """
            host: legacy.example.com
            rules:
              - role: MainContent
                selectors:
                  - main.docs-body
                confidence: 0.95
            """;
        var t = YamlOperatorTemplateLoader.Parse(yaml);
        t.Rules[0].Claims.Should().BeNull(
            "back-compat: rules without a chain block load with null Claims");
    }
}