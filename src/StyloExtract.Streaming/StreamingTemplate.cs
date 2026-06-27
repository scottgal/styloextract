using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

/// <summary>
/// A streaming template — three <see cref="IdentityClaim"/> tripwires the
/// scanner state machine fires on as the response stream flows past.
///
/// Task 4 of Phase 1 (alpha.24) replaced the alpha.16..23 MinHash
/// <c>TemplateFence</c> shape with this tripwire shape. The state machine
/// transitions on EXACT identity-claim match against the tokenizer's
/// per-event hash data:
///
/// <list type="bullet">
///   <item><description>AwaitPrefix → match <see cref="PrefixTripwire"/> →
///   AwaitContentStart</description></item>
///   <item><description>AwaitContentStart → match <see cref="ContentStartTripwire"/> →
///   Capturing (depth snapshot taken)</description></item>
///   <item><description>Capturing → close-event matching <see cref="ContentEndTripwire"/>
///   AND depth ≤ snapshot → Captured</description></item>
///   <item><description>Bailout when <see cref="BailoutBytes"/> consumed without
///   a state change, or when capture exceeds <see cref="MaxCaptureBytes"/>.</description></item>
/// </list>
///
/// Trade-off: the alpha.21..23 LSH bands gave soft tolerance across DOM
/// diffs. Tripwires match exactly on stable-by-construction identifiers
/// (the identity-aware inducer from Task 2 picks stable claims) — drift
/// becomes a clean miss → refit signal, which is what we wanted anyway.
/// </summary>
public sealed record StreamingTemplate
{
    public required Guid TemplateId { get; init; }

    /// <summary>
    /// Host the template was induced or registered for. Lookup key for
    /// <see cref="IStreamingTemplateStore.GetByHostAsync"/>. Empty string for
    /// pre-host-keyed templates. Lowercase + canonical form.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Identity claim the scanner waits on while in AwaitPrefix. First
    /// matching open-event transitions to AwaitContentStart. Typically the
    /// outer page chrome opener — header / nav / banner / top-level main.
    /// </summary>
    public required IdentityClaim PrefixTripwire { get; init; }

    /// <summary>
    /// Identity claim the scanner waits on while in AwaitContentStart. First
    /// matching open-event transitions to Capturing and snapshots the DOM
    /// depth. Typically the content-region opener — article / main / a
    /// paragraph-cluster wrapper.
    /// </summary>
    public required IdentityClaim ContentStartTripwire { get; init; }

    /// <summary>
    /// Identity claim the scanner waits on while in Capturing. First matching
    /// close-event at depth ≤ snapshot-depth transitions to Captured. The
    /// depth gate prevents premature termination from nested elements that
    /// happen to share the same identity.
    /// </summary>
    public required IdentityClaim ContentEndTripwire { get; init; }

    /// <summary>
    /// Maximum bytes the scanner may consume in a non-Capturing state without
    /// a state transition before latching Bailout. Bounds the scanner's worst
    /// case on pages that don't carry the expected fences.
    /// </summary>
    public required int BailoutBytes { get; init; }

    /// <summary>
    /// Maximum bytes the captured content region may span before the scanner
    /// bails. Bounds the worst case on pages whose ContentEnd tripwire never
    /// fires (e.g. infinite-scroll feeds).
    /// </summary>
    public required int MaxCaptureBytes { get; init; }

    /// <summary>
    /// Monotonically increasing version for this host's streaming template.
    /// Starts at 1 on initial induction; <see cref="StreamingRefitOrchestrator"/>
    /// bumps it whenever drift triggers a re-induction.
    /// </summary>
    public int Version { get; init; } = 1;
}
