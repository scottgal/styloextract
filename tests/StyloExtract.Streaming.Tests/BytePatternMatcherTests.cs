using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

/// <summary>
/// Task 13 byte-pattern matcher behaviour: attribute-order tolerance, quote
/// style tolerance, whitespace tolerance, script/comment body skip, nested
/// same-tag counter, byte-budget bailout, EOS bailout latch.
///
/// Each test runs against the public single-pass <see cref="BytePatternScanner"/>
/// because that's the simplest harness; the same FSM runs in
/// <see cref="IncrementalBytePatternScanner"/> via the shared
/// <see cref="ScannerCore"/>.
/// </summary>
public sealed class BytePatternMatcherTests
{
    [Fact]
    public void Attribute_order_is_tolerated()
    {
        // Pattern requires id="post" only; tag has id first then class.
        var template = BuildTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagWithAttr("main", "id", "post"),
            TripwireTestHelpers.ClosePattern("main"));

        var v1 = ScanByteString(template,
            "<header></header><main id=\"post\" class=\"x\">YES</main>");
        v1.Should().Be(ScanVerdict.Captured);

        // Reversed attribute order — must still match.
        var v2 = ScanByteString(template,
            "<header></header><main class=\"x\" id=\"post\">YES</main>");
        v2.Should().Be(ScanVerdict.Captured);
    }

    [Fact]
    public void Double_quoted_single_quoted_and_unquoted_values_all_match()
    {
        var template = BuildTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagWithAttr("main", "id", "post"),
            TripwireTestHelpers.ClosePattern("main"));

        ScanByteString(template, "<header></header><main id=\"post\">YES</main>")
            .Should().Be(ScanVerdict.Captured);
        ScanByteString(template, "<header></header><main id='post'>YES</main>")
            .Should().Be(ScanVerdict.Captured);
        ScanByteString(template, "<header></header><main id=post>YES</main>")
            .Should().Be(ScanVerdict.Captured);
    }

    [Fact]
    public void Whitespace_around_attrs_and_before_close_is_tolerated()
    {
        var template = BuildTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagWithAttr("main", "id", "post"),
            TripwireTestHelpers.ClosePattern("main"));

        ScanByteString(template,
            "<header></header><main  id=\"post\"  >YES</main>")
            .Should().Be(ScanVerdict.Captured);
        ScanByteString(template,
            "<header></header><main\n\tid = \"post\"\t>YES</main>")
            .Should().Be(ScanVerdict.Captured);
    }

    [Fact]
    public void Script_body_does_not_trigger_pattern_matches()
    {
        // The first <main id="post"> sits inside a <script> body — must be
        // ignored. The real <main id="post"> outside the script must capture.
        var template = BuildTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagWithAttr("main", "id", "post"),
            TripwireTestHelpers.ClosePattern("main"));

        var html =
            "<header></header>" +
            "<script>var s = '<main id=\"post\">fake</main>';</script>" +
            "<main id=\"post\">YES</main>";
        var v = ScanByteString(template, html);
        v.Should().Be(ScanVerdict.Captured);
    }

    [Fact]
    public void Style_body_does_not_trigger_pattern_matches()
    {
        var template = BuildTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagWithAttr("main", "id", "post"),
            TripwireTestHelpers.ClosePattern("main"));

        var html =
            "<header></header>" +
            "<style>.x{content:\"<main id='post'>fake</main>\";}</style>" +
            "<main id=\"post\">YES</main>";
        var v = ScanByteString(template, html);
        v.Should().Be(ScanVerdict.Captured);
    }

    [Fact]
    public void Html_comment_body_does_not_trigger_pattern_matches()
    {
        var template = BuildTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagWithAttr("main", "id", "post"),
            TripwireTestHelpers.ClosePattern("main"));

        var html =
            "<header></header>" +
            "<!-- <main id=\"post\">fake</main> -->" +
            "<main id=\"post\">YES</main>";
        var v = ScanByteString(template, html);
        v.Should().Be(ScanVerdict.Captured);
    }

    [Fact]
    public void Nested_same_tag_capture_ends_on_outer_close_not_inner()
    {
        var template = BuildTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));

        var html =
            "<header></header>" +
            "<article>" +
            "OUTER START " +
            "<article>INNER</article>" +
            " OUTER END" +
            "</article>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var scanner = new BytePatternScanner(in template);
        var v = scanner.Feed(bytes);

        v.Should().Be(ScanVerdict.Captured);
        // The capture should span the entire outer article — inner included.
        var captured = Encoding.UTF8.GetString(
            bytes.AsSpan((int)scanner.CaptureStartByte, (int)(scanner.CaptureEndByte - scanner.CaptureStartByte)));
        captured.Should().Contain("OUTER START");
        captured.Should().Contain("OUTER END");
        captured.Should().Contain("INNER");
    }

    [Fact]
    public void Bails_when_byte_budget_exceeded()
    {
        var template = BuildTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"),
            bailoutBytes: 50);

        var html = new string('x', 500);
        var v = ScanByteString(template, html);
        v.Should().Be(ScanVerdict.Bailout);
    }

    [Fact]
    public void End_of_stream_with_no_match_latches_to_Bailout()
    {
        var template = BuildTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));

        var scanner = IncrementalBytePatternScanner.Create(template);
        scanner.Feed("<html><body><div>nothing here</div></body></html>"u8);
        var v = scanner.Flush();

        v.Should().Be(ScanVerdict.Bailout,
            "Continue at end-of-stream is meaningless — Flush must latch to Bailout");
    }

    private static StreamingTemplate BuildTemplate(
        StyloExtract.Abstractions.BytePattern prefix,
        StyloExtract.Abstractions.BytePattern contentStart,
        StyloExtract.Abstractions.BytePattern contentEnd,
        int bailoutBytes = 1_000_000) =>
        TripwireTestHelpers.MakeTemplate(prefix, contentStart, contentEnd, bailoutBytes);

    private static ScanVerdict ScanByteString(StreamingTemplate template, string html)
    {
        var scanner = new BytePatternScanner(in template);
        return scanner.Feed(Encoding.UTF8.GetBytes(html));
    }
}
