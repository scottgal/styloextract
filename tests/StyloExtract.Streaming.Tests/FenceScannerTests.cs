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
