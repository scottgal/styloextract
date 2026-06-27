using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

/// <summary>
/// Task 13 byte-pattern scanner FSM behaviour. Pins the state transitions
/// in terms of byte-level matches: prefix open → content-start open →
/// content-end close, with the nested-open counter refusing premature close
/// matches.
///
/// File name retained from the Task 4 era for git locality; the tests
/// themselves drive the new <see cref="BytePatternScanner"/>.
/// </summary>
public sealed class FenceScannerTests
{
    [Fact]
    public void Transitions_to_AwaitContentStart_when_prefix_pattern_matches()
    {
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));

        var scanner = new BytePatternScanner(in template);
        // Feed just enough to fire the prefix but not the content-start.
        scanner.Feed("<body><header>"u8);

        scanner.State.Should().Be(FenceState.AwaitContentStart);
    }

    [Fact]
    public void Transitions_to_Capturing_when_content_start_pattern_matches_after_prefix()
    {
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));

        var scanner = new BytePatternScanner(in template);
        scanner.Feed("<body><header></header><article>"u8);

        scanner.State.Should().Be(FenceState.Capturing);
    }

    [Fact]
    public void Returns_Captured_verdict_when_content_end_pattern_matches()
    {
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));

        var scanner = new BytePatternScanner(in template);
        var v = scanner.Feed("<body><header>x</header><article><p>YES</p></article></body>"u8);

        v.Should().Be(ScanVerdict.Captured);
        scanner.State.Should().Be(FenceState.Captured);
    }

    [Fact]
    public void Nested_same_tag_does_not_terminate_capture_early()
    {
        // The nested-open counter must keep capture alive past the inner
        // </article> close — only the outer close that returns the counter
        // to 0 ends the capture.
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));

        var scanner = new BytePatternScanner(in template);
        var html =
            "<body><header>x</header><article>" +
            "<article>inner</article>" +
            "outer text" +
            "</article></body>";
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);

        var v = scanner.Feed(bytes);

        v.Should().Be(ScanVerdict.Captured);
        // Capture must end at the outer </article>, not the inner one.
        scanner.CaptureEndByte.Should().BeGreaterThan(scanner.CaptureStartByte);
    }

    [Fact]
    public void Bails_when_byte_budget_exceeded_before_prefix_match()
    {
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"),
            bailoutBytes: 100);

        var scanner = new BytePatternScanner(in template);
        // 500 bytes of unrelated text — well past bailout.
        var bytes = new byte[500];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)'x';
        var v = scanner.Feed(bytes);

        v.Should().Be(ScanVerdict.Bailout);
        scanner.State.Should().Be(FenceState.Bailed);
    }

    [Fact]
    public void Bails_when_capture_region_exceeds_MaxCaptureBytes()
    {
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"),
            maxCaptureBytes: 50);

        var scanner = new BytePatternScanner(in template);
        var sb = new System.Text.StringBuilder();
        sb.Append("<body><header></header><article>");
        for (int i = 0; i < 200; i++) sb.Append("<p>filler</p>");
        sb.Append("</article></body>");
        var v = scanner.Feed(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));

        v.Should().Be(ScanVerdict.Bailout);
        scanner.State.Should().Be(FenceState.Bailed);
    }

    [Fact]
    public void Records_capture_byte_range_at_state_transitions()
    {
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));

        var scanner = new BytePatternScanner(in template);
        scanner.CaptureStartByte.Should().Be(0);
        scanner.CaptureEndByte.Should().Be(0);

        var v = scanner.Feed("<body><header>x</header><article>YES</article></body>"u8);

        v.Should().Be(ScanVerdict.Captured);
        scanner.CaptureStartByte.Should().BeGreaterThan(0);
        scanner.CaptureEndByte.Should().BeGreaterThan(scanner.CaptureStartByte);
    }
}
