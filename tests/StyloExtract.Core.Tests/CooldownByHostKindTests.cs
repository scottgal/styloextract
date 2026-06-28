using FluentAssertions;
using StyloExtract.Abstractions.TemplateEnrichment;
using StyloExtract.Core.TemplateEnrichment;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Pins that Induce and Repair use independent cooldown slots in
/// <see cref="InMemoryTemplateEnrichmentQueue"/>. Before this fix, an Induce
/// enqueued on a host's first visit took the cooldown slot for 1 hour;
/// any subsequent Repair (the apply-time bug-out signal that means "the
/// template you just induced is producing junk, please redo") would be
/// silently dropped. With per-(host, kind) cooldown both can be enqueued.
/// </summary>
public class CooldownByHostKindTests
{
    private static TemplateEnrichmentJob Job(string host, EnrichmentJobKind kind) => new()
    {
        Host = host,
        Skeleton = "ROOT body\n",
        FingerprintHex = "abc",
        CreatedAt = DateTimeOffset.UtcNow,
        Kind = kind,
    };

    [Fact]
    public async Task Repair_For_Same_Host_Is_Not_Blocked_By_Recent_Induce()
    {
        using var queue = new InMemoryTemplateEnrichmentQueue(
            new EnrichmentQueueOptions { PerHostCooldown = TimeSpan.FromHours(1) });

        (await queue.TryEnqueueAsync(Job("example.com", EnrichmentJobKind.Induce))).Should().BeTrue();
        (await queue.TryEnqueueAsync(Job("example.com", EnrichmentJobKind.Repair))).Should().BeTrue(
            "Repair has its own cooldown slot; the recent Induce must not block it");
    }

    [Fact]
    public async Task Same_Kind_For_Same_Host_Within_Cooldown_Is_Dropped()
    {
        using var queue = new InMemoryTemplateEnrichmentQueue(
            new EnrichmentQueueOptions { PerHostCooldown = TimeSpan.FromHours(1) });

        (await queue.TryEnqueueAsync(Job("example.com", EnrichmentJobKind.Induce))).Should().BeTrue();
        (await queue.TryEnqueueAsync(Job("example.com", EnrichmentJobKind.Induce))).Should().BeFalse(
            "the second Induce within the cooldown window must be deduped");
    }

    [Fact]
    public async Task Different_Hosts_Have_Independent_Cooldowns()
    {
        using var queue = new InMemoryTemplateEnrichmentQueue(
            new EnrichmentQueueOptions { PerHostCooldown = TimeSpan.FromHours(1) });

        (await queue.TryEnqueueAsync(Job("a.example", EnrichmentJobKind.Induce))).Should().BeTrue();
        (await queue.TryEnqueueAsync(Job("b.example", EnrichmentJobKind.Induce))).Should().BeTrue();
        (await queue.TryEnqueueAsync(Job("c.example", EnrichmentJobKind.Repair))).Should().BeTrue();
    }
}