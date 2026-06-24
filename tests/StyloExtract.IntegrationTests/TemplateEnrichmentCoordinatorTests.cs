using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Abstractions.TemplateEnrichment;
using StyloExtract.Core.Llm;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Core.Skeleton;
using StyloExtract.Core.TemplateEnrichment;
using Xunit;

namespace StyloExtract.IntegrationTests;

/// <summary>
/// End-to-end test for phase 3b: a TemplateEnrichmentJob enqueued by the
/// extractor is drained by the coordinator, run through a stub
/// ILlmTextProvider, and the resulting OperatorTemplate is written to
/// the operator-template root as a YAML file that the file-watching
/// store picks up and the next request can hard-override against.
/// </summary>
public class TemplateEnrichmentCoordinatorTests : IDisposable
{
    private readonly string _root;

    public TemplateEnrichmentCoordinatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "styloextract-enrich-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private sealed class CannedLlm : ILlmTextProvider
    {
        public string Response { get; set; } = "";
        public int Calls { get; private set; }
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Response);
        }
    }

    private const string ValidYaml = """
        ```yaml
        host: weird-shopify.example
        rules:
          - role: MainContent
            selectors:
              - div.product-detail-root
            confidence: 0.95
          - role: PrimaryNavigation
            selectors:
              - header
            confidence: 0.85
        ```
        """;

    private static TemplateEnrichmentJob NewJob(string host = "weird-shopify.example") => new()
    {
        Host = host,
        Skeleton = "ROOT body\n├─ main.product-detail-root — \"Acme Widget\"\n└─ footer — \"© 2026\"\n",
        FingerprintHex = "abcdef1234567890",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Drains_Job_Calls_Inducer_Writes_Yaml_To_Root()
    {
        using var queue = new InMemoryTemplateEnrichmentQueue();
        var llm = new CannedLlm { Response = ValidYaml };
        var inducer = new LlmTemplateInducer(llm, new DomSkeletonRenderer());
        var coordinator = new TemplateEnrichmentCoordinator(
            queue, inducer, _root,
            operatorTemplateStore: null,
            options: new EnrichmentCoordinatorOptions { MinInterCallInterval = TimeSpan.Zero });

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await coordinator.StartAsync(cts.Token);

        (await queue.TryEnqueueAsync(NewJob())).Should().BeTrue();

        // Spin until the file shows up or timeout.
        var path = Path.Combine(_root, "weird-shopify.example.yaml");
        var waitUntil = DateTimeOffset.UtcNow.AddSeconds(4);
        while (!File.Exists(path) && DateTimeOffset.UtcNow < waitUntil)
        {
            await Task.Delay(50);
        }

        File.Exists(path).Should().BeTrue("the coordinator should write the induced YAML to disk");
        var written = await File.ReadAllTextAsync(path);
        written.Should().Contain("host: weird-shopify.example");
        written.Should().Contain("- role: MainContent");
        written.Should().Contain("- div.product-detail-root");
        written.Should().Contain("Induced from fingerprint abcdef1234567890");

        queue.Complete();
        try { await coordinator.StopAsync(CancellationToken.None); } catch { }
        cts.Cancel();
    }

    [Fact]
    public async Task Skips_When_Operator_Template_Already_Exists_For_Host()
    {
        // Hand-author a template for the host first.
        var existingPath = Path.Combine(_root, "weird-shopify.example.yaml");
        await File.WriteAllTextAsync(existingPath, """
            host: weird-shopify.example
            description: hand-authored
            rules:
              - role: MainContent
                selectors:
                  - .hand-authored-selector
            """);

        using var store = new YamlFileOperatorTemplateStore(_root, watch: false);
        using var queue = new InMemoryTemplateEnrichmentQueue();
        var llm = new CannedLlm { Response = ValidYaml };
        var inducer = new LlmTemplateInducer(llm, new DomSkeletonRenderer());
        var coordinator = new TemplateEnrichmentCoordinator(
            queue, inducer, _root,
            operatorTemplateStore: store,
            options: new EnrichmentCoordinatorOptions { MinInterCallInterval = TimeSpan.Zero });

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await coordinator.StartAsync(cts.Token);

        (await queue.TryEnqueueAsync(NewJob())).Should().BeTrue();
        await Task.Delay(500);

        // LLM should never have been called.
        llm.Calls.Should().Be(0);
        // The hand-authored YAML must be untouched.
        var afterContent = await File.ReadAllTextAsync(existingPath);
        afterContent.Should().Contain("hand-authored");
        afterContent.Should().NotContain("product-detail-root");

        queue.Complete();
        try { await coordinator.StopAsync(CancellationToken.None); } catch { }
        cts.Cancel();
    }

    [Fact]
    public async Task Skips_When_Induced_Template_Has_No_MainContent_Rule()
    {
        const string yamlNoMain = """
            ```yaml
            host: weird.example
            rules:
              - role: Footer
                selectors:
                  - footer
            ```
            """;
        using var queue = new InMemoryTemplateEnrichmentQueue();
        var llm = new CannedLlm { Response = yamlNoMain };
        var inducer = new LlmTemplateInducer(llm, new DomSkeletonRenderer());
        var coordinator = new TemplateEnrichmentCoordinator(
            queue, inducer, _root,
            operatorTemplateStore: null,
            options: new EnrichmentCoordinatorOptions { MinInterCallInterval = TimeSpan.Zero });

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await coordinator.StartAsync(cts.Token);

        (await queue.TryEnqueueAsync(NewJob("weird.example"))).Should().BeTrue();
        await Task.Delay(500);

        // LLM was called, but the result was rejected and nothing was written.
        llm.Calls.Should().Be(1);
        File.Exists(Path.Combine(_root, "weird.example.yaml")).Should().BeFalse();

        queue.Complete();
        try { await coordinator.StopAsync(CancellationToken.None); } catch { }
        cts.Cancel();
    }

    [Fact]
    public async Task Per_Host_Cooldown_Drops_Duplicate_Enqueues_Quickly()
    {
        using var queue = new InMemoryTemplateEnrichmentQueue(new EnrichmentQueueOptions
        {
            PerHostCooldown = TimeSpan.FromSeconds(30),
        });

        (await queue.TryEnqueueAsync(NewJob())).Should().BeTrue();
        // A second job for the same host inside the cooldown window: drop.
        (await queue.TryEnqueueAsync(NewJob())).Should().BeFalse();
        // A different host gets through.
        (await queue.TryEnqueueAsync(NewJob("other.example"))).Should().BeTrue();
    }

    [Fact]
    public async Task Bounded_Queue_Drops_When_Capacity_Reached()
    {
        using var queue = new InMemoryTemplateEnrichmentQueue(new EnrichmentQueueOptions
        {
            Capacity = 2,
            PerHostCooldown = TimeSpan.Zero,
        });

        (await queue.TryEnqueueAsync(NewJob("a"))).Should().BeTrue();
        (await queue.TryEnqueueAsync(NewJob("b"))).Should().BeTrue();
        // Channel uses DropNewest semantics: the third TryWrite succeeds by
        // overwriting the oldest, so behaviour is "always-accepts, drops
        // internally". For the operator, that's "we never lose newer jobs
        // when the LLM is slow." Sanity check by counting drained items.
        (await queue.TryEnqueueAsync(NewJob("c"))).Should().BeTrue();

        queue.Complete();
        var drained = new List<TemplateEnrichmentJob>();
        await foreach (var j in queue.DequeueAllAsync(CancellationToken.None))
        {
            drained.Add(j);
        }
        drained.Count.Should().Be(2,
            because: "the bounded channel keeps at most Capacity items");
    }

    [Fact]
    public async Task Stale_Jobs_Are_Dropped_On_Dequeue()
    {
        using var queue = new InMemoryTemplateEnrichmentQueue(new EnrichmentQueueOptions
        {
            MaxJobAge = TimeSpan.FromMilliseconds(10),
            PerHostCooldown = TimeSpan.Zero,
        });
        // Enqueue with a created-at well in the past.
        var stale = NewJob() with { CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        (await queue.TryEnqueueAsync(stale)).Should().BeTrue();

        queue.Complete();
        var drained = new List<TemplateEnrichmentJob>();
        await foreach (var j in queue.DequeueAllAsync(CancellationToken.None))
        {
            drained.Add(j);
        }
        drained.Should().BeEmpty();
    }
}
