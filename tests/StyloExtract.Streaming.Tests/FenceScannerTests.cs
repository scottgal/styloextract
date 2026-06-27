using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class FenceScannerTests
{
    [Fact]
    public void Transitions_to_AwaitContentStart_when_prefix_fence_matches()
    {
        var prefixEvents = MakeEvents(seed: 1, count: 8);
        var template = MakeTemplate(
            prefixEvents,
            MakeEvents(seed: 2, count: 8),
            MakeEvents(seed: 3, count: 8));

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[8];
        var scanner = new FenceScanner(in template, signatureBuffer, windowBuffer);

        foreach (var (t, c) in prefixEvents)
            scanner.Tick(new TagEvent(t, c, ByteLength: 8, IsClose: false));

        scanner.State.Should().Be(FenceState.AwaitContentStart);
    }

    [Fact]
    public void Returns_Captured_verdict_when_content_end_fence_matches()
    {
        var prefixEvents = MakeEvents(seed: 1, count: 8);
        var contentStartEvents = MakeEvents(seed: 2, count: 8);
        var contentEndEvents = MakeEvents(seed: 3, count: 8);
        var template = MakeTemplate(prefixEvents, contentStartEvents, contentEndEvents);

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[8];
        var scanner = new FenceScanner(in template, signatureBuffer, windowBuffer);

        // alpha.21: alternate IsClose so DOM depth stays around 0 — otherwise
        // the depth-aware capture-end check refuses to match (capture-end
        // requires depth to return to capture-start depth).
        var verdict = ScanVerdict.Continue;
        FeedAlternatingOpenClose(ref scanner, prefixEvents, ref verdict);
        FeedAlternatingOpenClose(ref scanner, contentStartEvents, ref verdict);
        FeedAlternatingOpenClose(ref scanner, contentEndEvents, ref verdict);

        verdict.Should().Be(ScanVerdict.Captured);
        scanner.State.Should().Be(FenceState.Captured);
    }

    private static void FeedAlternatingOpenClose(
        ref FenceScanner scanner,
        (ulong tagHash, ulong classHash)[] events,
        ref ScanVerdict verdict)
    {
        for (int i = 0; i < events.Length; i++)
        {
            var (t, c) = events[i];
            // i even → open, i odd → close. Class hash is 0 on close per
            // MinimalHtmlTokenizer behaviour, but for shingle equality we
            // keep the test fixture in lockstep: fence built from same
            // (t, c) pairs treats them as open events. Use IsClose alternating
            // so depth tracking returns to ~0 by end of group.
            verdict = scanner.Tick(new TagEvent(t, c, ByteLength: 8, IsClose: (i & 1) == 1));
        }
    }

    [Fact]
    public void Transitions_to_Capturing_when_content_start_fence_matches_after_prefix()
    {
        var prefixEvents = MakeEvents(seed: 1, count: 8);
        var contentStartEvents = MakeEvents(seed: 2, count: 8);
        var template = MakeTemplate(prefixEvents, contentStartEvents, MakeEvents(seed: 3, count: 8));

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[8];
        var scanner = new FenceScanner(in template, signatureBuffer, windowBuffer);

        foreach (var (t, c) in prefixEvents)
            scanner.Tick(new TagEvent(t, c, ByteLength: 8, IsClose: false));
        foreach (var (t, c) in contentStartEvents)
            scanner.Tick(new TagEvent(t, c, ByteLength: 8, IsClose: false));

        scanner.State.Should().Be(FenceState.Capturing);
    }

    [Fact]
    public void Bails_when_per_template_drift_threshold_exceeded()
    {
        var prefixEvents = MakeEvents(seed: 1, count: 4);
        var baseTemplate = MakeTemplate(
            prefixEvents,
            MakeEvents(seed: 2, count: 4),
            MakeEvents(seed: 3, count: 4));
        var template = baseTemplate with
        {
            MaxEventsWithoutTransition = 3,
            BailoutBytes = 1_000_000_000,
        };

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[4];
        var scanner = new FenceScanner(in template, signatureBuffer, windowBuffer);

        // Push the same in-bloom hash repeatedly. Window holds a singleton set;
        // sketch never matches the 4-event fence; drift bailout should fire.
        var inBloomHash = prefixEvents[0].tagHash;
        var verdict = ScanVerdict.Continue;
        for (int i = 0; i < 4; i++)
            verdict = scanner.Tick(new TagEvent(inBloomHash, 0UL, ByteLength: 10, IsClose: false));

        verdict.Should().Be(ScanVerdict.Bailout);
        scanner.State.Should().Be(FenceState.Bailed);
    }

    [Fact]
    public void Non_structural_tags_are_dropped_by_scanner_filter()
    {
        // alpha.21: meta/link/script/img/span/etc bypass the sketch entirely.
        // Feeding only non-structural tags must NEVER reach the prefix
        // fence's match condition — the sketch stays empty.
        var prefixEvents = MakeEvents(seed: 1, count: 8);
        var template = MakeTemplate(
            prefixEvents,
            MakeEvents(seed: 2, count: 8),
            MakeEvents(seed: 3, count: 8));

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[8];
        var scanner = new FenceScanner(in template, signatureBuffer, windowBuffer);

        Span<byte> buf = stackalloc byte[16];
        var nonStructural = new[] { "meta", "link", "script", "img", "span", "a" };
        foreach (var name in nonStructural)
        {
            var len = Encoding.UTF8.GetBytes(name, buf);
            var hash = XxHash3.HashToUInt64(buf[..len]);
            for (int i = 0; i < 16; i++)
                scanner.Tick(new TagEvent(hash, 0UL, ByteLength: 10, IsClose: false));
        }
        // Scanner should still be in AwaitPrefix — never matched, never bailed
        // (bailout requires structural-event push count to exceed threshold).
        scanner.State.Should().Be(FenceState.AwaitPrefix,
            "non-structural tags must NOT push into the sketch or trigger drift bailout");
    }

    [Fact]
    public void Depth_aware_capture_end_skips_nested_match()
    {
        // alpha.21: while in Capturing, ContentEnd matches that occur at
        // depth > _depthAtCaptureStart must be ignored. The capture only
        // terminates when DOM depth returns to baseline AND sketch matches.
        // Set up a fence whose ContentEnd sketch pattern intentionally
        // appears nested inside the content region.
        var prefixEvents = MakeEvents(seed: 1, count: 8);
        var contentStartEvents = MakeEvents(seed: 2, count: 8);
        var contentEndEvents = MakeEvents(seed: 3, count: 8);
        var template = MakeTemplate(prefixEvents, contentStartEvents, contentEndEvents);

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[8];
        var scanner = new FenceScanner(in template, signatureBuffer, windowBuffer);

        // Drive through to Capturing with balanced open/close.
        var v = ScanVerdict.Continue;
        FeedAlternatingOpenClose(ref scanner, prefixEvents, ref v);
        FeedAlternatingOpenClose(ref scanner, contentStartEvents, ref v);
        scanner.State.Should().Be(FenceState.Capturing);

        // Now push the contentEndEvents but as ALL OPENS — depth grows past
        // _depthAtCaptureStart. Even though sketch matches contentEndFence
        // shingles, capture must NOT terminate while depth is elevated.
        foreach (var (t, c) in contentEndEvents)
            v = scanner.Tick(new TagEvent(t, c, ByteLength: 10, IsClose: false));
        v.Should().NotBe(ScanVerdict.Captured,
            "depth-aware capture-end must reject matches while depth > _depthAtCaptureStart");
        scanner.State.Should().Be(FenceState.Capturing);
    }

    [Fact]
    public void Returns_Bailout_when_byte_budget_exceeded_before_prefix_match()
    {
        var baseline = MakeTemplate(
            MakeEvents(seed: 1, count: 8),
            MakeEvents(seed: 2, count: 8),
            MakeEvents(seed: 3, count: 8));
        var template = baseline with { BailoutBytes = 100 };

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[8];
        var scanner = new FenceScanner(in template, signatureBuffer, windowBuffer);

        var verdict = ScanVerdict.Continue;
        for (int i = 0; i < 5; i++)
            verdict = scanner.Tick(new TagEvent(
                TagNameHash: (ulong)(i * 7919 + 100_000),
                ClassHash: (ulong)(i * 31),
                ByteLength: 50,
                IsClose: false));

        verdict.Should().Be(ScanVerdict.Bailout);
        scanner.State.Should().Be(FenceState.Bailed);
    }

    [Fact]
    public void Bails_when_capture_region_exceeds_MaxCaptureBytes()
    {
        var prefixEvents = MakeEvents(seed: 1, count: 8);
        var contentStartEvents = MakeEvents(seed: 2, count: 8);
        var contentEndEvents = MakeEvents(seed: 3, count: 8);
        var baseTemplate = MakeTemplate(prefixEvents, contentStartEvents, contentEndEvents);
        var template = baseTemplate with { MaxCaptureBytes = 50 };

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[8];
        var scanner = new FenceScanner(in template, signatureBuffer, windowBuffer);

        // Drive through to Capturing.
        foreach (var (t, c) in prefixEvents)
            scanner.Tick(new TagEvent(t, c, ByteLength: 5, IsClose: false));
        foreach (var (t, c) in contentStartEvents)
            scanner.Tick(new TagEvent(t, c, ByteLength: 5, IsClose: false));
        scanner.State.Should().Be(FenceState.Capturing);

        // Push a singleton (in-bloom) event repeatedly while in Capturing — sketch never
        // matches content-end fence, byte count exceeds MaxCaptureBytes, scanner bails.
        var inBloomHash = prefixEvents[0].tagHash;
        var verdict = ScanVerdict.Continue;
        for (int i = 0; i < 10; i++)
        {
            verdict = scanner.Tick(new TagEvent(inBloomHash, 0UL, ByteLength: 20, IsClose: false));
            if (verdict != ScanVerdict.Continue) break;
        }

        verdict.Should().Be(ScanVerdict.Bailout);
        scanner.State.Should().Be(FenceState.Bailed);
    }

    [Fact]
    public void Records_capture_byte_range_at_state_transitions()
    {
        var prefixEvents = MakeEvents(seed: 1, count: 8);
        var contentStartEvents = MakeEvents(seed: 2, count: 8);
        var contentEndEvents = MakeEvents(seed: 3, count: 8);
        var template = MakeTemplate(prefixEvents, contentStartEvents, contentEndEvents);

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[8];
        var scanner = new FenceScanner(in template, signatureBuffer, windowBuffer);

        scanner.CaptureStartByte.Should().Be(0);
        scanner.CaptureEndByte.Should().Be(0);

        // alpha.21: feed with alternating open/close so depth stays near 0,
        // matching the depth-aware capture-end semantics.
        var v = ScanVerdict.Continue;
        FeedAlternatingOpenClose(ref scanner, prefixEvents, ref v);
        scanner.CaptureStartByte.Should().Be(0, "Capturing not yet entered");

        FeedAlternatingOpenClose(ref scanner, contentStartEvents, ref v);
        var capStart = scanner.CaptureStartByte;
        capStart.Should().BeGreaterThan(0, "Capturing entered — start recorded");

        FeedAlternatingOpenClose(ref scanner, contentEndEvents, ref v);
        scanner.CaptureEndByte.Should().BeGreaterThan(capStart, "Captured entered — end recorded after start");
    }

    private static StreamingTemplate MakeTemplate(
        (ulong, ulong)[] prefix,
        (ulong, ulong)[] contentStart,
        (ulong, ulong)[] contentEnd) => new()
    {
        TemplateId = Guid.NewGuid(),
        Host = "",
        PrefixFence = TemplateFence.BuildFromEvents(prefix, requiredDepth: 0),
        ContentStartFence = TemplateFence.BuildFromEvents(contentStart, requiredDepth: 0),
        ContentEndFence = TemplateFence.BuildFromEvents(contentEnd, requiredDepth: 0),
        BailoutBytes = 1_000_000,
        MaxCaptureBytes = 1_000_000,
        WindowSize = 8,
        MaxEventsWithoutTransition = 256,
    };

    // alpha.21: synthetic events must be built from STRUCTURAL tag names so the
    // scanner's structural-tag filter doesn't reject them. Seed varies the
    // sequence; count is honored.
    private static readonly string[][] s_structuralPalettes = new[]
    {
        new[] { "header", "nav", "ul", "li", "main", "article", "section", "div" },
        new[] { "body", "main", "article", "h1", "p", "section", "div", "aside" },
        new[] { "footer", "nav", "ul", "li", "div", "p", "section", "article" },
        new[] { "div", "section", "article", "header", "footer", "nav", "main", "aside" },
    };

    private static (ulong tagHash, ulong classHash)[] MakeEvents(int seed, int count)
    {
        var events = new (ulong, ulong)[count];
        var palette = s_structuralPalettes[(seed - 1) % s_structuralPalettes.Length];
        Span<byte> buf = stackalloc byte[16];
        ulong s = (ulong)seed * 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < count; i++)
        {
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;
            var name = palette[i % palette.Length];
            var nameLen = Encoding.UTF8.GetBytes(name, buf);
            var tagHash = XxHash3.HashToUInt64(buf[..nameLen]);
            // Class hash distinct per (seed, i) so different fences don't trivially collide.
            var classHash = s ^ ((ulong)seed << 32);
            events[i] = (tagHash, classHash);
        }
        return events;
    }
}
