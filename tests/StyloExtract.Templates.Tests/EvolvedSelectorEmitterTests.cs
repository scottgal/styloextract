using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class EvolvedSelectorEmitterTests
{
    [Fact]
    public async Task EmitForCluster_TwoHosts_EmitsOnePerHost()
    {
        var (index, _) = NewIndex();
        const int bucket = 42;

        // 3 from hostA, 2 from hostB — all share the same leaf claim.
        for (var i = 0; i < 3; i++)
        {
            await index.AppendObservationAsync(
                NewObservation("a.com", BlockRole.MainContent, bucket,
                    NewClaim("main", "post-content")), default);
        }
        for (var i = 0; i < 2; i++)
        {
            await index.AppendObservationAsync(
                NewObservation("b.com", BlockRole.MainContent, bucket,
                    NewClaim("main", "post-content")), default);
        }

        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        var emitted = await emitter.EmitForClusterAsync(bucket, BlockRole.MainContent);

        emitted.Should().Be(2, "one candidate per distinct contributing host");

        var hostACandidates = await index.GetCandidatesByHostAsync("a.com", BlockRole.MainContent);
        var hostBCandidates = await index.GetCandidatesByHostAsync("b.com", BlockRole.MainContent);

        hostACandidates.Should().HaveCount(1);
        hostACandidates[0].Claims.Should().HaveCount(1);
        hostACandidates[0].Claims[^1].Tag.Should().Be("main");
        hostACandidates[0].Claims[^1].Id.Should().Be("post-content");
        hostACandidates[0].SourceCount.Should().Be(3);
        hostACandidates[0].LshBucket.Should().Be(bucket);
        hostACandidates[0].Role.Should().Be(BlockRole.MainContent);
        hostACandidates[0].ReputationScore.Should().Be(0);
        hostACandidates[0].LastWonAt.Should().BeNull();
        hostACandidates[0].LastLostAt.Should().BeNull();

        hostBCandidates.Should().HaveCount(1);
        hostBCandidates[0].SourceCount.Should().Be(2);
    }

    [Fact]
    public async Task EmitForCluster_RepeatRun_DedupesViaUniqueIndex()
    {
        var (index, _) = NewIndex();
        const int bucket = 43;

        for (var i = 0; i < 3; i++)
        {
            await index.AppendObservationAsync(
                NewObservation("a.com", BlockRole.MainContent, bucket,
                    NewClaim("main", "post-content")), default);
        }
        for (var i = 0; i < 2; i++)
        {
            await index.AppendObservationAsync(
                NewObservation("b.com", BlockRole.MainContent, bucket,
                    NewClaim("main", "post-content")), default);
        }

        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        var first = await emitter.EmitForClusterAsync(bucket, BlockRole.MainContent);
        var second = await emitter.EmitForClusterAsync(bucket, BlockRole.MainContent);

        first.Should().Be(2);
        second.Should().Be(2,
            "the upsert call count is returned regardless; UNIQUE index makes the writes no-ops");

        var hostACandidates = await index.GetCandidatesByHostAsync("a.com", BlockRole.MainContent);
        var hostBCandidates = await index.GetCandidatesByHostAsync("b.com", BlockRole.MainContent);
        hostACandidates.Should().HaveCount(1, "dedup prevents a second row for the same target");
        hostBCandidates.Should().HaveCount(1);
    }

    [Fact]
    public async Task EmitForCluster_InsufficientObservations_ReturnsZero()
    {
        var (index, _) = NewIndex();
        const int bucket = 44;

        await index.AppendObservationAsync(
            NewObservation("a.com", BlockRole.MainContent, bucket, NewClaim("main", "x")), default);
        await index.AppendObservationAsync(
            NewObservation("b.com", BlockRole.MainContent, bucket, NewClaim("main", "x")), default);

        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        var emitted = await emitter.EmitForClusterAsync(bucket, BlockRole.MainContent);

        emitted.Should().Be(0);
        var rows = await index.GetCandidatesByHostAsync("a.com", BlockRole.MainContent);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task EmitForCluster_NoStableSubsequence_ReturnsZero()
    {
        var (index, _) = NewIndex();
        const int bucket = 45;

        // 5 observations across 5 hosts, all with disjoint tags. No stable
        // tag at the leaf position crosses the 0.7 threshold.
        var tags = new[] { "div", "section", "article", "main", "aside" };
        for (var i = 0; i < tags.Length; i++)
        {
            await index.AppendObservationAsync(
                NewObservation($"host{i}.com", BlockRole.MainContent, bucket,
                    NewClaim(tags[i], $"id-{i}")), default);
        }

        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        var emitted = await emitter.EmitForClusterAsync(bucket, BlockRole.MainContent);

        emitted.Should().Be(0);
    }

    [Fact]
    public async Task EmitForAllClusters_CoversMultipleBucketsAndRoles()
    {
        var (index, _) = NewIndex();

        // Bucket 100, MainContent — 3 observations, 1 host (still emits 1 candidate)
        for (var i = 0; i < 3; i++)
        {
            await index.AppendObservationAsync(
                NewObservation("a.com", BlockRole.MainContent, 100,
                    NewClaim("main", "post-content")), default);
        }

        // Bucket 100, PrimaryNavigation — 4 observations, 2 hosts → 2 candidates
        for (var i = 0; i < 2; i++)
        {
            await index.AppendObservationAsync(
                NewObservation("a.com", BlockRole.PrimaryNavigation, 100,
                    NewClaim("nav", "main-nav")), default);
            await index.AppendObservationAsync(
                NewObservation("b.com", BlockRole.PrimaryNavigation, 100,
                    NewClaim("nav", "main-nav")), default);
        }

        // Bucket 200, MainContent — 3 observations, 1 host
        for (var i = 0; i < 3; i++)
        {
            await index.AppendObservationAsync(
                NewObservation("c.com", BlockRole.MainContent, 200,
                    NewClaim("article", "body")), default);
        }

        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        var results = new List<ClusterEmissionResult>();
        await foreach (var r in emitter.EmitForAllClustersAsync())
        {
            results.Add(r);
        }

        results.Should().HaveCount(3);
        var cells = results
            .Select(r => (r.LshBucket, r.Role, r.CandidatesEmitted))
            .ToHashSet();
        cells.Should().Contain((100, BlockRole.MainContent, 1));
        cells.Should().Contain((100, BlockRole.PrimaryNavigation, 2));
        cells.Should().Contain((200, BlockRole.MainContent, 1));
    }

    [Fact]
    public async Task GetCandidatesByHost_FiltersByRole()
    {
        var (index, _) = NewIndex();
        const int bucket = 50;
        for (var i = 0; i < 3; i++)
        {
            await index.AppendObservationAsync(
                NewObservation("a.com", BlockRole.MainContent, bucket,
                    NewClaim("main", "post-content")), default);
            await index.AppendObservationAsync(
                NewObservation("a.com", BlockRole.PrimaryNavigation, bucket,
                    NewClaim("nav", "primary")), default);
        }

        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        await emitter.EmitForClusterAsync(bucket, BlockRole.MainContent);
        await emitter.EmitForClusterAsync(bucket, BlockRole.PrimaryNavigation);

        var all = await index.GetCandidatesByHostAsync("a.com");
        all.Should().HaveCount(2);

        var mainOnly = await index.GetCandidatesByHostAsync("a.com", BlockRole.MainContent);
        mainOnly.Should().HaveCount(1);
        mainOnly[0].Role.Should().Be(BlockRole.MainContent);
    }

    internal static (SqliteTemplateIndex Index, SqliteConnection KeepAlive) NewIndex()
    {
        var cs = $"Data Source=file:emit-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var keepAlive = new SqliteConnection(cs);
        keepAlive.Open();
        SqliteSchema.EnsureCreated(keepAlive);
        return (new SqliteTemplateIndex(cs), keepAlive);
    }

    internal static IdentityClaim NewClaim(string tag, string? id = null, IReadOnlyList<string>? classes = null)
    {
        var tagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag));
        ulong? idHash = id is null ? null : XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(id));
        var classArr = classes is null || classes.Count == 0 ? Array.Empty<string>() : classes.ToArray();
        var classHashes = new ulong[classArr.Length];
        for (var i = 0; i < classArr.Length; i++)
        {
            classHashes[i] = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(classArr[i]));
        }
        return new IdentityClaim
        {
            Tag = tag,
            TagHash = tagHash,
            Id = id,
            IdHash = idHash,
            Classes = classArr,
            ClassHashes = classHashes,
        };
    }

    internal static TemplateObservation NewObservation(
        string host, BlockRole role, int bucket, IdentityClaim leaf, DateTimeOffset? inducedAt = null)
    {
        return new TemplateObservation
        {
            ObservationId = Guid.NewGuid(),
            Host = host,
            LshBucket = bucket,
            Role = role,
            Claims = new[] { leaf },
            TargetSignature = leaf.TagHash ^ (leaf.IdHash ?? 0UL),
            Cardinality = 1,
            Confidence = 0.9,
            InducedAt = inducedAt ?? DateTimeOffset.UtcNow,
            InducerKind = InducerKind.Heuristic,
        };
    }
}
