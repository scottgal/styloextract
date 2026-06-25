using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Abstractions.TemplateEnrichment;
using StyloExtract.Core;
using StyloExtract.Core.Llm;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Core.Skeleton;
using StyloExtract.Core.TemplateEnrichment;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
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

    [Fact]
    public async Task Repair_Job_Loads_Existing_Yaml_And_Overwrites_With_Llm_Output()
    {
        // Seed an "existing" (broken) template on disk.
        var host = "broken.example";
        var existingPath = Path.Combine(_root, host + ".yaml");
        await File.WriteAllTextAsync(existingPath, """
            host: broken.example
            description: Original (broken) - MainContent points at footer.
            version: 1
            rules:
              - role: MainContent
                selectors:
                  - footer
                confidence: 0.9
            """);

        const string repairedYaml = """
            ```yaml
            host: broken.example
            description: Repaired by the LLM - MainContent now targets the article body.
            version: 2
            rules:
              - role: MainContent
                selectors:
                  - main.article-body
                confidence: 0.95
            ```
            """;
        using var queue = new InMemoryTemplateEnrichmentQueue();
        var llm = new CannedLlm { Response = repairedYaml };
        var inducer = new LlmTemplateInducer(llm, new DomSkeletonRenderer());
        var coordinator = new TemplateEnrichmentCoordinator(
            queue, inducer, _root,
            operatorTemplateStore: null,
            options: new EnrichmentCoordinatorOptions { MinInterCallInterval = TimeSpan.Zero });

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await coordinator.StartAsync(cts.Token);

        var repairJob = new TemplateEnrichmentJob
        {
            Host = host,
            Skeleton = "ROOT body\n├─ main.article-body — \"actual article text\"\n└─ footer — \"chrome\"\n",
            FingerprintHex = "fingerprint01",
            CreatedAt = DateTimeOffset.UtcNow,
            Kind = EnrichmentJobKind.Repair,
            BadMarkdownSample = "© 2026 chrome only",
        };
        (await queue.TryEnqueueAsync(repairJob)).Should().BeTrue();

        var waitUntil = DateTimeOffset.UtcNow.AddSeconds(4);
        while (DateTimeOffset.UtcNow < waitUntil)
        {
            var content = File.Exists(existingPath) ? await File.ReadAllTextAsync(existingPath) : "";
            if (content.Contains("article-body")) break;
            await Task.Delay(50);
        }

        llm.Calls.Should().Be(1, "the coordinator must call the LLM once for the repair job");
        var afterContent = await File.ReadAllTextAsync(existingPath);
        afterContent.Should().Contain("article-body",
            because: "the repaired YAML should overwrite the broken one in place");
        afterContent.Should().NotContain("- footer",
            because: "the broken selector should be replaced, not duplicated");

        queue.Complete();
        try { await coordinator.StopAsync(CancellationToken.None); } catch { }
        cts.Cancel();
    }

    [Fact]
    public async Task Repair_Job_Skips_When_Existing_Template_File_Missing()
    {
        const string anyYaml = """
            ```yaml
            host: nofile.example
            rules:
              - role: MainContent
                selectors:
                  - main
            ```
            """;
        using var queue = new InMemoryTemplateEnrichmentQueue();
        var llm = new CannedLlm { Response = anyYaml };
        var inducer = new LlmTemplateInducer(llm, new DomSkeletonRenderer());
        var coordinator = new TemplateEnrichmentCoordinator(
            queue, inducer, _root,
            operatorTemplateStore: null,
            options: new EnrichmentCoordinatorOptions { MinInterCallInterval = TimeSpan.Zero });

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await coordinator.StartAsync(cts.Token);

        var repairJob = new TemplateEnrichmentJob
        {
            Host = "nofile.example",
            Skeleton = "ROOT body\n└─ main\n",
            FingerprintHex = "fingerprint02",
            CreatedAt = DateTimeOffset.UtcNow,
            Kind = EnrichmentJobKind.Repair,
        };
        (await queue.TryEnqueueAsync(repairJob)).Should().BeTrue();
        await Task.Delay(500);

        llm.Calls.Should().Be(0,
            because: "without an existing template on disk there's nothing to repair");
        File.Exists(Path.Combine(_root, "nofile.example.yaml")).Should().BeFalse();

        queue.Complete();
        try { await coordinator.StopAsync(CancellationToken.None); } catch { }
        cts.Cancel();
    }

    /// <summary>
    /// ExtractionResult.LlmInductionFired is true when the enrichment queue
    /// is wired and the extraction produces a novel template (queue enqueue
    /// succeeds). False when no queue is wired.
    /// </summary>
    [Fact]
    public async Task LlmInductionFired_True_When_EnrichmentQueue_Wired_And_Novel_Template()
    {
        var body = new string('x', 400);
        var html = $"<!DOCTYPE html><html><head><title>LLM Flag Test</title></head>" +
                   $"<body><main><article><h1>Test Article</h1><p>{body}</p>" +
                   $"</article></main></body></html>";

        using var queue = new InMemoryTemplateEnrichmentQueue(new EnrichmentQueueOptions
        {
            PerHostCooldown = TimeSpan.Zero,
        });

        // Build a LayoutExtractor with the queue wired (no skeleton renderer override
        // needed — LayoutExtractor creates its own DomSkeletonRenderer when a queue
        // is provided but no explicit renderer is passed).
        var cs = $"Data Source=file:test-llmflag-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        using var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fp = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        var extractor = new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold: 0.85, slowPathThreshold: 0.75,
            new RefitOrchestrator(index, new ExtractorInducer(), 0.35, 5, 3),
            new DefaultNoopVersionEventSink(),
            enrichmentQueue: queue);

        var sourceUri = new Uri("https://llm-flag.example/article");
        var result = await extractor.ExtractAsync(
            html, sourceUri,
            new ExtractionOptions { LearnNewTemplates = true });

        result.LlmInductionFired.Should().BeTrue(
            because: "a novel template with a wired queue should enqueue LLM induction");

        // Without a queue, the flag must stay false.
        var cs2 = $"Data Source=file:test-llmflag-nq-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        using var conn2 = new SqliteConnection(cs2);
        conn2.Open();
        SqliteSchema.EnsureCreated(conn2);
        var index2 = new SqliteTemplateIndex(cs2);
        var extractor2 = new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index2, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold: 0.85, slowPathThreshold: 0.75,
            new RefitOrchestrator(index2, new ExtractorInducer(), 0.35, 5, 3),
            new DefaultNoopVersionEventSink());

        var result2 = await extractor2.ExtractAsync(
            html, sourceUri,
            new ExtractionOptions { LearnNewTemplates = true });

        result2.LlmInductionFired.Should().BeFalse(
            because: "without a wired enrichment queue LLM induction cannot fire");
    }
}
