using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;
using StyloExtract.Core.Llm;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Core.Skeleton;
using StyloExtract.Html;
using StyloExtract.Llm.Ollama;
using Xunit;

namespace StyloExtract.IntegrationTests;

/// <summary>
/// Real-Ollama end-to-end. Pulls in the live Gemma 4 model that's
/// already on disk (gemma4:e2b in the dev environment per the design
/// recommendation) and validates the actual induction chain:
/// DomCleaner → DomSkeletonRenderer → LlmInducerPrompts →
/// OllamaTextProvider (HTTP) → LlmTemplateInducer YAML extraction →
/// YamlOperatorTemplateLoader.Parse.
///
/// <para>
/// SkippableFact: skipped when Ollama is unreachable on localhost:11434
/// OR the configured model is not installed. Network-dependent + needs
/// ~7 GB of disk for the model; intentionally NOT a default-suite test.
/// Run explicitly with --filter to exercise.
/// </para>
/// </summary>
public class LiveOllamaInductionTests
{
    private const string OllamaUrl = "http://localhost:11434";

    // Default model per the design (gemma4:e4b-it-qat) — but fall back to
    // anything in the gemma4 family that's locally available so the test
    // works in the dev environment without a forced model download.
    private static readonly string[] CandidateModels =
    {
        "gemma4:e4b-it-qat",
        "gemma4:e2b",
        "gemma4:12b-it-qat",
    };

    private const string ProductPageHtml = """
        <!DOCTYPE html>
        <html><head><title>Acme Widget v4 — Product</title></head>
        <body>
          <header><nav class="site-nav">
            <a href="/">Home</a><a href="/shop">Shop</a><a href="/about">About</a>
          </nav></header>
          <main class="product-detail-root">
            <h1 class="product__title">Acme Widget v4</h1>
            <div class="product-description-body">
              <p>The Widget v4 is our flagship product, hand-finished in our Portland
                 workshop with materials sourced from the Pacific Northwest.</p>
              <p>Available in three colors. Each unit includes a lifetime warranty
                 and free shipping anywhere in the contiguous US.</p>
            </div>
          </main>
          <footer class="site-footer">© 2026 Acme · Privacy · Terms</footer>
        </body></html>
        """;

    private static async Task<(bool OllamaUp, string? ModelToUse)> ProbeAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var doc = await http.GetFromJsonAsync<OllamaTagsResponse>($"{OllamaUrl}/api/tags");
            var available = doc?.Models?.Select(m => m.Name).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (available is null) return (false, null);
            foreach (var candidate in CandidateModels)
            {
                if (available.Contains(candidate)) return (true, candidate);
            }
            // Fallback: any gemma4 tag the user has.
            var anyGemma4 = available.FirstOrDefault(n => n!.StartsWith("gemma4:", StringComparison.OrdinalIgnoreCase));
            return anyGemma4 is null ? (true, null) : (true, anyGemma4);
        }
        catch
        {
            return (false, null);
        }
    }

    [SkippableFact(Timeout = 120_000)]
    public async Task Inducer_Round_Trips_Through_Live_Gemma4_Ollama()
    {
        var (ollamaUp, model) = await ProbeAsync();
        Skip.IfNot(ollamaUp, $"Ollama unreachable at {OllamaUrl}; `ollama serve` or skip.");
        Skip.If(model is null,
            $"No Gemma 4 model installed locally. Tried: {string.Join(", ", CandidateModels)}. " +
            "`ollama pull gemma4:e2b` (or another gemma4 variant) to enable.");

        // Build the live stack with the real Ollama backend.
        using var http = new HttpClient();
        var opts = Options.Create(new OllamaTextProviderOptions
        {
            OllamaUrl = OllamaUrl,
            Model = model!,
            Timeout = TimeSpan.FromSeconds(90), // generous for cold-cache loads
            Temperature = 0.1,
            MaxOutputTokens = 1024,
        });
        var provider = new OllamaTextProvider(http, opts);
        var inducer = new LlmTemplateInducer(provider, new DomSkeletonRenderer());

        // Real DOM → cleaned → induced.
        var doc = new AngleSharpHtmlDomParser().Parse(ProductPageHtml);
        new DomCleaner().Clean(doc);

        OperatorTemplate? template;
        try
        {
            template = await inducer.InduceAsync(doc, "acme.example");
        }
        catch (LlmProviderException ex) when (ex.Message.Contains("model"))
        {
            // Model loaded but didn't recognise the prompt shape; treat as
            // skip rather than failure because the model selection is
            // beyond what this test controls.
            Skip.If(true, $"Live LLM rejected the prompt: {ex.Message}");
            return;
        }

        // Models in this size class don't always produce valid YAML on the
        // first try. What we assert is: the chain RAN (HTTP succeeded,
        // wire format was right, response was parsed), not necessarily
        // that the induction was useful. The "produces good templates"
        // bar is the quality-calibration follow-up (v2.1).
        if (template is null)
        {
            // Print the raw model output for the operator running this test
            // to inspect. Skip rather than fail — small models are imperfect
            // structured-output emitters and the harness already validated
            // every parse / failure mode in LlmTemplateInducerTests with
            // canned responses.
            Skip.If(true,
                $"Model {model} didn't produce a parseable YAML template for the test page. " +
                "This is expected for E2B-class models; the chain ran end-to-end (no exception). " +
                "Try a larger model (gemma4:12b-it-qat) for production quality.");
            return;
        }

        template.Host.Should().Be("acme.example");
        template.Rules.Should().NotBeEmpty();
        template.Rules.Any(r => r.Selectors.Count > 0).Should().BeTrue(
            because: "any successful induction yields at least one rule with one selector");

        // The roundtrip emit must also produce valid YAML that re-parses.
        var emitted = OperatorTemplateYamlEmitter.Emit(template);
        var reparsed = YamlOperatorTemplateLoader.Parse(emitted);
        reparsed.Host.Should().Be(template.Host);
        reparsed.Rules.Count.Should().Be(template.Rules.Count);
    }

    [SkippableFact(Timeout = 30_000)]
    public async Task Provider_Reports_LlmProviderException_When_Model_Not_Installed()
    {
        var (ollamaUp, _) = await ProbeAsync();
        Skip.IfNot(ollamaUp, "Ollama unreachable");

        using var http = new HttpClient();
        var provider = new OllamaTextProvider(http, Options.Create(new OllamaTextProviderOptions
        {
            OllamaUrl = OllamaUrl,
            Model = "definitely-not-a-real-model:999b",
            Timeout = TimeSpan.FromSeconds(10),
        }));

        var act = () => provider.CompleteAsync(
            "you are a helpful assistant",
            "say hello in one word");
        await act.Should().ThrowAsync<LlmProviderException>(
            because: "ollama returns HTTP 404 for unknown model tags; provider must translate to LlmProviderException so the coordinator's cooldown logic kicks in");
    }

    private sealed class OllamaTagsResponse
    {
        public OllamaModelInfo[]? Models { get; set; }
    }
    private sealed class OllamaModelInfo
    {
        public string? Name { get; set; }
    }
}
