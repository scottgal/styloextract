using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class TemplateFenceBuilderTests
{
    [Fact]
    public void BuildFromEvents_produces_signature_with_some_slots_set()
    {
        var events = new (ulong tagHash, ulong classHash)[]
        {
            (0x101, 0x201), (0x102, 0x202), (0x103, 0x203), (0x104, 0x204),
            (0x105, 0x205), (0x106, 0x206), (0x107, 0x207), (0x108, 0x208),
        };

        var fence = TemplateFence.BuildFromEvents(events, requiredDepth: 0);

        fence.MinHash.Should().HaveCount(128);
        fence.MinHash.Should().Contain(slot => slot != uint.MaxValue, "at least one slot should be lowered from default after observing events");
        fence.LshBands.Should().HaveCount(16);
        fence.RequiredDepth.Should().Be(0);
    }
}
