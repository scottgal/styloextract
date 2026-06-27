namespace StyloExtract.Abstractions;

/// <summary>
/// Identifies which inducer produced a <see cref="TemplateObservation"/>.
/// Stored as a byte in the rule-observations corpus so Phase 2 mining can
/// segment patterns by source (e.g. compare LLM-induced selector stability
/// vs heuristic-induced).
/// </summary>
public enum InducerKind : byte
{
    /// <summary>
    /// Rule emitted by the deterministic heuristic inducer in
    /// <c>StyloExtract.Heuristics.ExtractorInducer</c>.
    /// </summary>
    Heuristic = 0,

    /// <summary>
    /// Rule emitted by an LLM-template-induction job
    /// (<c>StyloExtract.Core.Llm</c>).
    /// </summary>
    Llm = 1,

    /// <summary>
    /// Rule emitted by the LLM repair pipeline (existing-template fix-up).
    /// </summary>
    Repair = 2,

    /// <summary>
    /// Rule sourced from a hand-authored operator template (YAML).
    /// </summary>
    Operator = 3,
}
