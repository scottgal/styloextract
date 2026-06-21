using FluentAssertions;
using Microsoft.Data.Sqlite;
using Mostlylucid.Ephemeral;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

/// <summary>
/// Tests for thread-safety via SqliteSingleWriter and ObservationRecorded signal emission.
/// </summary>
public class SqliteTemplateIndexConcurrencyTests
{
    private static (SqliteTemplateIndex Index, string ConnectionString) NewIndex()
    {
        var cs = $"Data Source=file:testdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        using var bootstrap = new SqliteConnection(cs);
        bootstrap.Open();
        SqliteSchema.EnsureCreated(bootstrap);
        var idx = new SqliteTemplateIndex(cs);
        return (idx, cs);
    }

    private static StructuralFingerprint Fp(uint seed)
    {
        var sig = new uint[128]; Array.Fill(sig, seed);
        var bands = new ulong[16]; Array.Fill(bands, (ulong)seed * 37);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig, AnchorMinHash = sig, LshBands = bands,
            PqGramCounts = new Dictionary<string, double> { [$"k-{seed}"] = 1 }, PqGramNorm = 1, ShingleCount = 1, Hex = seed.ToString("X8")
        };
    }

    private static LearnedExtractor Ex(Guid? id = null) => new()
    {
        TemplateId = id ?? Guid.NewGuid(),
        Version = 1,
        Rules = Array.Empty<BlockRule>(),
        Centroid = new ExtractorCentroidState
        {
            TotalObservations = 1, ByRole = new Dictionary<BlockRole, RoleCentroid>(),
            OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow
        }
    };

    [Fact]
    public async Task ConcurrentRecordObservationAsync_DoesNotCorruptCount()
    {
        // Critical 1: SqliteSingleWriter serialises concurrent writes.
        // Spawn 20 concurrent RecordObservation calls; the final count must be exactly 21
        // (1 from RegisterAsync + 20 concurrent).
        var (idx, _) = NewIndex();
        var fp = Fp(999);
        var id = await idx.RegisterAsync(new byte[16], fp, Ex(), default);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => idx.RecordObservationAsync(id, fp, 1.0, default))
            .ToArray();
        await Task.WhenAll(tasks);

        var count = await idx.GetObservationCountAsync(id, default);
        count.Should().Be(21, "RegisterAsync seeds count=1, then 20 concurrent increments = 21");
    }

    [Fact]
    public async Task AgingPriorityScorer_OldHeavyBeatsEquivalentFreshLight_InProbe()
    {
        // Critical 2 integration: verify that the AgingPriorityScorer is actually wired in
        // the probe path by registering two templates with the same similarity but different
        // observation counts; the high-observation template should win.
        var (idx, _) = NewIndex();
        var fpBase = Fp(42);
        var host = new byte[16];

        // Register template A with many observations (simulated via initial registration
        // + many RecordObservation calls) using the same fingerprint.
        var idA = await idx.RegisterAsync(host, fpBase, Ex(), default);
        for (int i = 0; i < 50; i++)
            await idx.RecordObservationAsync(idA, fpBase, 1.0, default);

        // Register template B with zero additional observations.
        var idB = await idx.RegisterAsync(host, fpBase, Ex(), default);

        // Both have identical fingerprints (similarity=1.0). Template A has 51 observations;
        // template B has 1. AgingPriorityScorer should favour A on the slow path.
        var result = await idx.ProbeSlowPathAsync(host, fpBase, 0.5, default);
        result.Should().NotBeNull();
        result!.Value.TemplateId.Should().Be(idA, "high-observation template should win on equal similarity");
    }

    [Fact]
    public async Task ObservationRecorded_Signal_EmittedAfterRecordObservation()
    {
        // Required new test: verify TypedSignalSink<StyloExtractSignal> receives
        // the ObservationRecorded signal after a successful RecordObservationAsync.
        var sink = new TypedSignalSink<StyloExtractSignal>();
        var cs = $"Data Source=file:testdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var idx = new SqliteTemplateIndex(cs, signals: sink);
        var fp = Fp(77);
        var id = await idx.RegisterAsync(new byte[16], fp, Ex(), default);

        var received = new List<SignalEvent<StyloExtractSignal>>();
        sink.TypedSignalRaised += e => received.Add(e);

        await idx.RecordObservationAsync(id, fp, 0.9, default);

        received.Should().ContainSingle(s =>
            s.Signal == StyloExtractSignals.ObservationRecorded &&
            s.Payload.TemplateId == id &&
            s.Payload.ObservationCount == 2); // 1 from register + 1 from record
    }

    [Fact]
    public void TemplateVersionDiff_NonZeroSignatureJaccardDelta_WhenFingerprintsAreDifferent()
    {
        // Important 4: SignatureJaccardDelta must be non-zero when old != new fingerprint.
        var oldFp = Fp(1);
        var newFp = Fp(99999); // Very different

        var diff = TemplateVersionDiffer.Diff(
            new LearnedExtractor { TemplateId = Guid.NewGuid(), Version = 1, Rules = Array.Empty<BlockRule>(), Centroid = new ExtractorCentroidState { TotalObservations = 1, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow } },
            new LearnedExtractor { TemplateId = Guid.NewGuid(), Version = 2, Rules = Array.Empty<BlockRule>(), Centroid = new ExtractorCentroidState { TotalObservations = 2, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow } },
            oldFp, newFp);

        diff.SignatureJaccardDelta.Should().BeGreaterThan(0,
            "delta must be non-zero when fingerprints differ (Important 4 fix)");
    }
}
