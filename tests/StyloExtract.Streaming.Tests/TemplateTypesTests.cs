using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class TemplateTypesTests
{
    [Fact]
    public void StreamingTemplate_round_trips_through_record_with_expression()
    {
        var prefix = new TemplateFence(new uint[128], new ulong[16], 0x1UL, 1);
        var contentStart = new TemplateFence(new uint[128], new ulong[16], 0x2UL, 3);
        var contentEnd = new TemplateFence(new uint[128], new ulong[16], 0x4UL, 3);

        var template = new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = "",
            PrefixFence = prefix,
            ContentStartFence = contentStart,
            ContentEndFence = contentEnd,
            MinContentDepth = 3,
            BailoutBytes = 262_144,
            MaxCaptureBytes = 1_048_576,
            WindowSize = 8,
            MaxEventsWithoutTransition = 256,
        };

        var updated = template with { BailoutBytes = 524_288 };
        updated.BailoutBytes.Should().Be(524_288);
        updated.TemplateId.Should().Be(template.TemplateId);
        updated.ContentStartFence.TagAllowlistBloom.Should().Be(0x2UL);
    }

    [Fact]
    public void TemplateFence_is_a_value_type_passable_by_in()
    {
        var fence = new TemplateFence(new uint[128], new ulong[16], 0xFFUL, 2);
        SinkByIn(in fence).Should().Be(0xFFUL);
    }

    private static ulong SinkByIn(in TemplateFence fence) => fence.TagAllowlistBloom;
}
