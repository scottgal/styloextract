using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StyloExtract.Abstractions;

namespace StyloExtract.Llm.Ollama;

/// <summary>
/// Registers <see cref="OllamaTextProvider"/> as the
/// <see cref="ILlmTextProvider"/> implementation. Pair with
/// <c>AddStyloExtractLlmInducer</c> from the StyloExtract.AspNetCore
/// package to wire the full template-induction stack.
///
/// <para>Operator wiring (Program.cs):</para>
/// <code>
/// services.AddStyloExtract();
/// services.AddStyloExtractOperatorTemplates("config/templates");
/// services.AddOllamaTextProvider(o => o.Model = "gemma4:12b-it-qat");
/// services.AddStyloExtractLlmInducer("config/templates");
/// </code>
/// </summary>
public static class OllamaServiceCollectionExtensions
{
    public static IServiceCollection AddOllamaTextProvider(
        this IServiceCollection services,
        Action<OllamaTextProviderOptions>? configure = null)
    {
        services.AddOptions<OllamaTextProviderOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }
        // Named HttpClient so operators can customise (proxy, retry policies,
        // etc.) via the standard AddHttpClient extension methods if they
        // want. We resolve it by name from the provider.
        services.AddHttpClient<OllamaTextProvider>();
        services.TryAddSingleton<ILlmTextProvider>(sp =>
            sp.GetRequiredService<OllamaTextProvider>());
        return services;
    }
}
