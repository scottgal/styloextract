using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class EndToEndCaptureTests
{
    [Fact]
    public void Tokenizer_drives_scanner_to_captured_for_known_template()
    {
        ReadOnlySpan<byte> html =
            "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8;

        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagClaim("header"),
            TripwireTestHelpers.TagClaim("article"),
            TripwireTestHelpers.TagClaim("article"));

        var scanner = new FenceScanner(in template);
        var tokenizer = new MinimalHtmlTokenizer(html);

        var verdict = ScanVerdict.Continue;
        while (verdict == ScanVerdict.Continue && tokenizer.TryReadTag(out var evt))
            verdict = scanner.Tick(in evt);

        verdict.Should().Be(ScanVerdict.Captured);
        scanner.State.Should().Be(FenceState.Captured);
    }
}
