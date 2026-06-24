using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StyloExtract.Abstractions;
using StyloExtract.Abstractions.TemplateEnrichment;
using StyloExtract.Core.Llm;
using StyloExtract.Core.Skeleton;
using StyloExtract.Core.TemplateEnrichment;
using StyloExtract.Llm.Ollama;
using Xunit;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// Wires the recommended phase-3c stack end-to-end through
/// AddStyloExtract + AddOllamaTextProvider + AddStyloExtractLlmInducer
/// and asserts each layer resolves with the right concrete types. The
/// hosted coordinator is registered but not started here; that's covered
/// by TemplateEnrichmentCoordinatorTests in the IntegrationTests project.
/// </summary>
public class LlmInducerDiCompositionTests
{
    [Fact]
    public void Stack_Composes_With_Ollama_Provider_And_Resolves_Every_Layer()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloextract-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddStyloExtract();
            services.AddStyloExtractOperatorTemplates(root);
            services.AddOllamaTextProvider(o => o.Model = "gemma4:e4b-it-qat");
            services.AddStyloExtractLlmInducer(root);

            using var sp = services.BuildServiceProvider();

            // Each layer resolves.
            sp.GetRequiredService<ILlmTextProvider>().Should().BeOfType<OllamaTextProvider>();
            sp.GetRequiredService<DomSkeletonRenderer>().Should().NotBeNull();
            sp.GetRequiredService<ITemplateEnrichmentQueue>().Should().BeOfType<InMemoryTemplateEnrichmentQueue>();
            sp.GetRequiredService<LlmTemplateInducer>().Should().NotBeNull();

            // Hosted services include the coordinator.
            var hosted = sp.GetServices<IHostedService>().ToList();
            hosted.Should().Contain(h => h is TemplateEnrichmentCoordinator);

            // OperatorTemplateStore is still resolvable and the coordinator
            // got it injected (cannot directly probe; check side-effect via
            // separate test in IntegrationTests).
            sp.GetRequiredService<IOperatorTemplateStore>().Should().NotBeNull();
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void AddStyloExtractLlmInducer_Without_Provider_Wired_Fails_Loudly_On_Resolve()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloextract-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddStyloExtract();
            services.AddStyloExtractLlmInducer(root);
            // Deliberately no AddOllamaTextProvider — the inducer requires
            // an ILlmTextProvider; resolving it should fail explicitly
            // rather than silently no-op.

            using var sp = services.BuildServiceProvider();
            var act = () => sp.GetRequiredService<LlmTemplateInducer>();
            act.Should().Throw<InvalidOperationException>()
                .Where(ex => ex.Message.Contains(nameof(ILlmTextProvider)));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Inducer_Without_OperatorTemplate_Store_Still_Composes()
    {
        // The store is optional; the coordinator handles its absence by
        // simply not skipping the LLM call.
        var root = Path.Combine(Path.GetTempPath(), "styloextract-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddStyloExtract();
            services.AddOllamaTextProvider();
            services.AddStyloExtractLlmInducer(root);

            using var sp = services.BuildServiceProvider();
            sp.GetService<IOperatorTemplateStore>().Should().BeNull();
            sp.GetRequiredService<LlmTemplateInducer>().Should().NotBeNull();
            sp.GetServices<IHostedService>().Should().Contain(h => h is TemplateEnrichmentCoordinator);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
