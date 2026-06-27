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
    public int EventsSinceStateChange;
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

        // Depth tracking — needed for depth-aware capture-end check.
        if (evt.IsClose)
        {
            if (s.Depth > 0) s.Depth--;
        }
        else
        {
            s.Depth++;
        }

        // alpha.21 structural-tag filter — non-structural tags bypass the sketch entirely.
        if (StructuralTagAllowlist.Contains(evt.TagNameHash))
        {
            PushSketch(window, ref s, evt.TagNameHash, evt.ClassHash);
            s.PrevTagHash = evt.TagNameHash;
            RecomputeSketch(signature, window, in s);
            s.EventsSinceStateChange++;

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
            if (s.State != prevState) s.EventsSinceStateChange = 0;
        }

        if (s.State == FenceState.AwaitPrefix && s.EventsSinceStateChange >= template.MaxEventsWithoutTransition)
        {
            s.State = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        if (s.State == FenceState.Capturing && (s.BytesConsumed - s.CaptureStartByte) > template.MaxCaptureBytes)
        {
            s.State = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        if (s.State == FenceState.AwaitPrefix && s.BytesConsumed > template.BailoutBytes)
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
