using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class SqliteTemplateIndexRegisterTests
{
    private static SqliteConnection NewConn()
    {
        var cs = $"Data Source=file:testdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var c = new SqliteConnection(cs);
        c.Open();
        SqliteSchema.EnsureCreated(c);
        return c;
    }

    [Fact]
    public async Task Register_PersistsTemplateAndExtractor()
    {
        using var conn = NewConn();
        var idx = new SqliteTemplateIndex(conn);
        var fp = NewFingerprint();
        var ex = NewExtractor();
        var hostHash = new byte[16];

        var id = await idx.RegisterAsync(hostHash, fp, ex, default);

        var loaded = await idx.GetExtractorAsync(id, default);
        loaded.Should().NotBeNull();
        loaded!.Rules.Should().HaveCount(1);
        (await idx.GetObservationCountAsync(id, default)).Should().Be(1);
        (await idx.GetTemplateVersionAsync(id, default)).Should().Be(1);
    }

    private static StructuralFingerprint NewFingerprint()
    {
        var sig = new uint[128];
        return new StructuralFingerprint
        {
            StructuralMinHash = sig,
            AnchorMinHash = sig,
            LshBands = new ulong[16],
            PqGramCounts = new Dictionary<string, double> { ["x"] = 1 },
            PqGramNorm = 1,
            ShingleCount = 1,
            Hex = "00000000"
        };
    }

    private static LearnedExtractor NewExtractor() => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule { RuleId = "r1", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.9, ObservationCount = 1, DriftScore = 0 }
        },
        Centroid = new ExtractorCentroidState
        {
            TotalObservations = 1,
            ByRole = new Dictionary<BlockRole, RoleCentroid>(),
            OverallDriftScore = 0,
            LastObservation = DateTimeOffset.UtcNow
        }
    };
}
