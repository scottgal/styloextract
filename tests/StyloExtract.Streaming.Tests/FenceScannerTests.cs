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

        var verdict = ScanVerdict.Continue;
        foreach (var (t, c) in prefixEvents)
            verdict = scanner.Tick(new TagEvent(t, c, ByteLength: 8, IsClose: false));
        foreach (var (t, c) in contentStartEvents)
            verdict = scanner.Tick(new TagEvent(t, c, ByteLength: 8, IsClose: false));
        foreach (var (t, c) in contentEndEvents)
            verdict = scanner.Tick(new TagEvent(t, c, ByteLength: 8, IsClose: false));

        verdict.Should().Be(ScanVerdict.Captured);
        scanner.State.Should().Be(FenceState.Captured);
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

        foreach (var (t, c) in prefixEvents)
            scanner.Tick(new TagEvent(t, c, ByteLength: 10, IsClose: false));
        scanner.CaptureStartByte.Should().Be(0, "Capturing not yet entered");

        foreach (var (t, c) in contentStartEvents)
            scanner.Tick(new TagEvent(t, c, ByteLength: 10, IsClose: false));
        var capStart = scanner.CaptureStartByte;
        capStart.Should().BeGreaterThan(0, "Capturing entered — start recorded");

        foreach (var (t, c) in contentEndEvents)
            scanner.Tick(new TagEvent(t, c, ByteLength: 10, IsClose: false));
        scanner.CaptureEndByte.Should().BeGreaterThan(capStart, "Captured entered — end recorded after start");
    }

    private static StreamingTemplate MakeTemplate(
        (ulong, ulong)[] prefix,
        (ulong, ulong)[] contentStart,
        (ulong, ulong)[] contentEnd) => new()
    {
        TemplateId = Guid.NewGuid(),
        PrefixFence = TemplateFence.BuildFromEvents(prefix, requiredDepth: 0),
        ContentStartFence = TemplateFence.BuildFromEvents(contentStart, requiredDepth: 0),
        ContentEndFence = TemplateFence.BuildFromEvents(contentEnd, requiredDepth: 0),
        MinContentDepth = 0,
        BailoutBytes = 1_000_000,
        MaxCaptureBytes = 1_000_000,
        WindowSize = 8,
        MaxEventsWithoutTransition = 256,
    };

    private static (ulong tagHash, ulong classHash)[] MakeEvents(int seed, int count)
    {
        var events = new (ulong, ulong)[count];
        ulong s = (ulong)seed * 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < count; i++)
        {
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;
            var t = s;
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;
            var c = s;
            events[i] = (t, c);
        }
        return events;
    }
}
