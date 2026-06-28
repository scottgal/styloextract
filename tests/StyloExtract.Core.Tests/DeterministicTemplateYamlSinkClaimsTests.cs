using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core.OperatorTemplates;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Direct tests for <see cref="DeterministicTemplateYamlSink"/>. Persistence
/// through <see cref="LayoutExtractor"/> is covered separately in
/// <see cref="DeterministicTemplateYamlPersistenceTests"/>; these tests
/// exercise the <see cref="LearnedExtractor"/> → YAML projection in isolation
/// so the claim-chain pass-through is pinned without the heuristic-induction
/// detour.
/// </summary>
public class DeterministicTemplateYamlSinkClaimsTests
{
    private static IdentityClaim TagOnly(string tag) => new()
    {
        Tag = tag,
        TagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag)),
    };

    private static IdentityClaim WithId(string tag, string id) => new()
    {
        Tag = tag,
        TagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag)),
        Id = id,
        IdHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(id)),
    };

    private static LearnedExtractor BuildExtractor(IReadOnlyList<IdentityClaim>? claims)
    {
        return new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new List<BlockRule>
            {
                new()
                {
                    RuleId = "h0001",
                    Role = BlockRole.MainContent,
                    CssSelectors = new List<string> { "body > div > main" },
                    MeanConfidence = 0.92,
                    ObservationCount = 1,
                    DriftScore = 0,
                    Claims = claims,
                },
            },
            Centroid = new ExtractorCentroidState
            {
                TotalObservations = 1,
                ByRole = new Dictionary<BlockRole, RoleCentroid>(),
                OverallDriftScore = 0,
                LastObservation = DateTimeOffset.UtcNow,
            },
        };
    }

    [Fact]
    public void Persist_EmitsChainBlock_WhenBlockRuleCarriesClaims()
    {
        var root = Path.Combine(Path.GetTempPath(), "stylo-det-claims-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sink = new DeterministicTemplateYamlSink(root);
            var ex = BuildExtractor(new List<IdentityClaim>
            {
                TagOnly("body"),
                WithId("main", "main-content"),
            });

            var path = sink.Persist("with-claims.example.com", ex);

            path.Should().NotBeNull();
            var yaml = File.ReadAllText(path!);
            yaml.Should().Contain("chain:",
                "sink must surface the claim chain when the inducer attached one");
            yaml.Should().Contain("id: main-content");

            // Round-trip back through the loader and confirm the chain survives.
            var parsed = YamlOperatorTemplateLoader.Parse(yaml);
            parsed.Rules.Should().HaveCount(1);
            parsed.Rules[0].Claims.Should().NotBeNull();
            parsed.Rules[0].Claims!.Should().HaveCount(2);
            parsed.Rules[0].Claims![1].Id.Should().Be("main-content");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Persist_OmitsChainBlock_WhenBlockRuleHasNullClaims()
    {
        var root = Path.Combine(Path.GetTempPath(), "stylo-det-noclaims-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sink = new DeterministicTemplateYamlSink(root);
            var ex = BuildExtractor(claims: null);

            var path = sink.Persist("no-claims.example.com", ex);

            path.Should().NotBeNull();
            var yaml = File.ReadAllText(path!);
            yaml.Should().NotContain("chain:",
                "sink must not synthesise a chain when the inducer didn't attach one");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}