using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;

namespace StyloExtract.Llm.Ollama;

/// <summary>
/// Talks to a local (or remote) Ollama server via the <c>/api/chat</c>
/// endpoint. Streaming disabled — the whole response is returned to the
/// caller as a single string, which <c>LlmTemplateInducer</c> then
/// parses. Default model is Gemma 4 E4B per the design.
///
/// <para>
/// AOT-clean: pure HttpClient + System.Text.Json source generator
/// (OllamaJsonContext below). No reflection, no dynamic types.
/// </para>
/// </summary>
public sealed class OllamaTextProvider : ILlmTextProvider
{
    private readonly HttpClient _http;
    private readonly OllamaTextProviderOptions _options;
    private readonly ILogger<OllamaTextProvider>? _logger;

    public OllamaTextProvider(
        HttpClient http,
        IOptions<OllamaTextProviderOptions> options,
        ILogger<OllamaTextProvider>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger;

        // BaseAddress + Timeout from options. Caller can also configure
        // these via AddHttpClient extensions if they want a shared handler;
        // we don't overwrite if BaseAddress is already set by the caller.
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(_options.OllamaUrl);
        }
        _http.Timeout = _options.Timeout;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var request = new OllamaChatRequest
        {
            Model = _options.Model,
            Stream = false,
            Messages = new[]
            {
                new OllamaChatMessage { Role = "system", Content = systemPrompt },
                new OllamaChatMessage { Role = "user", Content = userPrompt },
            },
            Options = new OllamaChatOptions
            {
                Temperature = _options.Temperature,
                NumPredict = _options.MaxOutputTokens,
            },
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(
                "/api/chat",
                request,
                OllamaJsonContext.Default.OllamaChatRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout fires as OperationCanceledException with
            // a NON-canceled token. Translate to LlmProviderException so
            // the coordinator's cooldown logic kicks in, not the
            // graceful-shutdown path.
            throw new LlmProviderException(
                $"Ollama timed out after {_options.Timeout}; consider raising OllamaTextProviderOptions.Timeout or switching to a smaller model.");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmProviderException(
                $"Ollama at {_options.OllamaUrl} unreachable: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
            throw new LlmProviderException(
                $"Ollama returned HTTP {(int)response.StatusCode}: {Snip(body, 300)}");
        }

        OllamaChatResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync(
                OllamaJsonContext.Default.OllamaChatResponse,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new LlmProviderException("Ollama response was not valid JSON", ex);
        }

        var content = parsed?.Message?.Content;
        if (string.IsNullOrEmpty(content))
        {
            // Fallback: some models put the answer in `thinking` when their
            // chain-of-thought trace IS the answer (we asked for YAML; CoT-
            // enabled models sometimes return YAML inside thinking).
            var thinking = parsed?.Message?.Thinking;
            if (!string.IsNullOrEmpty(thinking))
            {
                _logger?.LogInformation("Ollama returned content in `thinking` field, not `content`; using it; model={Model}", _options.Model);
                return thinking;
            }
            _logger?.LogWarning("Ollama returned empty content; model={Model}", _options.Model);
            throw new LlmProviderException("Ollama response had empty message.content");
        }
        return content;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
        catch { return ""; }
    }

    private static string Snip(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}

// Wire formats for /api/chat. Kept internal so consumers stay decoupled.

internal sealed class OllamaChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    // Gemma 4 (and other reasoning-tagged Ollama models) default to emitting a
    // chain-of-thought "thinking" trace BEFORE the actual content. With our
    // small NumPredict budget the model exhausts its output tokens on the
    // thinking trace and returns message.content empty. Disable thinking so
    // every output token goes to the actual response.
    [JsonPropertyName("think")] public bool Think { get; set; }
    [JsonPropertyName("messages")] public OllamaChatMessage[] Messages { get; set; } = Array.Empty<OllamaChatMessage>();
    [JsonPropertyName("options")] public OllamaChatOptions? Options { get; set; }
}

internal sealed class OllamaChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    // Gemma 4 and other reasoning-tagged models put their chain-of-thought
    // in a separate `thinking` field. We try `content` first; if it's empty
    // we fall back to `thinking` (the model occasionally emits the answer
    // there when both fields are returned).
    [JsonPropertyName("thinking")] public string? Thinking { get; set; }
}

internal sealed class OllamaChatOptions
{
    [JsonPropertyName("temperature")] public double Temperature { get; set; }
    [JsonPropertyName("num_predict")] public int NumPredict { get; set; }
}

internal sealed class OllamaChatResponse
{
    [JsonPropertyName("message")] public OllamaChatMessage? Message { get; set; }
    [JsonPropertyName("done")] public bool Done { get; set; }
}

[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatResponse))]
internal partial class OllamaJsonContext : JsonSerializerContext { }
