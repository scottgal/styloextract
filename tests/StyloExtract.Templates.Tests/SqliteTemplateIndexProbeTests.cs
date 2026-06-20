using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class SqliteTemplateIndexProbeTests
{
    private static SqliteConnection NewConn()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        SqliteSchema.EnsureCreated(c);
        return c;
    }

    private static StructuralFingerprint Fp(uint seed)
    {
        var sig = new uint[128];
        Array.Fill(sig, seed);
        var bands = new ulong[16];
        Array.Fill(bands, (ulong)seed * 31);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig,
            AnchorMinHash = sig,
            LshBands = bands,
            PqGramCounts = new Dictionary<string, double> { [$"k-{seed}"] = 1 },
            PqGramNorm = 1,
            ShingleCount = 1,
            Hex = seed.ToString("X8")
        };
    }

    private static LearnedExtractor Ex(Guid? id = null) => new()
    {
        TemplateId = id ?? Guid.NewGuid(),
        Version = 1,
        Rules = Array.Empty<BlockRule>(),
        Centroid = new ExtractorCentroidState { TotalObservations = 1, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
    };

    [Fact]
    public async Task ProbeFastPath_HitsRegisteredTemplate()
    {
        using var conn = NewConn();
        var idx = new SqliteTemplateIndex(conn);
        var host = new byte[16];
        var fp = Fp(42);
        var id = await idx.RegisterAsync(host, fp, Ex(), default);

        var hit = await idx.ProbeFastPathAsync(host, fp, 0.85, default);

        hit.Should().Be(id);
    }

    [Fact]
    public async Task ProbeFastPath_DifferentBands_ReturnsNull()
    {
        using var conn = NewConn();
        var idx = new SqliteTemplateIndex(conn);
        var host = new byte[16];
        await idx.RegisterAsync(host, Fp(1), Ex(), default);

        var hit = await idx.ProbeFastPathAsync(host, Fp(999), 0.85, default);

        hit.Should().BeNull();
    }

    [Fact]
    public async Task ProbeSlowPath_HitsOnPerfectCosine()
    {
        using var conn = NewConn();
        var idx = new SqliteTemplateIndex(conn);
        var host = new byte[16];
        var fp = Fp(7);
        var id = await idx.RegisterAsync(host, fp, Ex(), default);

        var hit = await idx.ProbeSlowPathAsync(host, fp, 0.75, default);

        hit.Should().NotBeNull();
        hit!.Value.TemplateId.Should().Be(id);
        hit.Value.Cosine.Should().BeGreaterThan(0.95);
    }
}
