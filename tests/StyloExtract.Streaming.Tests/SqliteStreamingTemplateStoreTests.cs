using System.IO.Hashing;
using System.Text;
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
        retrieved.WindowSize.Should().Be(template.WindowSize);
        retrieved.BailoutBytes.Should().Be(template.BailoutBytes);
        retrieved.PrefixFence.MinHash.Should().Equal(template.PrefixFence.MinHash);
        retrieved.ContentStartFence.MinHash.Should().Equal(template.ContentStartFence.MinHash);
        retrieved.ContentEndFence.LshBands.Should().Equal(template.ContentEndFence.LshBands);
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
        // alpha.21: UpsertAsync now appends per (host, version). Both versions
        // survive; GetByHostAsync returns the latest; GetByHostAtVersionAsync
        // retrieves any version; ListVersionsByHostAsync enumerates.
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

    private static StreamingTemplate BuildTemplate()
    {
        var events = TagEvents("<body>", "<header>", "</header>", "<article>");
        var fence = TemplateFence.BuildFromEvents(events, requiredDepth: 1);
        return new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = "",
            PrefixFence = fence,
            ContentStartFence = fence,
            ContentEndFence = fence,
            BailoutBytes = 262_144,
            MaxCaptureBytes = 1_048_576,
            WindowSize = 8,
            MaxEventsWithoutTransition = 256,
        };
    }

    private static (ulong tagHash, ulong classHash)[] TagEvents(params string[] tags)
    {
        var result = new (ulong, ulong)[tags.Length];
        Span<byte> buf = stackalloc byte[64];
        for (int i = 0; i < tags.Length; i++)
        {
            var t = tags[i];
            var isClose = t.StartsWith("</", StringComparison.Ordinal);
            var nameStart = isClose ? 2 : 1;
            var nameEnd = t.IndexOf('>', nameStart);
            var name = t.AsSpan(nameStart, nameEnd - nameStart);
            var n = Encoding.UTF8.GetBytes(name, buf);
            result[i] = (XxHash3.HashToUInt64(buf[..n]), 0UL);
        }
        return result;
    }
}
