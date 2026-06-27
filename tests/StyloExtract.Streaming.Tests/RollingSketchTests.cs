using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class RollingSketchTests
{
    [Fact]
    public void Sketch_signature_matches_fence_built_from_same_events_when_window_holds_all()
    {
        var events = new (ulong tagHash, ulong classHash)[]
        {
            (0x101, 0x201), (0x102, 0x202), (0x103, 0x203), (0x104, 0x204),
            (0x105, 0x205), (0x106, 0x206), (0x107, 0x207), (0x108, 0x208),
        };
        var fence = TemplateFence.BuildFromEvents(events, requiredDepth: 0);

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[16];
        var sketch = new RollingSketch(signatureBuffer, windowBuffer);

        ulong prev = 0UL;
        foreach (var (t, c) in events)
        {
            sketch.Push(prev, t, c);
            prev = t;
        }
        sketch.Recompute();

        for (int i = 0; i < 128; i++)
            sketch.Signature[i].Should().Be(fence.MinHash[i], $"slot {i} should equal fence MinHash slot");
    }

    [Fact]
    public void Empty_sketch_has_all_default_slots()
    {
        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[16];
        var sketch = new RollingSketch(signatureBuffer, windowBuffer);

        for (int i = 0; i < 128; i++)
            sketch.Signature[i].Should().Be(uint.MaxValue);
    }

    [Fact]
    public void Matches_returns_true_for_fence_built_from_same_events()
    {
        var events = MakeEvents(seed: 11, count: 8);
        var fence = TemplateFence.BuildFromEvents(events, requiredDepth: 0);

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[16];
        var sketch = new RollingSketch(signatureBuffer, windowBuffer);
        ulong prev = 0UL;
        foreach (var (t, c) in events)
        {
            sketch.Push(prev, t, c);
            prev = t;
        }
        sketch.Recompute();

        sketch.Matches(in fence).Should().BeTrue();
    }

    [Fact]
    public void Matches_returns_false_for_fence_built_from_disjoint_events()
    {
        var fenceEvents = MakeEvents(seed: 11, count: 8);
        var sketchEvents = MakeEvents(seed: 999, count: 8);
        var fence = TemplateFence.BuildFromEvents(fenceEvents, requiredDepth: 0);

        Span<uint> signatureBuffer = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[16];
        var sketch = new RollingSketch(signatureBuffer, windowBuffer);
        ulong prev = 0UL;
        foreach (var (t, c) in sketchEvents)
        {
            sketch.Push(prev, t, c);
            prev = t;
        }
        sketch.Recompute();

        sketch.Matches(in fence).Should().BeFalse();
    }

    [Fact]
    public void Markov_shingles_are_order_sensitive()
    {
        // alpha.21 contract: reversing the event sequence produces a different
        // signature. Pre-alpha.21 (single-tag shingles) would have matched.
        var a = (ulong)0xAAA;
        var b = (ulong)0xBBB;
        var forward = new (ulong, ulong)[] { (a, 0), (b, 0) };
        var reverse = new (ulong, ulong)[] { (b, 0), (a, 0) };

        var fForward = TemplateFence.BuildFromEvents(forward, requiredDepth: 0);
        var fReverse = TemplateFence.BuildFromEvents(reverse, requiredDepth: 0);

        fForward.MinHash.Should().NotEqual(fReverse.MinHash,
            "Markov bigram shingles must distinguish [A,B] from [B,A]");
    }

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
