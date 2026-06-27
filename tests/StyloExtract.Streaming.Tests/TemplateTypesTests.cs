using FluentAssertions;
using StyloExtract.Abstractions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class TemplateTypesTests
{
    [Fact]
    public void StreamingTemplate_round_trips_through_record_with_expression()
    {
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagClaim("header"),
            TripwireTestHelpers.TagClaim("article"),
            TripwireTestHelpers.TagClaim("article"),
            bailoutBytes: 262_144,
            maxCaptureBytes: 1_048_576);

        var updated = template with { BailoutBytes = 524_288 };
        updated.BailoutBytes.Should().Be(524_288);
        updated.TemplateId.Should().Be(template.TemplateId);
        updated.ContentStartTripwire.Tag.Should().Be("article");
    }

    [Fact]
    public void IdentityClaim_record_with_expression_preserves_required_fields()
    {
        var claim = TripwireTestHelpers.TagClaim("main");
        var refined = claim with { Id = "content", IdHash = 0xDEADBEEFUL };
        refined.Tag.Should().Be("main");
        refined.TagHash.Should().Be(claim.TagHash);
        refined.Id.Should().Be("content");
    }
}
