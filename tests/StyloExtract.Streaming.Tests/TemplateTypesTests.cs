using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class TemplateTypesTests
{
    [Fact]
    public void StreamingTemplate_round_trips_through_record_with_expression()
    {
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"),
            bailoutBytes: 262_144,
            maxCaptureBytes: 1_048_576);

        var updated = template with { BailoutBytes = 524_288 };
        updated.BailoutBytes.Should().Be(524_288);
        updated.TemplateId.Should().Be(template.TemplateId);
        updated.ContentStartPattern.TagNameSpan.SequenceEqual("article"u8).Should().BeTrue();
    }
}
