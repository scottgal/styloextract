using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

/// <summary>
/// Mutable per-scan state shared by <see cref="FenceScanner"/> (ref-struct)
/// and <see cref="IncrementalFenceScanner"/> (heap-backed). Both scanners
/// route through <see cref="StreamingTick.Step"/> so the FSM lives in one
/// place.
///
/// Task 4 rewrite (alpha.24): the alpha.21..23 sketch / window state is
/// gone. The new state machine matches tripwires against per-event
/// <see cref="ElementHashSet"/>s — no sliding window, no MinHash recompute
/// per tick.
/// </summary>
internal struct StreamingTickState
{
    public FenceState State;
    public long BytesConsumed;
    public long CaptureStartByte;
    public long CaptureEndByte;
    /// <summary>
    /// Bytes consumed since the last FSM transition. The non-Capturing
    /// states bail when this exceeds <see cref="StreamingTemplate.BailoutBytes"/>.
    /// </summary>
    public long BytesSinceStateChange;
    public int Depth;
    public int DepthAtCaptureStart;

    public static StreamingTickState Initial => new()
    {
        State = FenceState.AwaitPrefix,
    };
}

/// <summary>
/// Shared per-tick scoring used by both scanners. Pure static — all state
/// lives in the caller-owned <see cref="StreamingTickState"/>.
///
/// Tripwire matching is exact (XxHash3 equality on tag + id + class
/// hashes), not probabilistic. The scanner fires a transition the moment
/// a tag event satisfies the current state's tripwire as an
/// <see cref="IdentityClaim"/>.
/// </summary>
internal static class StreamingTick
{
    public static ScanVerdict Step(
        in TagEvent evt,
        ref StreamingTickState s,
        in StreamingTemplate template)
    {
        if (s.State == FenceState.Captured) return ScanVerdict.Captured;
        if (s.State == FenceState.Bailed) return ScanVerdict.Bailout;

        s.BytesConsumed += evt.ByteLength;
        s.BytesSinceStateChange += evt.ByteLength;

        // Track DOM depth across structural opens/closes only — void elements
        // and inline tags have no honest close in real-world HTML and would
        // inflate the counter unboundedly (the alpha.23 fix carried forward).
        var isStructural = StructuralTagAllowlist.Contains(evt.TagNameHash);
        if (isStructural)
        {
            if (evt.IsClose)
            {
                if (s.Depth > 0) s.Depth--;
            }
            else
            {
                s.Depth++;
            }
        }

        var prevState = s.State;
        switch (s.State)
        {
            case FenceState.AwaitPrefix:
                if (!evt.IsClose)
                {
                    var element = evt.ToElementHashSet();
                    if (IdentityClaimMatcher.MatchesByHash(in element, template.PrefixTripwire))
                        s.State = FenceState.AwaitContentStart;
                }
                break;

            case FenceState.AwaitContentStart:
                if (!evt.IsClose)
                {
                    var element = evt.ToElementHashSet();
                    if (IdentityClaimMatcher.MatchesByHash(in element, template.ContentStartTripwire))
                    {
                        s.State = FenceState.Capturing;
                        s.CaptureStartByte = s.BytesConsumed;
                        s.DepthAtCaptureStart = s.Depth;
                    }
                }
                break;

            case FenceState.Capturing:
                // ContentEnd fires on CLOSE events at or below capture-start depth.
                // Open tags can never close the capture (matches the layout-side
                // semantics where the captured subtree ends at its closing tag).
                if (evt.IsClose && s.Depth <= s.DepthAtCaptureStart)
                {
                    var element = evt.ToElementHashSet();
                    if (IdentityClaimMatcher.MatchesByHash(in element, template.ContentEndTripwire))
                    {
                        s.State = FenceState.Captured;
                        s.CaptureEndByte = s.BytesConsumed;
                        return ScanVerdict.Captured;
                    }
                }
                break;
        }
        if (s.State != prevState) s.BytesSinceStateChange = 0;

        // Pre-capture bailout: too many bytes consumed without a transition.
        if ((s.State == FenceState.AwaitPrefix || s.State == FenceState.AwaitContentStart)
            && s.BytesSinceStateChange > template.BailoutBytes)
        {
            s.State = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        // In-capture bailout: captured region grew past the per-template ceiling.
        if (s.State == FenceState.Capturing
            && (s.BytesConsumed - s.CaptureStartByte) > template.MaxCaptureBytes)
        {
            s.State = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        return ScanVerdict.Continue;
    }
}
