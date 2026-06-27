using System.IO.Hashing;

namespace StyloExtract.Streaming;

/// <summary>
/// Mutable per-scan state shared by <see cref="FenceScanner"/> (ref-struct,
/// span-backed) and <see cref="IncrementalFenceScanner"/> (heap-backed).
/// alpha.21 extracted the per-tick algorithm into <see cref="StreamingTick.Step"/>
/// so both scanners execute literally the same code; this struct carries
/// the bits each scanner used to hold privately.
/// </summary>
internal struct StreamingTickState
{
    public FenceState State;
    public long BytesConsumed;
    public long CaptureStartByte;
    public long CaptureEndByte;
    /// <summary>
    /// alpha.23: bytes (not events) consumed since the last FSM transition.
    /// Pre-alpha.21 the equivalent <c>EventsSinceStateChange</c> counter
    /// incremented on every accepted tag; alpha.21's structural-tag filter
    /// throttled increments to structural tags only, which on real pages
    /// is a small fraction of all tags — the bailout never fired. Bytes
    /// are robust against that filter-ratio drift and let us reuse the
    /// per-template <see cref="StreamingTemplate.BailoutBytes"/> budget.
    /// </summary>
    public long BytesSinceStateChange;
    public ulong PrevTagHash;
    public int Depth;
    public int DepthAtCaptureStart;
    public int SketchCount;
    public int SketchWriteIdx;

    public static StreamingTickState Initial => new()
    {
        State = FenceState.AwaitPrefix,
    };
}

/// <summary>
/// Shared per-tick scoring used by both scanners. Pure static — all state
/// lives in the caller-owned <see cref="StreamingTickState"/> and the
/// caller-owned signature/window spans.
/// </summary>
internal static class StreamingTick
{
    private static readonly ulong[] s_seeds = BuildSeeds();

    private static ulong[] BuildSeeds()
    {
        var seeds = new ulong[RollingSketch.SignatureSize];
        for (int i = 0; i < RollingSketch.SignatureSize; i++)
            seeds[i] = 0x9E3779B97F4A7C15UL * (ulong)(i + 1);
        return seeds;
    }

