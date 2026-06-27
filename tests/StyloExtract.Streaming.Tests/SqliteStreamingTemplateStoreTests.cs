using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class SqliteStreamingTemplateStoreTests
{
    [Fact]
    public async Task Round_trips_a_template_through_sqlite()
    {
        var template = BuildTemplate();

        await using var store = new SqliteStreamingTemplateStore("Data Source=:memory:");
        await store.RegisterAsync(template);

        var retrieved = await store.GetAsync(template.TemplateId);

        retrieved.Should().NotBeNull();
        retrieved!.TemplateId.Should().Be(template.TemplateId);
        retrieved.BailoutBytes.Should().Be(template.BailoutBytes);
        retrieved.PrefixTripwire.Tag.Should().Be(template.PrefixTripwire.Tag);
        retrieved.PrefixTripwire.TagHash.Should().Be(template.PrefixTripwire.TagHash);
        retrieved.ContentStartTripwire.Tag.Should().Be(template.ContentStartTripwire.Tag);
        retrieved.ContentEndTripwire.Tag.Should().Be(template.ContentEndTripwire.Tag);
    }

    [Fact]
    public async Task Get_unknown_id_returns_null()
    {
        await using var store = new SqliteStreamingTemplateStore("Data Source=:memory:");

        var result = await store.GetAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryGetHot_returns_cached_value_after_RegisterAsync()
    {
        var template = BuildTemplate();
        await using var store = new SqliteStreamingTemplateStore("Data Source=:memory:");
        await store.RegisterAsync(template);

        var hot = store.TryGetHot(template.TemplateId);

        hot.Should().NotBeNull();
        hot!.TemplateId.Should().Be(template.TemplateId);
    }

    [Fact]
    public async Task Version_chain_retains_prior_versions()
    {
        await using var store = new SqliteStreamingTemplateStore("Data Source=:memory:");
        var v1 = BuildTemplate() with { Host = "v.example", Version = 1 };
        var v2 = BuildTemplate() with { Host = "v.example", Version = 2, TemplateId = Guid.NewGuid() };

        await store.UpsertAsync(v1);
        await store.UpsertAsync(v2);

        (await store.GetByHostAsync("v.example"))!.Version.Should().Be(2);
        (await store.GetByHostAtVersionAsync("v.example", 1))!.TemplateId.Should().Be(v1.TemplateId);
        (await store.GetByHostAtVersionAsync("v.example", 2))!.TemplateId.Should().Be(v2.TemplateId);
        var versions = await store.ListVersionsByHostAsync("v.example");
        versions.Should().Equal(1, 2);
    }

    [Fact]
    public async Task Version_chain_survives_reopen()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"sx-streaming-{Guid.NewGuid():N}.db");
        try
        {
            var v1 = BuildTemplate() with { Host = "reopen.example", Version = 1 };
            var v2 = BuildTemplate() with { Host = "reopen.example", Version = 2, TemplateId = Guid.NewGuid() };
            await using (var store = new SqliteStreamingTemplateStore($"Data Source={tempDb}"))
            {
                await store.UpsertAsync(v1);
                await store.UpsertAsync(v2);
            }
            await using (var reopen = new SqliteStreamingTemplateStore($"Data Source={tempDb}"))
            {
                (await reopen.GetByHostAsync("reopen.example"))!.Version.Should().Be(2);
                var versions = await reopen.ListVersionsByHostAsync("reopen.example");
                versions.Should().Equal(1, 2);
            }
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-wal"); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-shm"); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task InMemory_store_version_chain_works()
    {
        var store = new InMemoryStreamingTemplateStore();
        var v1 = BuildTemplate() with { Host = "im.example", Version = 1 };
        var v2 = BuildTemplate() with { Host = "im.example", Version = 2, TemplateId = Guid.NewGuid() };

        await store.UpsertAsync(v1);
        await store.UpsertAsync(v2);

        (await store.GetByHostAsync("im.example"))!.Version.Should().Be(2);
        (await store.GetByHostAtVersionAsync("im.example", 1))!.TemplateId.Should().Be(v1.TemplateId);
        (await store.ListVersionsByHostAsync("im.example")).Should().Equal(1, 2);
    }

    private static StreamingTemplate BuildTemplate() =>
        TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagClaim("header"),
            TripwireTestHelpers.TagClaim("article"),
            TripwireTestHelpers.TagClaim("article"),
            bailoutBytes: 262_144,
            maxCaptureBytes: 1_048_576);
}
