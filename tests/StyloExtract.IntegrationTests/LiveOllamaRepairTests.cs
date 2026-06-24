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
/// Real-Ollama end-to-end for the repair path. Same SkippableFact shape as
/// LiveOllamaInductionTests: skipped if Ollama is unreachable or no Gemma 4
/// variant is installed locally. Exercises the whole chain — DomCleaner →
/// DomSkeletonRenderer → LlmInducerPrompts.SystemRepair → OllamaTextProvider
/// HTTP → LlmTemplateInducer.RepairFromSkeletonAsync YAML extraction →
/// YamlOperatorTemplateLoader.Parse.
/// </summary>
public class LiveOllamaRepairTests
{
    private const string OllamaUrl = "http://localhost:11434";

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
            <a href="/">Home</a><a href="/shop">Shop</a>
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

    // Deliberately wrong: MainContent points at <footer>, which holds copyright
    // chrome rather than the product description.
    private const string BrokenTemplateYaml = """
        host: acme.example
        description: Template that mistakenly targets the footer as MainContent.
        version: 1
        rules:
          - role: MainContent
            selectors:
              - footer.site-footer
            confidence: 0.9
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
                if (available.Contains(candidate)) return (true, candidate);
            var anyGemma4 = available.FirstOrDefault(n => n!.StartsWith("gemma4:", StringComparison.OrdinalIgnoreCase));
            return anyGemma4 is null ? (true, null) : (true, anyGemma4);
        }
        catch
        {
            return (false, null);
        }
    }

    [SkippableFact(Timeout = 120_000)]
    public async Task Repair_Round_Trips_Through_Live_Gemma4_Ollama()
    {
        var (ollamaUp, model) = await ProbeAsync();
        Skip.IfNot(ollamaUp, $"Ollama unreachable at {OllamaUrl}; `ollama serve` or skip.");
        Skip.If(model is null,
            $"No Gemma 4 model installed locally. Tried: {string.Join(", ", CandidateModels)}. " +
            "`ollama pull gemma4:e2b` (or another gemma4 variant) to enable.");

        using var http = new HttpClient();
        var opts = Options.Create(new OllamaTextProviderOptions
        {
            OllamaUrl = OllamaUrl,
            Model = model!,
            Timeout = TimeSpan.FromSeconds(90),
            Temperature = 0.1,
            MaxOutputTokens = 1024,
        });
        var provider = new OllamaTextProvider(http, opts);
        var inducer = new LlmTemplateInducer(provider);

        var doc = new AngleSharpHtmlDomParser().Parse(ProductPageHtml);
        new DomCleaner().Clean(doc);
        var skeleton = new DomSkeletonRenderer().Render(doc);
        skeleton.Should().NotBeNullOrEmpty();

        OperatorTemplate? template;
        try
        {
            template = await inducer.RepairFromSkeletonAsync(
                skeleton, "acme.example", BrokenTemplateYaml,
                badMarkdownSample: "© 2026 Acme · Privacy · Terms");
        }
        catch (LlmProviderException ex) when (ex.Message.Contains("model"))
        {
            Skip.If(true, $"Live LLM rejected the prompt: {ex.Message}");
            return;
        }

        if (template is null)
        {
            Skip.If(true,
                $"Model {model} didn't produce a parseable YAML template. " +
                "Try a larger model (gemma4:12b-it-qat) for repair-quality output.");
            return;
        }

        template.Host.Should().Be("acme.example");
        template.Rules.Should().NotBeEmpty();
        template.Rules.Any(r => r.Selectors.Count > 0).Should().BeTrue();

        // The repaired template's MainContent rule must NOT still point at footer.
        // That is the whole point of the exercise: the broken template was wrong,
        // a correct repair targets the actual content container.
        var mainRule = template.Rules.FirstOrDefault(r => r.Role == BlockRole.MainContent);
        if (mainRule is not null)
        {
            mainRule.Selectors
                .Any(s => s.Contains("footer", StringComparison.OrdinalIgnoreCase))
                .Should().BeFalse(
                    because: "the broken template's MainContent pointed at footer; a real repair fixes that");
        }

        var emitted = OperatorTemplateYamlEmitter.Emit(template);
        var reparsed = YamlOperatorTemplateLoader.Parse(emitted);
        reparsed.Host.Should().Be(template.Host);
        reparsed.Rules.Count.Should().Be(template.Rules.Count);
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
