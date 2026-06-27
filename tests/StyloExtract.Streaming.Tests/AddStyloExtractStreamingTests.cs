using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class AddStyloExtractStreamingTests
{
    [Fact]
    public void AddStyloExtractStreaming_DefaultsToInMemoryStore()
    {
        var sp = new ServiceCollection()
            .AddStyloExtractStreaming()
            .BuildServiceProvider();

        sp.GetRequiredService<IStreamingTemplateStore>()
            .Should().BeOfType<InMemoryStreamingTemplateStore>();
        sp.GetRequiredService<StreamingPathSelector>().Should().NotBeNull();
        sp.GetRequiredService<StreamingTemplateInducer>().Should().NotBeNull();
        sp.GetRequiredService<StreamingRefitOrchestrator>().Should().NotBeNull();
        sp.GetRequiredService<IStreamingTemplateVersionSink>()
            .Should().BeOfType<NoopStreamingTemplateVersionSink>();
    }

    [Fact]
    public void AddStyloExtractStreaming_WithSqlitePath_RegistersSqliteStore()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"sx-streaming-{Guid.NewGuid():N}.db");
        try
        {
            var sp = new ServiceCollection()
                .AddStyloExtractStreaming(o => o.SqlitePath = tempDb)
                .BuildServiceProvider();

            sp.GetRequiredService<IStreamingTemplateStore>()
                .Should().BeOfType<SqliteStreamingTemplateStore>();
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-wal"); } catch { /* best-effort */ }
            try { File.Delete(tempDb + "-shm"); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void AddStyloExtractStreaming_ConsumerVersionSinkWins()
    {
        // The documented contract: a consumer-registered sink wins over the
        // default no-op sink, regardless of registration order.
        var mySink = new RecordingSink();
        var sp = new ServiceCollection()
            .AddSingleton<IStreamingTemplateVersionSink>(mySink)
            .AddStyloExtractStreaming()
            .BuildServiceProvider();

        sp.GetRequiredService<IStreamingTemplateVersionSink>()
            .Should().BeSameAs(mySink);
    }

    [Fact]
    public void AddStyloExtractStreaming_OptionsCarryThroughToOrchestrator()
    {
        var sp = new ServiceCollection()
            .AddStyloExtractStreaming(o =>
            {
                o.RelativeDriftThreshold = 0.5;
                o.DriftBailoutCount = 7;
                o.ScansPerForcedRefit = 25;
            })
            .BuildServiceProvider();

        var orchestrator = sp.GetRequiredService<StreamingRefitOrchestrator>();
        orchestrator.RelativeDriftThreshold.Should().Be(0.5);
        orchestrator.DriftBailoutCount.Should().Be(7);
        orchestrator.ScansPerForcedRefit.Should().Be(25);
    }

    private sealed class RecordingSink : IStreamingTemplateVersionSink
    {
        public ValueTask OnRefittedAsync(StreamingTemplateRefitEvent evt, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
