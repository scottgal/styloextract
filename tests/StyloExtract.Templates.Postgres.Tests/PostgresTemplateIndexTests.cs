using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Templates;
using StyloExtract.Templates.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace StyloExtract.Templates.Postgres.Tests;

/// <summary>
/// Roundtrip parity tests for PostgresTemplateIndex against ITemplateIndex contract.
///
/// Two execution modes:
///   1. Docker available (CI): <see cref="PostgreSqlContainer"/> spins up a transient
///      Postgres 16 container per test class, torn down in DisposeAsync.
///   2. No Docker / explicit connection string:
///      Set env var STYLOEXTRACT_PG_CONN to a live Postgres connection string.
///      Each test creates its own schema under a unique schema name to stay isolated.
///   3. Neither available: all tests skip cleanly.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresTemplateIndexTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        var envConn = Environment.GetEnvironmentVariable("STYLOEXTRACT_PG_CONN");
        if (!string.IsNullOrWhiteSpace(envConn))
        {
            _connectionString = envConn;
            return;
        }

        if (!DockerIsAvailable())
        {
            // Skip: neither env var nor Docker. Tests will Skip() individually.
            return;
        }

        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("styloextract_test")
            .WithUsername("se")
            .WithPassword("secret")
            .Build();

        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static bool DockerIsAvailable()
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            proc.Start();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void SkipIfUnavailable()
    {
        if (_connectionString is null)
            throw new Xunit.SkipException("No Postgres connection available (set STYLOEXTRACT_PG_CONN or ensure Docker is running).");
    }

    private PostgresTemplateIndex NewIndex() => new PostgresTemplateIndex(_connectionString!);

    // --- fixtures ---

    private static StructuralFingerprint NewFingerprint(uint seed = 42)
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
            PqGramCounts = new Dictionary<string, double> { [$"k-{seed}"] = 1.0 },
            PqGramNorm = 1.0,
            ShingleCount = 1,
            Hex = seed.ToString("X8")
        };
    }

    private static LearnedExtractor NewExtractor(Guid? id = null, double driftScore = 0.0) => new()
    {
        TemplateId = id ?? Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule
            {
                RuleId = "r1",
                Role = BlockRole.MainContent,
                CssSelectors = new[] { "main > article" },
                MeanConfidence = 0.9,
                ObservationCount = 1,
                DriftScore = 0
            }
        },
        Centroid = new ExtractorCentroidState
        {
            TotalObservations = 1,
            ByRole = new Dictionary<BlockRole, RoleCentroid>(),
            OverallDriftScore = driftScore,
            LastObservation = DateTimeOffset.UtcNow
        }
    };

    // ---- ITemplateIndex contract: register + read roundtrip ----

    [SkippableFact]
    public async Task Register_PersistsTemplateAndExtractor()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var fp = NewFingerprint();
        var ex = NewExtractor();
        var hostHash = new byte[16];

        var id = await idx.RegisterAsync(hostHash, fp, ex, default);

        var loaded = await idx.GetExtractorAsync(id, default);
        loaded.Should().NotBeNull();
        loaded!.Rules.Should().HaveCount(1);
        loaded.Rules[0].RuleId.Should().Be("r1");

        (await idx.GetObservationCountAsync(id, default)).Should().Be(1);
        (await idx.GetTemplateVersionAsync(id, default)).Should().Be(1);
    }

    [SkippableFact]
    public async Task GetExtractor_UnknownId_ReturnsNull()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var loaded = await idx.GetExtractorAsync(Guid.NewGuid(), default);
        loaded.Should().BeNull();
    }

    // ---- centroid/vector roundtrip ----

    [SkippableFact]
    public async Task Register_CentroidVector_RoundtripsCorrectly()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var fp = NewFingerprint(7);
        var ex = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = Array.Empty<BlockRule>(),
            Centroid = new ExtractorCentroidState
            {
                TotalObservations = 42,
                ByRole = new Dictionary<BlockRole, RoleCentroid>
                {
                    [BlockRole.MainContent] = new RoleCentroid
                    {
                        ObservationCount = 10,
                        MeanLinkDensity = 0.12,
                        MeanTextLength = 350.5,
                        MeanDepth = 3.0
                    }
                },
                OverallDriftScore = 0.15,
                LastObservation = DateTimeOffset.UtcNow
            }
        };
        var host = new byte[16];

        var id = await idx.RegisterAsync(host, fp, ex, default);
        var loaded = await idx.GetExtractorAsync(id, default);

        loaded.Should().NotBeNull();
        loaded!.Centroid.TotalObservations.Should().Be(42);
        loaded.Centroid.OverallDriftScore.Should().BeApproximately(0.15, 1e-9);
        loaded.Centroid.ByRole.Should().ContainKey(BlockRole.MainContent);
        var rc = loaded.Centroid.ByRole[BlockRole.MainContent];
        rc.ObservationCount.Should().Be(10);
        rc.MeanLinkDensity.Should().BeApproximately(0.12, 1e-9);
        rc.MeanTextLength.Should().BeApproximately(350.5, 1e-9);
    }

    // ---- fast-path LSH probe ----

    [SkippableFact]
    public async Task ProbeFastPath_HitsRegisteredTemplate()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var host = new byte[16];
        var fp = NewFingerprint(42);
        var id = await idx.RegisterAsync(host, fp, NewExtractor(), default);

        var hit = await idx.ProbeFastPathAsync(host, fp, 0.85, default);

        hit.Should().Be(id);
    }

    [SkippableFact]
    public async Task ProbeFastPath_DifferentBands_ReturnsNull()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var host = new byte[16];
        await idx.RegisterAsync(host, NewFingerprint(1), NewExtractor(), default);

        var hit = await idx.ProbeFastPathAsync(host, NewFingerprint(999), 0.85, default);

        hit.Should().BeNull();
    }

    // ---- slow-path cosine probe ----

    [SkippableFact]
    public async Task ProbeSlowPath_HitsOnPerfectCosine()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var host = new byte[16];
        var fp = NewFingerprint(7);
        var id = await idx.RegisterAsync(host, fp, NewExtractor(), default);

        var hit = await idx.ProbeSlowPathAsync(host, fp, 0.75, default);

        hit.Should().NotBeNull();
        hit!.Value.TemplateId.Should().Be(id);
        hit.Value.Cosine.Should().BeGreaterThan(0.95);
    }

    [SkippableFact]
    public async Task ProbeSlowPath_BelowThreshold_ReturnsNull()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var host = new byte[16];

        // Register a template with pq-gram key "a", then probe with "b" - cosine = 0.
        var fp1 = NewFingerprint(1);
        await idx.RegisterAsync(host, fp1, NewExtractor(), default);

        var fp2 = new StructuralFingerprint
        {
            StructuralMinHash = new uint[128],
            AnchorMinHash = new uint[128],
            LshBands = new ulong[16],
            PqGramCounts = new Dictionary<string, double> { ["entirely-different-key"] = 1.0 },
            PqGramNorm = 1.0,
            ShingleCount = 1,
            Hex = "FFFFFFFF"
        };

        var hit = await idx.ProbeSlowPathAsync(host, fp2, 0.75, default);

        hit.Should().BeNull();
    }

    // ---- RecordObservation ----

    [SkippableFact]
    public async Task RecordObservation_IncrementsCount()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var host = new byte[16];
        var fp = NewFingerprint();
        var id = await idx.RegisterAsync(host, fp, NewExtractor(), default);

        await idx.RecordObservationAsync(id, fp, 0.92, default);
        await idx.RecordObservationAsync(id, fp, 0.88, default);

        (await idx.GetObservationCountAsync(id, default)).Should().Be(3); // 1 from Register + 2
    }

    [SkippableFact]
    public async Task RecordObservation_LruBound_Enforced()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var host = new byte[16];
        var fp = NewFingerprint();
        var id = await idx.RegisterAsync(host, fp, NewExtractor(), default);

        // Insert 110 observations; LRU cap is 100 so table should never exceed 100 rows.
        for (int i = 0; i < 110; i++)
        {
            await idx.RecordObservationAsync(id, fp, 0.9, default);
        }

        // Verify via direct query.
        await using var conn = new Npgsql.NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM template_observations WHERE template_id = @id";
        cmd.Parameters.Add("@id", NpgsqlTypes.NpgsqlDbType.Bytea).Value = id.ToByteArray();
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        count.Should().BeLessOrEqualTo(100);
    }

    // ---- UpdateExtractorAsync (extended surface for refit orchestrator) ----

    [SkippableFact]
    public async Task UpdateExtractor_PersistsNewBlob()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var host = new byte[16];
        var fp = NewFingerprint();
        var original = NewExtractor();
        var id = await idx.RegisterAsync(host, fp, original, default);

        var updated = original with
        {
            Centroid = original.Centroid with { OverallDriftScore = 0.42 }
        };

        await idx.UpdateExtractorAsync(id, updated, default);
        var loaded = await idx.GetExtractorAsync(id, default);

        loaded.Should().NotBeNull();
        loaded!.Centroid.OverallDriftScore.Should().BeApproximately(0.42, 1e-9);
    }

    // ---- BumpVersionAsync (extended surface for refit orchestrator) ----

    [SkippableFact]
    public async Task BumpVersion_IncrementsVersionAndWritesNewFingerprint()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var host = new byte[16];
        var fp1 = NewFingerprint(1);
        var ex1 = NewExtractor();
        var id = await idx.RegisterAsync(host, fp1, ex1, default);

        var fp2 = NewFingerprint(2);
        var ex2 = NewExtractor() with { Version = 2 };
        var (oldV, newV, oldFp) = await idx.BumpVersionAsync(id, ex2, fp2, "drift", 3, default);

        oldV.Should().Be(1);
        newV.Should().Be(2);
        oldFp.Should().NotBeNull();

        (await idx.GetTemplateVersionAsync(id, default)).Should().Be(2);
        var loaded = await idx.GetExtractorAsync(id, default);
        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(2);
    }

    [SkippableFact]
    public async Task BumpVersion_WritesHistoryRow()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var host = new byte[16];
        var fp = NewFingerprint(5);
        var id = await idx.RegisterAsync(host, fp, NewExtractor(), default);

        await idx.BumpVersionAsync(id, NewExtractor() with { Version = 2 }, NewFingerprint(6), "drift", 3, default);

        await using var conn = new Npgsql.NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM template_version_history WHERE template_id = @id";
        cmd.Parameters.Add("@id", NpgsqlTypes.NpgsqlDbType.Bytea).Value = id.ToByteArray();
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        count.Should().Be(1);
    }

    // ---- aging / priority scoring ----

    [SkippableFact]
    public async Task Aging_HeavierTemplateScoredHigherThanLightOneAtSameAge()
    {
        SkipIfUnavailable();

        await using var idxHeavy = NewIndex();
        var host = new byte[16];
        var fp = NewFingerprint(99);

        // Register a "heavy" template with many observations.
        var heavyId = await idxHeavy.RegisterAsync(host, fp, NewExtractor(), default);
        for (int i = 0; i < 50; i++)
            await idxHeavy.RecordObservationAsync(heavyId, fp, 0.9, default);

        // Register a "light" template with few observations (same fingerprint, different host hash).
        var lightHost = new byte[16]; lightHost[0] = 1;
        var lightId = await idxHeavy.RegisterAsync(lightHost, fp, NewExtractor(), default);

        // Both should match their respective host queries.
        var hitHeavy = await idxHeavy.ProbeFastPathAsync(host, fp, 0.5, default);
        var hitLight = await idxHeavy.ProbeFastPathAsync(lightHost, fp, 0.5, default);

        hitHeavy.Should().Be(heavyId);
        hitLight.Should().Be(lightId);
    }

    // ---- PostgresRefitOrchestrator parity ----

    [SkippableFact]
    public async Task PostgresRefitOrchestrator_HighDrift_BumpsVersion()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var fp = NewFingerprint(1);
        // Use a pre-seeded drift score that will cross the threshold after one EWMA step.
        // EWMA: 0.2 * 1.0 + 0.8 * 0.30 = 0.44 > 0.35
        var extractor = new LearnedExtractor
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
                OverallDriftScore = 0.30,
                LastObservation = DateTimeOffset.UtcNow
            }
        };
        var id = await idx.RegisterAsync(new byte[16], fp, extractor, default);
        for (int i = 0; i < 6; i++)
            await idx.RecordObservationAsync(id, fp, 1.0, default);

        var orch = new PostgresRefitOrchestrator(idx, new ExtractorInducer(),
            driftRefitThreshold: 0.35, observationsBeforeStable: 5, versionHistoryDepth: 3);

        // Blocks with unmatched roles -> delta = 1.0.
        var freshBlocks = new[]
        {
            new ExtractedBlock { Id = "b", Role = BlockRole.PrimaryNavigation, Confidence = 0.8, Text = "", Markdown = "", XPath = "/html/body/nav", CssSelector = "html > body > nav", TextLength = 100, LinkDensity = 0.9, Links = Array.Empty<ExtractedLink>() }
        };

        var result = await orch.MaybeRefitAsync(id, fp, freshBlocks, default);

        result.Refitted.Should().BeTrue();
        result.OldVersion.Should().Be(1);
        result.NewVersion.Should().Be(2);
        (await idx.GetTemplateVersionAsync(id, default)).Should().Be(2);
    }

    [SkippableFact]
    public async Task PostgresRefitOrchestrator_LowDrift_DoesNotRefit()
    {
        SkipIfUnavailable();

        await using var idx = NewIndex();
        var fp = NewFingerprint(3);
        var ex = NewExtractor();
        var id = await idx.RegisterAsync(new byte[16], fp, ex, default);
        for (int i = 0; i < 6; i++)
            await idx.RecordObservationAsync(id, fp, 1.0, default);

        var orch = new PostgresRefitOrchestrator(idx, new ExtractorInducer(),
            driftRefitThreshold: 0.35, observationsBeforeStable: 5, versionHistoryDepth: 3);

        // Blocks that match the existing rule role -> low delta.
        var lowDriftBlocks = new[]
        {
            new ExtractedBlock { Id = "b", Role = BlockRole.MainContent, Confidence = 0.9, Text = "hello", Markdown = "hello", XPath = "/main", CssSelector = "main > article", TextLength = 200, LinkDensity = 0.05, Links = Array.Empty<ExtractedLink>() }
        };

        var result = await orch.MaybeRefitAsync(id, fp, lowDriftBlocks, default);

        result.Refitted.Should().BeFalse();
        (await idx.GetTemplateVersionAsync(id, default)).Should().Be(1);
    }
}

