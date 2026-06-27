using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class CorpusMinerTests
{
    [Fact]
    public async Task ComputeMedian_AllObservationsAgree_ReturnsSharedChain()
    {
        var (index, _) = NewIndex();
        const int bucket = 42;
        for (var i = 0; i < 5; i++)
        {
            await index.AppendObservationAsync(
                NewObservation(
                    host: $"host{i}.com",
                    role: BlockRole.MainContent,
                    bucket: bucket,
                    leaf: NewClaim(tag: "main", id: "post-content")),
                default);
        }

        var median = await new CorpusMiner(index).ComputeMedianAsync(bucket, BlockRole.MainContent);

        median.Should().NotBeNull();
        median!.Should().HaveCount(1);
        median![0].Tag.Should().Be("main");
        median![0].Id.Should().Be("post-content");
    }

    [Fact]
    public async Task ComputeMedian_BelowMinSize_ReturnsNull()
    {
        var (index, _) = NewIndex();
        const int bucket = 99;
        for (var i = 0; i < 2; i++)
        {
            await index.AppendObservationAsync(
                NewObservation($"host{i}.com", BlockRole.MainContent, bucket, NewClaim("main", "x")),
                default);
        }

        var median = await new CorpusMiner(index).ComputeMedianAsync(bucket, BlockRole.MainContent);
        median.Should().BeNull();
    }

    [Fact]
    public async Task ComputeStableSubsequence_AllShareLeaf_ReturnsThatLeaf()
    {
        var (index, _) = NewIndex();
        const int bucket = 42;
        for (var i = 0; i < 5; i++)
        {
            await index.AppendObservationAsync(
                NewObservation($"host{i}.com", BlockRole.MainContent, bucket, NewClaim("main", "post-content")),
                default);
        }

        var chain = await new CorpusMiner(index).ComputeStableSubsequenceAsync(bucket, BlockRole.MainContent);

        chain.Should().NotBeNull();
        chain!.Should().HaveCount(1);
        chain![^1].Tag.Should().Be("main");
        chain![^1].Id.Should().Be("post-content");
    }

    [Fact]
    public async Task ComputeStableSubsequence_OnlyClassShared_DropsDivergentId()
    {
        var (index, _) = NewIndex();
        const int bucket = 50;
        for (var i = 0; i < 5; i++)
        {
            await index.AppendObservationAsync(
                NewObservation(
                    $"host{i}.com",
                    BlockRole.MainContent,
                    bucket,
                    NewClaim("main", id: $"site-{i}", classes: new[] { "article-body" })),
                default);
        }

        var chain = await new CorpusMiner(index).ComputeStableSubsequenceAsync(bucket, BlockRole.MainContent);

        chain.Should().NotBeNull();
        chain!.Should().HaveCount(1);
        var leaf = chain![^1];
        leaf.Tag.Should().Be("main");
        leaf.Id.Should().BeNull();
        leaf.Classes.Should().ContainSingle().Which.Should().Be("article-body");
    }

    [Fact]
    public async Task ComputeStableSubsequence_PartialIdPresence_BelowThresholdIsExcluded()
    {
        var (index, _) = NewIndex();
        const int bucket = 51;

        // 2 of 5 share the same id, 5 of 5 share the class.
        for (var i = 0; i < 5; i++)
        {
            var id = i < 2 ? "shared-id" : $"unique-{i}";
            await index.AppendObservationAsync(
                NewObservation($"host{i}.com", BlockRole.MainContent, bucket,
                    NewClaim("main", id, new[] { "article-body" })),
                default);
        }

        var chain = await new CorpusMiner(index)
            .ComputeStableSubsequenceAsync(bucket, BlockRole.MainContent, minPresenceRatio: 0.7);

        chain.Should().NotBeNull();
        chain!.Should().HaveCount(1);
        chain![^1].Id.Should().BeNull("only 2/5 share — below the 0.7 ratio");
        chain![^1].Classes.Should().ContainSingle().Which.Should().Be("article-body");
    }

    [Fact]
    public async Task FindOutliers_DivergentChainSurfaces()
    {
        var (index, _) = NewIndex();
        const int bucket = 60;

        for (var i = 0; i < 4; i++)
        {
            await index.AppendObservationAsync(
                NewObservation($"host{i}.com", BlockRole.MainContent, bucket,
                    NewClaim("main", "post-content")),
                default);
        }

        await index.AppendObservationAsync(
            NewObservation("outlier.com", BlockRole.MainContent, bucket,
                NewClaim("div", "totally-different")),
            default);

        var outliers = await new CorpusMiner(index)
            .FindOutliersAsync(bucket, BlockRole.MainContent, outlierThreshold: 5.0);

        outliers.Should().HaveCount(1);
        outliers[0].Claims[^1].Tag.Should().Be("div");
        outliers[0].Claims[^1].Id.Should().Be("totally-different");
    }

    [Fact]
    public async Task AnalyseCluster_BuildsFrequencyTable()
    {
        var (index, _) = NewIndex();
        const int bucket = 70;

        for (var i = 0; i < 5; i++)
        {
            await index.AppendObservationAsync(
                NewObservation($"host{i}.com", BlockRole.MainContent, bucket,
                    NewClaim("main", "post-content", new[] { "article-body" })),
                default);
        }

        var report = await new CorpusMiner(index).AnalyseClusterAsync(bucket, BlockRole.MainContent);

        report.LshBucket.Should().Be(bucket);
        report.Role.Should().Be(BlockRole.MainContent);
        report.ObservationCount.Should().Be(5);
        report.Positions.Should().HaveCount(1);
        var leaf = report.Positions[0];
        leaf.PositionFromLeaf.Should().Be(0);
        leaf.TagCounts["main"].Should().Be(5);
        leaf.IdCounts["post-content"].Should().Be(5);
        leaf.ClassCounts["article-body"].Should().Be(5);
    }

    [Fact]
    public async Task EmptyBucket_AllMethodsAreSafe()
    {
        var (index, _) = NewIndex();
        var miner = new CorpusMiner(index);

        (await miner.ComputeMedianAsync(404, BlockRole.MainContent)).Should().BeNull();
        (await miner.ComputeStableSubsequenceAsync(404, BlockRole.MainContent)).Should().BeNull();
        (await miner.FindOutliersAsync(404, BlockRole.MainContent)).Should().BeEmpty();

        var report = await miner.AnalyseClusterAsync(404, BlockRole.MainContent);
        report.ObservationCount.Should().Be(0);
        report.Positions.Should().BeEmpty();
    }

    private static (SqliteTemplateIndex Index, SqliteConnection KeepAlive) NewIndex()
    {
        var cs = $"Data Source=file:corpus-miner-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var keepAlive = new SqliteConnection(cs);
        keepAlive.Open();
        SqliteSchema.EnsureCreated(keepAlive);
        return (new SqliteTemplateIndex(cs), keepAlive);
    }

    private static IdentityClaim NewClaim(string tag, string? id = null, IReadOnlyList<string>? classes = null)
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

    private static TemplateObservation NewObservation(
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
