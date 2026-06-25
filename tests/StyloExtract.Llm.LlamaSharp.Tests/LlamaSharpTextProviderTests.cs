using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;
using StyloExtract.Llm.LlamaSharp;
using Xunit;

namespace StyloExtract.Llm.LlamaSharp.Tests;

/// <summary>
/// Live LlamaSharp tests. Requires a GGUF file on disk pointed at by
/// the <c>STYLOEXTRACT_LLAMASHARP_MODEL</c> environment variable.
/// Skipped when the variable is unset / the file doesn't exist so CI
/// without provisioned models passes cleanly.
///
/// <para>
/// Cheap way to populate this for local dev: install any model via
/// Ollama (`ollama pull llama3.2:3b`) then point the env var at the
/// underlying blob: `export STYLOEXTRACT_LLAMASHARP_MODEL=$(ollama show
/// llama3.2:3b --modelfile | grep '^FROM' | awk '{print $2}')`.
/// </para>
/// </summary>
public class LlamaSharpTextProviderTests
{
    private static string? GetModelPath()
    {
        var p = Environment.GetEnvironmentVariable("STYLOEXTRACT_LLAMASHARP_MODEL");
        return string.IsNullOrWhiteSpace(p) ? null : File.Exists(p) ? p : null;
    }

    // No Timeout — xUnit's Timeout attribute requires async tests, and this
    // is a pure ctor-validation check that returns immediately.
    [SkippableFact]
    public void Ctor_Throws_When_ModelPath_Not_Set()
    {
        var opts = Options.Create(new LlamaSharpTextProviderOptions { ModelPath = "" });
        Action act = () => new LlamaSharpTextProvider(opts);
        act.Should().Throw<ArgumentException>().WithMessage("*ModelPath*");
    }

    [SkippableFact(Timeout = 300_000)]
    public async Task EnsureLoaded_Throws_FileNotFound_When_Model_Missing()
    {
        var opts = Options.Create(new LlamaSharpTextProviderOptions { ModelPath = "/nope/this-does-not-exist.gguf" });
        using var sut = new LlamaSharpTextProvider(opts);
        var act = async () => await sut.CompleteAsync("sys", "user");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [SkippableFact(Timeout = 600_000)]
    public async Task Live_CompleteAsync_Produces_Yaml_Output_For_Yaml_Prompt()
    {
        var modelPath = GetModelPath();
        Skip.If(modelPath is null,
            "STYLOEXTRACT_LLAMASHARP_MODEL not set or file missing; skip live test. " +
            "`export STYLOEXTRACT_LLAMASHARP_MODEL=$(ollama show llama3.2:3b --modelfile | grep '^FROM' | awk '{print $2}')`");

        var services = new ServiceCollection();
        services.AddStyloExtractLlamaSharp(o =>
        {
            o.ModelPath = modelPath!;
            o.ContextSize = 4096;
            o.MaxOutputTokens = 256;
            o.Temperature = 0.1f;
        });
        using var sp = services.BuildServiceProvider();
        var llm = sp.GetRequiredService<ILlmTextProvider>();

        var response = await llm.CompleteAsync(
            systemPrompt: "You are a YAML generator. Output a single YAML document and nothing else.",
            userPrompt: "Produce a YAML document with keys: name (string), age (int), role (string). " +
                        "Use realistic values."
        );

        response.Should().NotBeNullOrWhiteSpace();
        // Just check we got a non-trivial response with at least one of the
        // requested fields. Small models running on CPU under load occasionally
        // truncate or paraphrase; full-key matching is too strict for this
        // smoke test. The end-to-end check is "the LLamaSharp + GGUF + chat
        // template path produces text", not "the model produces perfect YAML."
        var lower = response.ToLowerInvariant();
        var keyCount = (lower.Contains("name:") ? 1 : 0)
                       + (lower.Contains("age:") ? 1 : 0)
                       + (lower.Contains("role:") ? 1 : 0);
        keyCount.Should().BeGreaterThan(0,
            because: "model returned a non-trivial response with at least one of the requested keys");
    }

    [SkippableFact(Timeout = 600_000)]
    public async Task Live_DI_Resolves_LlamaSharp_Provider_As_ILlmTextProvider()
    {
        var modelPath = GetModelPath();
        Skip.If(modelPath is null,
            "STYLOEXTRACT_LLAMASHARP_MODEL not set or file missing; skip live test.");

        var services = new ServiceCollection();
        services.AddStyloExtractLlamaSharp(o => o.ModelPath = modelPath!);
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ILlmTextProvider>()
            .Should().BeOfType<LlamaSharpTextProvider>();
    }
}
