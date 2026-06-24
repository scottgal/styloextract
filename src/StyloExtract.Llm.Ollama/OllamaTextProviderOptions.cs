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
    /// Model tag to use. Default is the design's recommended Gemma 4 E4B
    /// quantised-aware-training variant — Apache 2.0, 128 K context, ~6 GB
    /// on disk, runs on a workstation CPU. Operators with more RAM should
    /// consider <c>gemma4:12b-it-qat</c> for stronger code/HTML output.
    /// </summary>
    public string Model { get; set; } = "gemma4:e4b-it-qat";

    /// <summary>
    /// Per-call timeout. Gemma 4 E4B on CPU runs the induction prompt in
    /// 10-30 s; the default 60 s leaves headroom for cold-cache loads
    /// and the 12B variant.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Sampling temperature. Template induction wants deterministic
    /// structured output, so the default is low (0.1). Operators
    /// inducing exploratory templates can dial it up.
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    /// Max tokens for the completion. Template YAML is small;
    /// 2 KB / ~600 tokens is plenty. Bound it so a runaway model
    /// can't pin the LLM for minutes.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 1024;
}
