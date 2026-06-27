using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

/// <summary>
/// A streaming template — three <see cref="BytePattern"/>s the byte-pattern
/// scanner (<see cref="BytePatternScanner"/> / <see cref="IncrementalBytePatternScanner"/>)
/// fires on as the response stream flows past.
///
/// Task 13 of Phase 1 replaced the Task 4 tripwire shape (three
/// <see cref="IdentityClaim"/>s evaluated against tokenizer hash data) with
/// byte-level patterns that match directly on the response bytes. No
/// tokeniser on the hot path; no per-tag identity-claim conjunction
/// evaluation; no DOM depth tracker. The patterns anchor on the local marker
/// bytes the inducer chose, so surrounding-DOM changes (header redesign,
/// sidebar swap, ad-div injection) don't touch the patterns.
///
/// Scanner FSM:
/// <list type="bullet">
///   <item>AwaitPrefix → match <see cref="PrefixPattern"/> → AwaitContentStart</item>
///   <item>AwaitContentStart → match <see cref="ContentStartPattern"/> →
///   Capturing (capture-start byte snapshot taken)</item>
///   <item>Capturing → match <see cref="ContentEndPattern"/> with the
///   nested-open counter at 1 → Captured</item>
///   <item>Bailout on <see cref="BailoutBytes"/> consumed in a non-Capturing
///   state without a transition, or on <see cref="MaxCaptureBytes"/> exceeded
///   in Capturing.</item>
/// </list>
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
    /// Pattern the scanner waits on while in AwaitPrefix. First open-tag
    /// match transitions to AwaitContentStart. Typically the outer page
    /// chrome opener — header / nav / banner / top-level main.
    /// </summary>
    public required BytePattern PrefixPattern { get; init; }

    /// <summary>
    /// Pattern the scanner waits on while in AwaitContentStart. First match
    /// transitions to Capturing and snapshots the capture-start byte. The
    /// captured byte span starts AT the matched tag so the consumer sees
    /// the content element's opening.
    /// </summary>
    public required BytePattern ContentStartPattern { get; init; }

    /// <summary>
    /// Pattern the scanner waits on while in Capturing. Usually the close
    /// form of the content-start tag. The scanner counts nested opens of
    /// the same tag name so an inline <c>&lt;article&gt;...&lt;/article&gt;</c>
    /// inside the captured region doesn't terminate the capture early —
    /// only the close that returns the counter to zero counts.
    /// </summary>
    public required BytePattern ContentEndPattern { get; init; }

    /// <summary>
    /// Maximum bytes the scanner may consume in a non-Capturing state without
    /// a state transition before latching Bailout. Bounds the scanner's
    /// worst case on pages that don't carry the expected patterns.
    /// </summary>
    public required int BailoutBytes { get; init; }

    /// <summary>
    /// Maximum bytes the captured content region may span before the scanner
    /// bails. Bounds the worst case on pages whose ContentEnd pattern never
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
