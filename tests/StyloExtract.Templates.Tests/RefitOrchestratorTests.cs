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
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(conn);

        // Seed a template at version 1
        var fp = NewFingerprint(1);
        var extractor = SeedExtractor();
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

    private static StructuralFingerprint NewFingerprint(uint seed)
    {
        var sig = new uint[128]; Array.Fill(sig, seed);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig, AnchorMinHash = sig, LshBands = new ulong[16],
            PqGramCounts = new Dictionary<string, double>(), PqGramNorm = 0, ShingleCount = 1, Hex = ""
        };
    }

    private static LearnedExtractor SeedExtractor() => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.9, ObservationCount = 6, DriftScore = 0 }
        },
        Centroid = new ExtractorCentroidState { TotalObservations = 6, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
    };
}
