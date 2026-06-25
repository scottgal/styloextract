using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;

namespace StyloExtract.Llm.LlamaSharp;

/// <summary>
/// DI helpers for wiring the in-process LlamaSharp backend as the
/// <see cref="ILlmTextProvider"/>.
///
/// <para>
/// Operators who don't want to run a separate Ollama server (the
/// default <c>OllamaTextProvider</c> backend) call this instead and
/// point at a GGUF file on disk. Same <see cref="ILlmTextProvider"/>
/// contract — <c>LlmTemplateInducer</c>, the production enrichment
/// coordinator, and the <c>template train</c> CLI all work unchanged.
/// </para>
///
/// <para>
/// Registered as a singleton: loading a GGUF model (~1–3 GB on disk,
/// ~2–4 GB in RAM after warming the KV cache) takes seconds; we hold
/// it for the process lifetime. Concurrent calls serialise on a
/// SemaphoreSlim because the single context can only generate one
/// response at a time.
/// </para>
/// </summary>
public static class LlamaSharpServiceCollectionExtensions
{
    /// <summary>
    /// Register the LlamaSharp backend. <paramref name="configure"/>
    /// sets <see cref="LlamaSharpTextProviderOptions.ModelPath"/> (required)
    /// and any other tunables.
    ///
    /// <example>
    /// <code>
    /// services.AddStyloExtractLlamaSharp(o =>
    /// {
    ///     o.ModelPath = "/var/models/qwen3.5-4b-q4_k_m.gguf";
    ///     o.ContextSize = 8192;
    ///     o.Threads = 8;
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddStyloExtractLlamaSharp(
        this IServiceCollection services,
        Action<LlamaSharpTextProviderOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        services.AddOptions<LlamaSharpTextProviderOptions>();

        // TryAddSingleton so operators who also register Ollama keep
        // whichever they wired first. Mixed scenarios should explicitly
        // remove the prior registration.
        services.TryAddSingleton<ILlmTextProvider>(sp =>
            new LlamaSharpTextProvider(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlamaSharpTextProviderOptions>>(),
                sp.GetService<ILogger<LlamaSharpTextProvider>>()));

        return services;
    }
}
