using System.Text;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;

namespace StyloExtract.Llm.LlamaSharp;

/// <summary>
/// In-process CPU LLM backend for StyloExtract via LLamaSharp.
///
/// <para>
/// Loads a single GGUF model lazily on first call, holds it for the
/// lifetime of the process. Designed to be registered as a DI singleton —
/// model load (~1–3 GB) and KV-cache allocation are expensive and must
/// not happen per-request.
/// </para>
///
/// <para>
/// Same <see cref="ILlmTextProvider"/> contract as
/// <c>OllamaTextProvider</c>, so callers (LlmTemplateInducer, the
/// production enrichment coordinator, the <c>template train</c> CLI)
/// don't change. Operators who don't want an Ollama server process pick
/// this backend instead.
/// </para>
///
/// <para>
/// The provider speaks the model's chat template — system + user
/// messages are formatted via <see cref="ChatHistory"/> + the model's
/// declared chat template (read from the GGUF metadata), so the same
/// system prompt the inducer writes for Ollama works here unchanged.
/// </para>
/// </summary>
public sealed class LlamaSharpTextProvider : ILlmTextProvider, IDisposable
{
    private readonly LlamaSharpTextProviderOptions _options;
    private readonly ILogger<LlamaSharpTextProvider>? _logger;
    private readonly SemaphoreSlim _generationLock = new(1, 1);

    // Lazy-init: load weights + model params on the first generation
    // call so DI graph construction stays fast and weight files don't
    // get touched until someone actually generates. The StatelessExecutor
    // is created PER CALL because its SystemMessage / ApplyTemplate
    // properties are init-only — but the executor itself is a thin
    // wrapper around the (cached) weights, so per-call construction is
    // cheap.
    private readonly object _loadLock = new();
    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private bool _disposed;

    public LlamaSharpTextProvider(
        IOptions<LlamaSharpTextProviderOptions> options,
        ILogger<LlamaSharpTextProvider>? logger = null)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        if (string.IsNullOrWhiteSpace(_options.ModelPath))
            throw new ArgumentException("ModelPath is required", nameof(options));
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureLoaded();

        // Per-call executor: the StatelessExecutor's SystemMessage and
        // ApplyTemplate properties are init-only, so we construct a new
        // executor for every call with the system prompt baked in. The
        // weights stay cached; constructing the executor wrapper is
        // cheap (no allocation beyond the wrapper itself).
        var executor = new StatelessExecutor(_weights!, _modelParams!)
        {
            SystemMessage = systemPrompt,
            ApplyTemplate = true,
        };

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _options.MaxOutputTokens,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = _options.Temperature,
            },
            // Cover the stop tokens of every model family we benchmark so the
            // generator halts at the model's natural turn boundary rather than
            // continuing into the template's next-turn structure (which the
            // model will happily echo back if we don't stop it). Qwen and
            // Phi use <|im_end|> / <|end|>; Llama 3+ uses <|eot_id|>;
            // Gemma uses <end_of_turn> / <|turn>; legacy uses </s>.
            AntiPrompts = new List<string>
            {
                "<|im_end|>", "<|end|>",         // Qwen, Phi
                "<|eot_id|>",                    // Llama 3+
                "<end_of_turn>", "<|turn>",      // Gemma 4 family
                "</s>",                          // legacy SentencePiece
            },
        };

        // Serialise concurrent calls. The single-context model means we
        // can only generate one response at a time; concurrent
        // CompleteAsync calls have to queue.
        await _generationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = new CancellationTokenSource(_options.Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            var sb = new StringBuilder();
            try
            {
                await foreach (var token in executor.InferAsync(userPrompt, inferenceParams, linkedCts.Token))
                {
                    sb.Append(token);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new LlmProviderException(
                    $"LlamaSharp generation timed out after {_options.Timeout}; raise Timeout or pick a smaller model.");
            }
            catch (Exception ex) when (ex is not LlmProviderException)
            {
                throw new LlmProviderException(
                    $"LlamaSharp generation failed: {ex.Message}", ex);
            }

            var result = sb.ToString();
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new LlmProviderException("LlamaSharp returned empty response");
            }
            return result;
        }
        finally
        {
            _generationLock.Release();
        }
    }

    /// <summary>
    /// Eagerly load the model + create the executor. Safe to call from
    /// startup (warm-up); not required — the first <see cref="CompleteAsync"/>
    /// call loads on demand.
    /// </summary>
    public void EnsureLoaded()
    {
        if (_weights is not null) return;
        lock (_loadLock)
        {
            if (_weights is not null) return;
            if (!File.Exists(_options.ModelPath))
            {
                throw new FileNotFoundException(
                    $"LlamaSharp model not found at {_options.ModelPath}. " +
                    "Download a GGUF model file (e.g. from HuggingFace) and point ModelPath at it.",
                    _options.ModelPath);
            }

            _logger?.LogInformation(
                "loading GGUF model from {ModelPath} (context={ContextSize}, threads={Threads}, gpuLayers={GpuLayerCount})",
                _options.ModelPath, _options.ContextSize, _options.Threads, _options.GpuLayerCount);

            _modelParams = new ModelParams(_options.ModelPath)
            {
                ContextSize = _options.ContextSize,
                Threads = _options.Threads > 0 ? _options.Threads : null,
                GpuLayerCount = _options.GpuLayerCount,
            };
            _weights = LLamaWeights.LoadFromFile(_modelParams);

            _logger?.LogInformation(
                "GGUF model loaded; params={ParameterCount}, size_bytes={SizeBytes}, context_window={ContextSize}",
                _weights.ParameterCount, _weights.SizeInBytes, _weights.ContextSize);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generationLock.Dispose();
        _weights?.Dispose();
    }
}
