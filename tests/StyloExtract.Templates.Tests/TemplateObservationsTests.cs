using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class TemplateObservationsTests
{
    [Fact]
    public async Task AppendAndGetByHost_RoundTripsAllFields()
    {
        var cs = $"Data Source=file:obs-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        using var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);

        var obs = NewObservation(host: "www.mostlylucid.net", role: BlockRole.MainContent, bucket: 12345);
        await index.AppendObservationAsync(obs, default);

        var rows = await index.GetObservationsByHostAsync("www.mostlylucid.net", role: null, limit: 100, default);
        rows.Should().HaveCount(1);
        var got = rows[0];
        got.ObservationId.Should().Be(obs.ObservationId);
        got.LshBucket.Should().Be(obs.LshBucket);
        got.Role.Should().Be(obs.Role);
        got.Cardinality.Should().Be(obs.Cardinality);
        got.Confidence.Should().BeApproximately(obs.Confidence, 1e-9);
        got.InducerKind.Should().Be(obs.InducerKind);
        got.TargetSignature.Should().Be(obs.TargetSignature);
        got.Claims.Should().HaveCount(obs.Claims.Count);
        got.Claims[0].Tag.Should().Be(obs.Claims[0].Tag);
        got.Claims[0].Id.Should().Be(obs.Claims[0].Id);
    }

    [Fact]
    public async Task GetByHost_FiltersByRole_AndOrdersByInducedAtDesc()
    {
        var cs = $"Data Source=file:obs-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        using var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);

        var host = "www.bbc.co.uk";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await index.AppendObservationAsync(NewObservation(host, BlockRole.MainContent, bucket: 1, inducedAt: t0), default);
        await index.AppendObservationAsync(NewObservation(host, BlockRole.PrimaryNavigation, bucket: 1, inducedAt: t0.AddMinutes(5)), default);
        await index.AppendObservationAsync(NewObservation(host, BlockRole.MainContent, bucket: 1, inducedAt: t0.AddMinutes(9)), default);

        var mainOnly = await index.GetObservationsByHostAsync(host, BlockRole.MainContent, limit: 100, default);
        mainOnly.Should().HaveCount(2);
        mainOnly[0].InducedAt.Should().BeAfter(mainOnly[1].InducedAt);
    }

    [Fact]
    public async Task GetByBucket_ScopesAcrossHosts()
    {
        var cs = $"Data Source=file:obs-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        using var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);

        await index.AppendObservationAsync(NewObservation("a.com", BlockRole.MainContent, bucket: 42), default);
        await index.AppendObservationAsync(NewObservation("b.com", BlockRole.MainContent, bucket: 42), default);
        await index.AppendObservationAsync(NewObservation("c.com", BlockRole.MainContent, bucket: 7), default);

        var bucket42 = await index.GetObservationsByBucketAsync(42, role: null, limit: 1000, default);
        bucket42.Should().HaveCount(2);
        var bucket7 = await index.GetObservationsByBucketAsync(7, role: null, limit: 1000, default);
        bucket7.Should().HaveCount(1);
    }

    private static TemplateObservation NewObservation(
        string host, BlockRole role, int bucket, DateTimeOffset? inducedAt = null)
    {
        var tag = "div";
        var tagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag));
        var id = "post-content";
        var idHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(id));
        var claim = new IdentityClaim
        {
            Tag = tag,
            TagHash = tagHash,
            Id = id,
            IdHash = idHash,
        };
        return new TemplateObservation
        {
            ObservationId = Guid.NewGuid(),
            Host = host,
            LshBucket = bucket,
            Role = role,
            Claims = new[] { claim },
            TargetSignature = tagHash ^ idHash,
            Cardinality = 1,
            Confidence = 0.92,
            InducedAt = inducedAt ?? DateTimeOffset.UtcNow,
            InducerKind = InducerKind.Heuristic,
        };
    }
}
