using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class EndToEndCaptureTests
{
    [Fact]
    public void Scanner_drives_to_Captured_for_known_template()
    {
        ReadOnlySpan<byte> html =
            "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8;

        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));

        var scanner = new BytePatternScanner(in template);
        var verdict = scanner.Feed(html);

        verdict.Should().Be(ScanVerdict.Captured);
        scanner.State.Should().Be(FenceState.Captured);
    }
}