    public static ScanVerdict Step(
        in TagEvent evt,
        ref StreamingTickState s,
        Span<uint> signature,
        Span<EventSlot> window,
        in StreamingTemplate template)
    {
        if (s.State == FenceState.Captured) return ScanVerdict.Captured;
        if (s.State == FenceState.Bailed) return ScanVerdict.Bailout;

        s.BytesConsumed += evt.ByteLength;
        s.BytesSinceStateChange += evt.ByteLength;

        // alpha.21 structural-tag filter — non-structural tags bypass the sketch entirely.
        if (StructuralTagAllowlist.Contains(evt.TagNameHash))
        {
            // alpha.23 depth tracking is STRUCTURAL-ONLY: void elements
            // (img/br/input/meta/link/hr) and inline content (a/span/i/b/em…)
            // have no corresponding close in the wild — tokenising every
            // emitted tag as +1 depth inflates depth unboundedly on real
            // pages (mostlylucid.net hit Depth=206 at </body> versus an
            // honest ~0). Restrict depth to structural tags only, matching
            // the sketch filter, so DepthAtCaptureStart and the depth-aware
            // ContentEnd check are comparable.
            if (evt.IsClose)
            {
                if (s.Depth > 0) s.Depth--;
            }
            else
            {
                s.Depth++;
            }

            PushSketch(window, ref s, evt.TagNameHash, evt.ClassHash);
            s.PrevTagHash = evt.TagNameHash;
            RecomputeSketch(signature, window, in s);

            var prevState = s.State;
            switch (s.State)
            {
                case FenceState.AwaitPrefix:
                    {
                        var f = template.PrefixFence;
                        if (SketchMatches(signature, in f))
                            s.State = FenceState.AwaitContentStart;
                        break;
                    }
                case FenceState.AwaitContentStart:
                    {
                        var f = template.ContentStartFence;
                        if (SketchMatches(signature, in f))
                        {
                            s.State = FenceState.Capturing;
                            s.CaptureStartByte = s.BytesConsumed;
                            s.DepthAtCaptureStart = s.Depth;
                        }
                        break;
                    }
                case FenceState.Capturing:
                    {
                        var f = template.ContentEndFence;
                        // alpha.21 depth-aware: ContentEnd only matches when
                        // DOM depth has returned to (or below) the depth at
                        // ContentStart. Nested matches mid-content are ignored.
                        if (s.Depth <= s.DepthAtCaptureStart && SketchMatches(signature, in f))
                        {
                            s.State = FenceState.Captured;
                            s.CaptureEndByte = s.BytesConsumed;
                            return ScanVerdict.Captured;
                        }
                        break;
                    }
            }
            if (s.State != prevState) s.BytesSinceStateChange = 0;
        }

        // alpha.23: pre-state-transition bailouts use BYTES-since-state-change
        // (robust against the structural-tag filter's event throttling) and
        // cover BOTH AwaitPrefix and AwaitContentStart. The Capturing state
        // has its own MaxCaptureBytes ceiling below.
        if ((s.State == FenceState.AwaitPrefix || s.State == FenceState.AwaitContentStart)
            && s.BytesSinceStateChange > template.BailoutBytes)
        {
            s.State = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        if (s.State == FenceState.Capturing && (s.BytesConsumed - s.CaptureStartByte) > template.MaxCaptureBytes)
        {
            s.State = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        return ScanVerdict.Continue;
    }

    private static void PushSketch(Span<EventSlot> window, ref StreamingTickState s, ulong tagHash, ulong classHash)
    {
        window[s.SketchWriteIdx] = new EventSlot(tagHash, classHash, s.PrevTagHash);
        s.SketchWriteIdx = (s.SketchWriteIdx + 1) % window.Length;
        s.SketchCount++;
    }

    private static void RecomputeSketch(Span<uint> signature, ReadOnlySpan<EventSlot> window, in StreamingTickState s)
    {
        signature.Fill(uint.MaxValue);
        var populated = Math.Min(s.SketchCount, window.Length);
        if (populated == 0) return;

        Span<byte> buf = stackalloc byte[16];
        var seeds = s_seeds.AsSpan();
        var len = window.Length;
        var start = s.SketchCount <= len ? 0 : s.SketchWriteIdx;
        // alpha.21: leftmost shingle uses prevTag=0 to match fence-builder semantics.
        for (int i = 0; i < populated; i++)
        {
            var slot = window[(start + i) % len];
            var prevTag = i == 0 ? 0UL : slot.PrevTagHash;
            var shingle = RollingSketch.ShingleHash(prevTag, slot.TagHash, slot.ClassHash);
            BitConverter.TryWriteBytes(buf, shingle);
            for (int sIdx = 0; sIdx < RollingSketch.SignatureSize; sIdx++)
            {
                BitConverter.TryWriteBytes(buf[8..], seeds[sIdx]);
                var h = (uint)(XxHash64.HashToUInt64(buf) & 0xFFFFFFFFUL);
                if (h < signature[sIdx]) signature[sIdx] = h;
            }
        }
    }

    private static bool SketchMatches(ReadOnlySpan<uint> signature, in TemplateFence fence)
    {
        Span<ulong> bands = stackalloc ulong[16];
        ComputeBands(signature, bands);
        var fenceBands = fence.LshBands;
        var n = Math.Min(bands.Length, fenceBands.Length);
        for (int i = 0; i < n; i++)
            if (bands[i] == fenceBands[i]) return true;
        return false;
    }

    private static void ComputeBands(ReadOnlySpan<uint> signature, Span<ulong> bands)
    {
        const int rowsPerBand = 8;
        Span<byte> buf = stackalloc byte[rowsPerBand * 4];
        for (int b = 0; b < bands.Length; b++)
        {
            for (int r = 0; r < rowsPerBand; r++)
                BitConverter.TryWriteBytes(buf.Slice(r * 4, 4), signature[b * rowsPerBand + r]);
            bands[b] = XxHash64.HashToUInt64(buf);
        }
    }
}
