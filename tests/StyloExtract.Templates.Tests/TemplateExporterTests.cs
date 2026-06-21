using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class TemplateExporterTests
{
    [Fact]
    public async Task ExportHostAsync_ProducesSchemaV1Json()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var idx = new SqliteTemplateIndex(conn);
        var host = new byte[16];
        var fp = NewFp();
        var ex = NewEx();
        await idx.RegisterAsync(host, fp, ex, default);

        using var ms = new MemoryStream();
        await TemplateExporter.ExportHostAsync(conn, host, "example.com", ms, default);

        var json = JsonDocument.Parse(ms.ToArray());
        json.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("host").GetProperty("displayName").GetString().Should().Be("example.com");
        json.RootElement.GetProperty("templates").GetArrayLength().Should().Be(1);
    }

    private static StructuralFingerprint NewFp()
    {
        var sig = new uint[128]; Array.Fill(sig, (uint)9);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig, AnchorMinHash = sig, LshBands = new ulong[16],
            PqGramCounts = new Dictionary<string, double> { ["k"] = 1 }, PqGramNorm = 1, ShingleCount = 1, Hex = "0"
        };
    }

    private static LearnedExtractor NewEx() => new()
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
