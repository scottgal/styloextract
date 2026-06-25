namespace StyloExtract.Llm.LlamaSharp;

/// <summary>
/// Configuration for <see cref="LlamaSharpTextProvider"/>. Bound via the
/// usual <c>services.AddOptions&lt;LlamaSharpTextProviderOptions&gt;().Configure(...)</c>
/// pattern or directly with the <see cref="LlamaSharpServiceCollectionExtensions"/>
/// helpers.
/// </summary>
public sealed class LlamaSharpTextProviderOptions
{
    /// <summary>
    /// Absolute path to the GGUF model file on local disk. No download
    /// behaviour; operators provision the file out-of-band (e.g. download
    /// from HuggingFace once at deploy time).
    /// </summary>
    public string ModelPath { get; set; } = "";

    /// <summary>
    /// Context window size in tokens. Default 8 192 fits the largest
    /// catalogue + skeleton our template-induction prompt generates
    /// (~3 KB) plus room for the response. Larger contexts use more RAM
    /// and slow down inference; smaller contexts truncate the prompt.
    /// </summary>
    public uint ContextSize { get; set; } = 8192;

    /// <summary>
    /// Number of CPU threads to use for generation. <c>0</c> = let
    /// llama.cpp pick (defaults to physical cores). Override only if
    /// you're sharing the host with other CPU-heavy workloads.
    /// </summary>
    public int Threads { get; set; } = 0;

    /// <summary>
    /// GPU layers to offload. <c>0</c> = pure CPU (the default target).
    /// Set higher when a GPU is available and the model supports it.
    /// </summary>
    public int GpuLayerCount { get; set; } = 0;

    /// <summary>
    /// Temperature for sampling. Template induction wants near-zero
    /// (deterministic) output — the model picks selectors not stories.
    /// </summary>
    public float Temperature { get; set; } = 0.1f;

    /// <summary>
    /// Max tokens to generate per call. Reasoning-tagged models burn
    /// tokens on chain-of-thought before the answer, so the budget needs
    /// headroom even though the YAML itself is short. 4 096 leaves room
    /// for ~3 KB of thinking + a 1 KB YAML answer.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// Per-call timeout. CPU inference at 5-10 tokens/s on a workstation
    /// CPU means a 1 KB answer takes 30-90 s; the default 180 s leaves
    /// headroom for cold cache + thinking.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(180);
}
