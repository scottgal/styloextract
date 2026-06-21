using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class TemplateImporterTests
{
    [Fact]
    public async Task ImportAsync_RoundTrip_RegistersTemplates()
    {
        using var src = NewConn();
        var idxSrc = new SqliteTemplateIndex(src);
        var host = new byte[16];
        await idxSrc.RegisterAsync(host, FakeFp(), FakeEx(), default);

        using var exportStream = new MemoryStream();
        await TemplateExporter.ExportHostAsync(src, host, "example.com", exportStream, default);
        exportStream.Position = 0;

        using var dst = NewConn();
        var result = await TemplateImporter.ImportAsync(dst, host, exportStream, default);

        result.ImportedCount.Should().Be(1);
        await using var cmd = dst.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM templates";
        ((long?)await cmd.ExecuteScalarAsync(default)).Should().Be(1);
    }

    private static SqliteConnection NewConn()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        SqliteSchema.EnsureCreated(c);
        return c;
    }

    private static StructuralFingerprint FakeFp()
    {
        var sig = new uint[128]; Array.Fill(sig, 11u);
        var bands = new ulong[16]; Array.Fill(bands, 11UL * 7);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig, AnchorMinHash = sig, LshBands = bands,
            PqGramCounts = new Dictionary<string, double> { ["k"] = 1 }, PqGramNorm = 1, ShingleCount = 1, Hex = "0"
        };
    }

    private static LearnedExtractor FakeEx() => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main" }, MeanConfidence = 0.9, ObservationCount = 1, DriftScore = 0 }
        },
        Centroid = new ExtractorCentroidState { TotalObservations = 1, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
    };
}
