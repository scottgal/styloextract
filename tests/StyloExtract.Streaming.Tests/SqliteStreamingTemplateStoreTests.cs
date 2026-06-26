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
        retrieved.MinContentDepth.Should().Be(template.MinContentDepth);
        retrieved.PrefixFence.TagAllowlistBloom.Should().Be(template.PrefixFence.TagAllowlistBloom);
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

    private static StreamingTemplate BuildTemplate()
    {
        var events = TagEvents("<body>", "<header>", "</header>", "<article>");
        var fence = TemplateFence.BuildFromEvents(events, requiredDepth: 1);
        return new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            PrefixFence = fence,
            ContentStartFence = fence,
            ContentEndFence = fence,
            MinContentDepth = 3,
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
