namespace StyloExtract.Abstractions;

/// <summary>
/// Minimal LLM-text-completion abstraction. Deliberately tiny: the provider
/// returns raw text; consumers parse, validate, and act on it themselves.
/// This is shared across the entire "response parser family" sketched in
/// <c>docs/ml-classifier-v2-design.md</c> — template induction is the
/// first member, PII redaction / content-safety verify / regulatory
/// disclosure injection are future siblings that reuse the same client
/// wiring.
///
/// <para>
/// Implementations: <c>OllamaTextProvider</c> (default, talks to a local
/// Ollama), <c>StyloBotLlmAdapter</c> (bridges StyloBot's existing
/// <c>ILlmProvider</c> in stylobot deployments), or any operator-supplied
/// provider that wraps a cloud LLM SDK.
/// </para>
///
/// <para>
/// Always called on the slow path (background coordinator), never on the
/// hot path. Per-call latency budgets in the tens of seconds are
/// acceptable; failing fast is preferable to retry-storming when the
/// backend is down — surface the error and let the coordinator's
/// per-host cooldown handle it.
/// </para>
/// </summary>
public interface ILlmTextProvider
{
    /// <summary>
    /// Complete <paramref name="userPrompt"/> under the system instruction
    /// <paramref name="systemPrompt"/>. Returns the raw model output.
    /// Implementations should NOT interpret or transform the response;
    /// downstream consumers own parsing.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancellation requested.</exception>
    /// <exception cref="LlmProviderException">Backend refused / timed out / returned a non-success response.</exception>
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised by <see cref="ILlmTextProvider"/> implementations on backend
/// failures (network errors, HTTP non-2xx, deserialisation failure).
/// Consumers translate this into a logged failure + per-host cooldown;
/// it should never escape onto the request hot path because the inducer
/// runs in a background coordinator.
/// </summary>
public sealed class LlmProviderException : Exception
{
    public LlmProviderException(string message) : base(message) { }
    public LlmProviderException(string message, Exception inner) : base(message, inner) { }
}
