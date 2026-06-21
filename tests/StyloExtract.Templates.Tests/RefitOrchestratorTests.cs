using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class RefitOrchestratorTests
{
    [Fact]
    public async Task MaybeRefitAsync_HighDriftAndOverObsThreshold_BumpsVersion()
    {
        var cs = $"Data Source=file:testdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        using var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);

        // Seed a template at version 1 with pre-accumulated drift near threshold.
        // With EWMA alpha=0.2 and a single call delta=1.0:
        //   accumulated = 0.2 * 1.0 + 0.8 * oldScore
        // To exceed threshold 0.35 in one call: 0.2 + 0.8 * oldScore >= 0.35 => oldScore >= 0.1875
        var fp = NewFingerprint(1);
        var extractor = SeedExtractor(initialDriftScore: 0.30); // 0.2*1.0 + 0.8*0.30 = 0.44 > 0.35
        var id = await index.RegisterAsync(new byte[16], fp, extractor, default);

        // Simulate enough observations to be "stable"
        for (int i = 0; i < 6; i++)
        {
            await index.RecordObservationAsync(id, fp, 1.0, default);
        }

        var orch = new RefitOrchestrator(index, new ExtractorInducer(),
            driftRefitThreshold: 0.35, observationsBeforeStable: 5, versionHistoryDepth: 3);

        // Simulate massive drift via blocks that don't match any cached rule
        var freshBlocks = new[]
        {
            new ExtractedBlock { Id = "b", Role = BlockRole.PrimaryNavigation, Confidence = 0.8, Text = "", Markdown = "", XPath = "/html/body/nav", CssSelector = "html > body > nav", TextLength = 100, LinkDensity = 0.9, Links = Array.Empty<ExtractedLink>() }
        };

        var result = await orch.MaybeRefitAsync(id, fp, freshBlocks, default);

        result.Refitted.Should().BeTrue();
        result.OldVersion.Should().Be(1);
        result.NewVersion.Should().Be(2);

        (await index.GetTemplateVersionAsync(id, default)).Should().Be(2);
    }

    [Fact]
    public async Task MaybeRefitAsync_DriftScoreEwma_AccumulatesMonotonicallyAndCrossesThreshold()
    {
        // Spec §7: DriftScore is EWMA over per-obs deltas.
        // With alpha=0.2 and persistent delta=1.0 each call, the accumulated score
        // converges to 1.0. Verify it rises monotonically and eventually crosses threshold.
        var cs = $"Data Source=file:testdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        using var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);

        var fp = NewFingerprint(42);
        // Start with zero accumulated drift.
        var extractor = SeedExtractor(initialDriftScore: 0.0);
        var id = await index.RegisterAsync(new byte[16], fp, extractor, default);

        // Simulate enough observations to be "stable"
        for (int i = 0; i < 6; i++)
        {
            await index.RecordObservationAsync(id, fp, 1.0, default);
        }

        var orch = new RefitOrchestrator(index, new ExtractorInducer(),
            driftRefitThreshold: 0.35, observationsBeforeStable: 5, versionHistoryDepth: 3);

        // Blocks with unmatched roles produce delta = 1.0 (unmatched role = complete drift).
        var driftBlocks = new[]
        {
            new ExtractedBlock { Id = "b0", Role = BlockRole.PrimaryNavigation, Confidence = 0.8, Text = "", Markdown = "", XPath = "/nav", CssSelector = "nav", TextLength = 100, LinkDensity = 0.9, Links = Array.Empty<ExtractedLink>() }
        };

        double prevScore = 0;
        int callCount = 0;
        RefitResult result;

        // Call until refit fires; verify monotonic rise.
        do
        {
            var current = await index.GetExtractorAsync(id, default);
            current.Should().NotBeNull();
            double currentScore = current!.Centroid.OverallDriftScore;
            currentScore.Should().BeGreaterThanOrEqualTo(prevScore, "accumulated drift must be non-decreasing");
            prevScore = currentScore;

            result = await orch.MaybeRefitAsync(id, fp, driftBlocks, default);
            callCount++;
        } while (!result.Refitted && callCount < 20);

        result.Refitted.Should().BeTrue("accumulated EWMA drift should eventually cross the 0.35 threshold");
        callCount.Should().BeLessThan(20, "EWMA should converge fast enough to cross 0.35 in a reasonable number of observations");
    }

    private static StructuralFingerprint NewFingerprint(uint seed)
    {
        var sig = new uint[128]; Array.Fill(sig, seed);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig, AnchorMinHash = sig, LshBands = new ulong[16],
            PqGramCounts = new Dictionary<string, double>(), PqGramNorm = 0, ShingleCount = 1, Hex = ""
        };
    }

    private static LearnedExtractor SeedExtractor(double initialDriftScore = 0.0) => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.9, ObservationCount = 6, DriftScore = 0 }
        },
        Centroid = new ExtractorCentroidState
        {
            TotalObservations = 6,
            ByRole = new Dictionary<BlockRole, RoleCentroid>(),
            OverallDriftScore = initialDriftScore,
            LastObservation = DateTimeOffset.UtcNow
        }
    };
}
