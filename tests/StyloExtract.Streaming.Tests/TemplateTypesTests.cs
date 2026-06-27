using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class TemplateTypesTests
{
    [Fact]
    public void StreamingTemplate_round_trips_through_record_with_expression()
    {
        // alpha.21: TemplateFence no longer carries TagAllowlistBloom; the
        // primary constructor is (MinHash, LshBands, RequiredDepth).
        var prefix = new TemplateFence(new uint[128], new ulong[16], 1);
        var contentStart = new TemplateFence(new uint[128], new ulong[16], 3);
        var contentEnd = new TemplateFence(new uint[128], new ulong[16], 3);

        var template = new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = "",
            PrefixFence = prefix,
            ContentStartFence = contentStart,
            ContentEndFence = contentEnd,
            BailoutBytes = 262_144,
            MaxCaptureBytes = 1_048_576,
            WindowSize = 8,
            MaxEventsWithoutTransition = 256,
        };

        var updated = template with { BailoutBytes = 524_288 };
        updated.BailoutBytes.Should().Be(524_288);
        updated.TemplateId.Should().Be(template.TemplateId);
        updated.ContentStartFence.RequiredDepth.Should().Be(3);
    }

    [Fact]
    public void TemplateFence_is_a_value_type_passable_by_in()
    {
        var fence = new TemplateFence(new uint[128], new ulong[16], 2);
        SinkByIn(in fence).Should().Be(2);
    }

    private static int SinkByIn(in TemplateFence fence) => fence.RequiredDepth;
}
