using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class ExtractorApplicatorTests
{
    private static ulong H(string s) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(s));

    private static IdentityClaim TagOnly(string tag) => new()
    {
        Tag = tag, TagHash = H(tag),
    };

    private static IdentityClaim TagId(string tag, string id) => new()
    {
        Tag = tag, TagHash = H(tag), Id = id, IdHash = H(id),
    };

    [Fact]
    public void Apply_EmitsBlocksMatchingRuleSelectors()
    {
        IExtractorApplicator applicator = new ExtractorApplicator();
        var extractor = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new[]
            {
                new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.92, ObservationCount = 5, DriftScore = 0 },
                new BlockRule { RuleId = "r1", Role = BlockRole.Footer, CssSelectors = new[] { "footer" }, MeanConfidence = 0.88, ObservationCount = 5, DriftScore = 0 }
            },
            Centroid = new ExtractorCentroidState { TotalObservations = 5, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
        };
        var doc = new AngleSharpHtmlDomParser().Parse("<html><body><main><article>x</article></main><footer>©</footer></body></html>");

        var result = applicator.Apply(doc, extractor);

        result.Blocks.Should().HaveCount(2);
        result.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent && b.Confidence == 0.92);
        result.Blocks.Should().Contain(b => b.Role == BlockRole.Footer && b.Confidence == 0.88);
        result.RulesApplied.Should().Be(2);
        result.RulesMissed.Should().Be(0);
    }

    [Fact]
    public void Apply_PopulatesMarkdown_OnContentRoles()
    {
        IExtractorApplicator applicator = new ExtractorApplicator();
        var extractor = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new[]
            {
                new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.92, ObservationCount = 5, DriftScore = 0 },
            },
            Centroid = new ExtractorCentroidState { TotalObservations = 5, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
        };
        var doc = new AngleSharpHtmlDomParser().Parse(
            "<html><body><main><article><h1>Top</h1><p>See <a href=\"/x\">link</a> for info.</p></article></main></body></html>");

        var result = applicator.Apply(doc, extractor);
        var main = result.Blocks.Single(b => b.Role == BlockRole.MainContent);
        main.Markdown.Should().NotBeNullOrEmpty();
        main.Markdown.Should().Contain("# Top");
        main.Markdown.Should().Contain("[link](/x)");
    }

    [Fact]
    public void Apply_LeavesMarkdownEmpty_OnNonContentRoles()
    {
        IExtractorApplicator applicator = new ExtractorApplicator();
        var extractor = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new[]
            {
                new BlockRule { RuleId = "r0", Role = BlockRole.PrimaryNavigation, CssSelectors = new[] { "nav" }, MeanConfidence = 0.85, ObservationCount = 5, DriftScore = 0 },
                new BlockRule { RuleId = "r1", Role = BlockRole.Footer, CssSelectors = new[] { "footer" }, MeanConfidence = 0.85, ObservationCount = 5, DriftScore = 0 },
                new BlockRule { RuleId = "r2", Role = BlockRole.Boilerplate, CssSelectors = new[] { "aside" }, MeanConfidence = 0.85, ObservationCount = 5, DriftScore = 0 },
            },
            Centroid = new ExtractorCentroidState { TotalObservations = 5, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
        };
        var doc = new AngleSharpHtmlDomParser().Parse(
            "<html><body><nav><a href='/'>Home</a></nav><aside>related</aside><footer>©</footer></body></html>");

        var result = applicator.Apply(doc, extractor);
        result.Blocks.Should().OnlyContain(b => b.Markdown == "");
    }

    [Fact]
    public void Apply_PrefersClaimsOverCssSelectors_WhenBothPresent()
    {
        // Both a claim chain and a CSS-string selector that would pick the
        // same element. The claim path is the one we want to exercise; assert
        // by giving the CSS string a deliberately bogus value that would
        // throw if the CSS path were taken (an unparseable selector logs and
        // falls through to a miss). If the claim path runs, we still match.
        IExtractorApplicator applicator = new ExtractorApplicator();
        var rule = new BlockRule
        {
            RuleId = "r0",
            Role = BlockRole.MainContent,
            CssSelectors = new[] { "this is not a valid css selector >>>" },
            Claims = new[] { TagId("main", "content") },
            MeanConfidence = 0.9,
            ObservationCount = 1,
            DriftScore = 0,
        };
        var extractor = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new[] { rule },
            Centroid = new ExtractorCentroidState
            {
                TotalObservations = 1,
                ByRole = new Dictionary<BlockRole, RoleCentroid>(),
                OverallDriftScore = 0,
                LastObservation = DateTimeOffset.UtcNow,
            },
        };
        var doc = new AngleSharpHtmlDomParser().Parse(
            "<html><body><main id='content'><p>" + new string('x', 200) + "</p></main></body></html>");

        var result = applicator.Apply(doc, extractor);

        // The rule matched via the claim path; the bogus CSS string was
        // ignored. We get exactly one MainContent block back.
        result.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent);
        result.RulesApplied.Should().Be(1);
        result.RulesMissed.Should().Be(0);
    }

    [Fact]
    public void Apply_LegacyRule_WithNullClaims_FallsBackToCssPath()
    {
        // Pre-Task-2 templates lack Claims entirely. Behaviour must be
        // identical to the original CSS-string evaluator.
        IExtractorApplicator applicator = new ExtractorApplicator();
        var extractor = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new[]
            {
                new BlockRule
                {
                    RuleId = "r0",
                    Role = BlockRole.MainContent,
                    CssSelectors = new[] { "main > article" },
                    Claims = null,
                    MeanConfidence = 0.9,
                    ObservationCount = 1,
                    DriftScore = 0,
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
        var doc = new AngleSharpHtmlDomParser().Parse(
            "<html><body><main><article>legacy</article></main></body></html>");

        var result = applicator.Apply(doc, extractor);

        result.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent && b.Text == "legacy");
        result.RulesApplied.Should().Be(1);
    }

    [Fact]
    public void Apply_ClaimAndCssPaths_ProduceEquivalentResults_OnCongruentFixture()
    {
        // Two extractors over the same document. One uses Claims, the other
        // uses the equivalent CSS string. Their output blocks should match
        // 1-to-1 (modulo block ids which are positional).
        var doc = new AngleSharpHtmlDomParser().Parse(
            "<html><body><main id='content'><article>same</article></main></body></html>");

        var claimRule = new BlockRule
        {
            RuleId = "r0",
            Role = BlockRole.MainContent,
            CssSelectors = new[] { "main#content > article" },
            Claims = new[] { TagId("main", "content"), TagOnly("article") },
            MeanConfidence = 0.9,
            ObservationCount = 1,
            DriftScore = 0,
        };
        var cssRule = claimRule with { RuleId = "r1", Claims = null };

        IExtractorApplicator applicator = new ExtractorApplicator();
        var centroid = new ExtractorCentroidState
        {
            TotalObservations = 1,
            ByRole = new Dictionary<BlockRole, RoleCentroid>(),
            OverallDriftScore = 0,
            LastObservation = DateTimeOffset.UtcNow,
        };

        var claimResult = applicator.Apply(doc, new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(), Version = 1,
            Rules = new[] { claimRule }, Centroid = centroid,
        });
        var cssResult = applicator.Apply(doc, new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(), Version = 1,
            Rules = new[] { cssRule }, Centroid = centroid,
        });

        claimResult.Blocks.Where(b => b.Role == BlockRole.MainContent)
            .Select(b => b.Text)
            .Should().BeEquivalentTo(
                cssResult.Blocks.Where(b => b.Role == BlockRole.MainContent).Select(b => b.Text));
    }
}
