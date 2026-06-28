namespace StyloExtract.Abstractions.TemplateEnrichment;

/// <summary>
/// Optional observer the <c>TemplateEnrichmentCoordinator</c> notifies
/// around each LLM call so consumers (lucidVIEW FULL's status bar, a
/// dashboard, a telemetry sink) can show that "the LLM is currently
/// running for host X". CPU-only dogfood LLM calls run in the tens of
/// seconds; without a visible signal the host appears stuck.
///
/// <para>The observer is best-effort. Implementations must not throw out
/// of <see cref="LlmCallStarted"/> or <see cref="LlmCallEnded"/> — the
/// coordinator does not catch exceptions from these calls. They are also
/// invoked synchronously on the coordinator's drain task; keep them
/// non-blocking (post to a dispatcher, write to a channel, etc.).</para>
/// </summary>
public interface ILlmActivityObserver
{
    /// <summary>Fires immediately before the inducer's LLM call begins.</summary>
    void LlmCallStarted(string host, EnrichmentJobKind kind);

    /// <summary>
    /// Fires after the LLM call returns (or throws / is cancelled).
    /// <paramref name="success"/> is true iff a non-null, validated template
    /// was produced and written to the operator-template root.
    /// </summary>
    void LlmCallEnded(string host, EnrichmentJobKind kind, bool success);
}