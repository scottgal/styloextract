namespace StyloExtract.Llm.Ollama;

/// <summary>
/// Configuration for <see cref="OllamaTextProvider"/>. Bind from
/// <c>StyloExtract:Llm:Ollama</c> in your appsettings.json (or set
/// via the <c>AddStyloExtractLlmInducer</c> options callback).
/// </summary>
public sealed class OllamaTextProviderOptions
{
    /// <summary>
    /// Base URL of the Ollama server. Default talks to a local Ollama on
    /// the gateway box. Operators with a shared inference node point at it.
    /// </summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Model tag to use. Default is <c>qwen3.5:4b</c> — Apache 2.0, ~3 GB on
    /// disk, runs on a workstation CPU. Empirically the best F1 on the WCXB
    /// template-induction bench (0.805) at less than a third of the size of
    /// Gemma 4 E4B.
    ///
    /// <para>
    /// Alternatives, per <c>tests/StyloExtract.Llm.Benchmark/README.md</c>:
    /// <list type="bullet">
    /// <item><c>qwen2.5-coder:3b</c> — 2 GB, ~21 s, F1 0.767. Code-trained;
    ///   sometimes beats the 4 B model on CSS-selector tasks. Best
    ///   smaller-and-faster pick.</item>
    /// <item><c>qwen3:1.7b</c> — 2 GB, ~12 s, F1 0.618. Smallest viable;
    ///   2× faster than the default.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Avoid models with thinking-mode budgets that empty the response
    /// (qwen3:4b, phi3.5, phi4-mini), reasoning-tagged models that burn
    /// output on chain-of-thought (deepseek-r1), and models too small for
    /// CSS-selector reasoning (llama3.2:1b, smollm2:*, granite3.1-moe:*).
    /// </para>
    /// </summary>
    public string Model { get; set; } = "qwen3.5:4b";

    /// <summary>
    /// Per-call timeout. Qwen 3.5 4B on CPU runs the induction prompt in
    /// 30-60 s; the default 90 s leaves headroom for cold-cache loads
    /// and per-page variance.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Sampling temperature. Template induction wants deterministic
    /// structured output, so the default is low (0.1). Operators
    /// inducing exploratory templates can dial it up.
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    /// Max tokens for the completion. Template YAML is small;
    /// 2 KB / ~600 tokens is plenty. Reasoning-tagged models (Gemma 4,
    /// Qwen 3, etc.) burn tokens on chain-of-thought before producing the
    /// answer, so the budget needs headroom even though the final YAML
    /// itself is short. 4 096 leaves room for ~3 KB of thinking + a 1 KB
    /// YAML answer. Bound it so a runaway model can't pin the LLM for
    /// minutes.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4096;
}
