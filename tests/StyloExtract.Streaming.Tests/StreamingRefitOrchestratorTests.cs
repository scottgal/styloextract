using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class StreamingRefitOrchestratorTests
{
    [Fact]
    public async Task Single_captured_observation_does_not_trigger_refit()
    {
        var (store, _, orchestrator, sink) = BuildPipeline();
        var seed = BuildPageA();
        await SeedHost(store, "host.test", seed);

        orchestrator.RecordCaptured("host.test", 100, 500, seed);

        // Wait briefly to let any (mistakenly) queued refit fire.
        await Task.Delay(50);
        sink.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Cadence_refit_fires_on_tenth_captured_scan_if_template_changed()
    {
        var (store, _, orchestrator, sink) = BuildPipeline(scansPerForcedRefit: 10);
        var initialBytes = BuildPageA();
        await SeedHost(store, "host.test", initialBytes);

        // Feed 9 captureds with the original page — same fences, no refit.
        for (int i = 0; i < 9; i++)
            orchestrator.RecordCaptured("host.test", 100, 500, initialBytes);
        await Task.Delay(100);
        sink.Events.Should().BeEmpty();

        // 10th captured uses a structurally different page — the inducer
        // produces different fences, refit should fire.
        var driftedBytes = BuildPageB();
        orchestrator.RecordCaptured("host.test", 100, 500, driftedBytes);

        await WaitForEvents(sink, expectedCount: 1, timeoutMs: 1000);
        sink.Events.Should().HaveCount(1);
        sink.Events[0].Host.Should().Be("host.test");
        sink.Events[0].OldVersion.Should().Be(1);
        sink.Events[0].NewVersion.Should().Be(2);
        sink.Events[0].Reason.Should().Be("cadence");
    }

    [Fact]
    public async Task ForceRefit_replaces_template_when_fences_differ()
    {
        var (store, _, orchestrator, sink) = BuildPipeline();
        var initial = BuildPageA();
        await SeedHost(store, "host.test", initial);

        var altered = BuildPageB();
        var refitted = await orchestrator.ForceRefitAsync("host.test", altered, "test");

        refitted.Should().NotBeNull();
        refitted!.Version.Should().Be(2);
        sink.Events.Should().HaveCount(1);
        sink.Events[0].NewVersion.Should().Be(2);
        sink.Events[0].Reason.Should().Be("test");

        var fromStore = await store.GetByHostAsync("host.test");
        fromStore.Should().NotBeNull();
        fromStore!.Version.Should().Be(2);
    }

    [Fact]
    public async Task ForceRefit_is_noop_when_fences_match()
    {
        var (store, _, orchestrator, sink) = BuildPipeline();
        var bytes = BuildPageA();
        await SeedHost(store, "host.test", bytes);

        var refitted = await orchestrator.ForceRefitAsync("host.test", bytes, "test");

        refitted.Should().BeNull();
        sink.Events.Should().BeEmpty();
        var fromStore = await store.GetByHostAsync("host.test");
        fromStore!.Version.Should().Be(1);
    }

    [Fact]
    public async Task StreamingTemplate_Version_defaults_to_1()
    {
        // Confirms additive-field default behaviour for pre-alpha.18 persisted
        // templates that won't carry a Version column.
        var template = new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = "x",
            PrefixFence = default,
            ContentStartFence = default,
            ContentEndFence = default,
            MinContentDepth = 0,
            BailoutBytes = 1,
            MaxCaptureBytes = 1,
            WindowSize = 1,
            MaxEventsWithoutTransition = 1,
        };
        template.Version.Should().Be(1);
        await Task.CompletedTask;
    }

    private static (
        IStreamingTemplateStore store,
        StreamingTemplateInducer inducer,
        StreamingRefitOrchestrator orchestrator,
        RecordingSink sink)
        BuildPipeline(int scansPerForcedRefit = StreamingRefitOrchestrator.DefaultScansPerForcedRefit)
    {
        var store = new InMemoryStreamingTemplateStore();
        var inducer = new StreamingTemplateInducer();
        var sink = new RecordingSink();
        var orch = new StreamingRefitOrchestrator(
            store, inducer, sink,
            scansPerForcedRefit: scansPerForcedRefit);
        return (store, inducer, orch, sink);
    }

    private static async Task SeedHost(IStreamingTemplateStore store, string host, byte[] bytes)
    {
        var inducer = new StreamingTemplateInducer();
        var template = inducer.Induce(host, bytes);
        template.Should().NotBeNull();
        await store.UpsertAsync(template!);
    }

    private static async Task WaitForEvents(RecordingSink sink, int expectedCount, int timeoutMs)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (sink.Events.Count >= expectedCount) return;
            await Task.Delay(20);
        }
    }

    private static byte[] BuildPageA()
    {
        var sb = new StringBuilder();
        sb.Append("<html><body><header><nav>x</nav></header>");
        for (int i = 0; i < 8; i++) sb.Append("<p>paragraph</p>");
        sb.Append("<footer>fin</footer></body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] BuildPageB()
    {
        // Same semantic skeleton (so inducer succeeds) but different inner
        // structure — paragraph cluster after a much deeper nav block.
        var sb = new StringBuilder();
        sb.Append("<html><body><header><nav><ul>");
        for (int i = 0; i < 10; i++) sb.Append("<li><a>l</a></li>");
        sb.Append("</ul></nav></header><main><article>");
        for (int i = 0; i < 30; i++) sb.Append("<p>long paragraph</p>");
        sb.Append("</article></main><footer>fin</footer></body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private sealed class RecordingSink : IStreamingTemplateVersionSink
    {
        public List<StreamingTemplateRefitEvent> Events { get; } = new();
        public ValueTask OnRefittedAsync(StreamingTemplateRefitEvent evt, CancellationToken cancellationToken)
        {
            lock (Events) Events.Add(evt);
            return ValueTask.CompletedTask;
        }
    }
}
