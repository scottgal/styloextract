using System.IO.Hashing;
using System.Text;

namespace StyloExtract.Streaming;

/// <summary>
/// Naive first-pass streaming-template inducer. Walks the HTML once via the
/// minimal tokenizer, identifies semantic-marker tag-sequence-pairs that
/// likely correspond to chrome → content → chrome transitions, and produces
/// a <see cref="StreamingTemplate"/> keyed to the given host.
///
/// Heuristic:
/// <list type="bullet">
///   <item><description>PrefixFence: first <c>&lt;header&gt;</c> open → first <c>&lt;/header&gt;</c> close, OR first <c>&lt;nav&gt;</c> open → first <c>&lt;/nav&gt;</c> close. If neither marker is present, fall back to the first <c>&lt;body&gt;</c> + a couple of following events.</description></item>
///   <item><description>ContentStartFence: the first non-trivial paragraph cluster after the prefix — typically <c>&lt;p&gt;…&lt;/p&gt;&lt;p&gt;…&lt;/p&gt;</c>.</description></item>
///   <item><description>ContentEndFence: first <c>&lt;footer&gt;</c> open, OR <c>&lt;/main&gt;</c> / <c>&lt;/article&gt;</c> close, OR <c>&lt;/body&gt;</c> as a final fallback.</description></item>
/// </list>
///
/// Returns null when no plausible fences can be identified (plain text,
/// SVG-only, image-only pages). The caller should treat null as "leave
/// <see cref="ScanVerdict.NoTemplate"/> alone, try again next visit".
/// </summary>
public sealed class StreamingTemplateInducer
{
    // Pre-hashed tag-name markers (lowercase, no angle brackets / class attrs).
    private static readonly ulong s_headerHash = XxHash3.HashToUInt64("header"u8);
    private static readonly ulong s_navHash = XxHash3.HashToUInt64("nav"u8);
    private static readonly ulong s_pHash = XxHash3.HashToUInt64("p"u8);
    private static readonly ulong s_footerHash = XxHash3.HashToUInt64("footer"u8);
    private static readonly ulong s_mainHash = XxHash3.HashToUInt64("main"u8);
    private static readonly ulong s_articleHash = XxHash3.HashToUInt64("article"u8);
    private static readonly ulong s_bodyHash = XxHash3.HashToUInt64("body"u8);

    /// <summary>Cap how far we'll scan looking for semantic markers.</summary>
    private const int MaxBytesScanned = 1_000_000;

    /// <summary>
    /// Build a <see cref="StreamingTemplate"/> from observed tag events for
    /// <paramref name="host"/>. Returns null when no plausible fences can be
    /// found within the first ~1MB of input.
    /// </summary>
    /// <param name="host">Host the template is being induced for (lookup key).</param>
    /// <param name="html">Page bytes (full or large enough prefix).</param>
    public StreamingTemplate? Induce(string host, ReadOnlySpan<byte> html)
    {
        if (string.IsNullOrEmpty(host)) return null;
        if (html.Length == 0) return null;

        var events = new List<RecordedEvent>(capacity: 512);
        var tokenizer = new MinimalHtmlTokenizer(html);
        var totalBytes = 0;
        while (tokenizer.TryReadTag(out var evt) && totalBytes < MaxBytesScanned)
        {
            totalBytes += evt.ByteLength;
            events.Add(new RecordedEvent(evt.TagNameHash, evt.ClassHash, evt.IsClose));
        }

        if (events.Count < 4) return null;

        // === PrefixFence: header open→close OR nav open→close ===
        var prefixWindow = FindPrefix(events);
        if (prefixWindow.Count == 0) return null;

        // === ContentStartFence: paragraph cluster <p>...</p><p>...</p> ===
        var prefixEndIdx = prefixWindow.EndIndex;
        var contentStartWindow = FindParagraphCluster(events, prefixEndIdx);
        if (contentStartWindow.Count == 0) return null;

        // === ContentEndFence: footer open / main/article close / body close ===
        var contentStartEndIdx = contentStartWindow.EndIndex;
        var contentEndWindow = FindContentEnd(events, contentStartEndIdx);
        if (contentEndWindow.Count == 0) return null;

        const int windowSize = 8;
        var prefixEvents = ToFenceEvents(events, prefixWindow, windowSize);
        var contentStartEvents = ToFenceEvents(events, contentStartWindow, windowSize);
        var contentEndEvents = ToFenceEvents(events, contentEndWindow, windowSize);

        var template = new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = host,
            PrefixFence = TemplateFence.BuildFromEvents(prefixEvents, requiredDepth: 0),
            ContentStartFence = TemplateFence.BuildFromEvents(contentStartEvents, requiredDepth: 0),
            ContentEndFence = TemplateFence.BuildFromEvents(contentEndEvents, requiredDepth: 0),
            MinContentDepth = 0,
            BailoutBytes = 5_000_000,
            MaxCaptureBytes = 5_000_000,
            WindowSize = windowSize,
            MaxEventsWithoutTransition = 256,
        };
        return template;
    }

    /// <summary>
    /// Snapshot of an inducer-chosen fence layout, useful for logging the
    /// induced template (the fences themselves are MinHash sketches and
    /// not human-readable).
    /// </summary>
    public readonly record struct InducedSummary(
        string PrefixMarker,
        string ContentStartMarker,
        string ContentEndMarker);

    /// <summary>
    /// Describe the human-readable shape of what <see cref="Induce"/> would
    /// pick. Walks the same heuristics and returns named markers (or null if
    /// induction would fail). Cheap — same single tokenizer pass.
    /// </summary>
    public InducedSummary? Describe(ReadOnlySpan<byte> html)
    {
        if (html.Length == 0) return null;

        var events = new List<RecordedEvent>(capacity: 512);
        var tokenizer = new MinimalHtmlTokenizer(html);
        var totalBytes = 0;
        while (tokenizer.TryReadTag(out var evt) && totalBytes < MaxBytesScanned)
        {
            totalBytes += evt.ByteLength;
            events.Add(new RecordedEvent(evt.TagNameHash, evt.ClassHash, evt.IsClose));
        }
        if (events.Count < 4) return null;

        var prefix = FindPrefix(events);
        if (prefix.Count == 0) return null;
        var contentStart = FindParagraphCluster(events, prefix.EndIndex);
        if (contentStart.Count == 0) return null;
        var contentEnd = FindContentEnd(events, contentStart.EndIndex);
        if (contentEnd.Count == 0) return null;

        return new InducedSummary(prefix.MarkerLabel, contentStart.MarkerLabel, contentEnd.MarkerLabel);
    }

    private static FenceWindow FindPrefix(List<RecordedEvent> events)
    {
        // <header> ... </header>
        var openIdx = IndexOf(events, s_headerHash, isClose: false, startInclusive: 0);
        if (openIdx >= 0)
        {
            var closeIdx = IndexOf(events, s_headerHash, isClose: true, startInclusive: openIdx + 1);
            if (closeIdx > openIdx)
                return new FenceWindow(openIdx, closeIdx, "header-open→close");
        }
        // <nav> ... </nav>
        openIdx = IndexOf(events, s_navHash, isClose: false, startInclusive: 0);
        if (openIdx >= 0)
        {
            var closeIdx = IndexOf(events, s_navHash, isClose: true, startInclusive: openIdx + 1);
            if (closeIdx > openIdx)
                return new FenceWindow(openIdx, closeIdx, "nav-open→close");
        }
        // <body> + next 3 events (last-ditch shape signal)
        openIdx = IndexOf(events, s_bodyHash, isClose: false, startInclusive: 0);
        if (openIdx >= 0 && openIdx + 3 < events.Count)
            return new FenceWindow(openIdx, openIdx + 3, "body-prefix");

        return default;
    }

    private static FenceWindow FindParagraphCluster(List<RecordedEvent> events, int afterIdx)
    {
        // Look for first <p> ... </p> ... <p> ... </p> sequence past afterIdx.
        var firstOpen = IndexOf(events, s_pHash, isClose: false, startInclusive: afterIdx + 1);
        if (firstOpen < 0) return default;
        var firstClose = IndexOf(events, s_pHash, isClose: true, startInclusive: firstOpen + 1);
        if (firstClose < 0) return default;
        var secondOpen = IndexOf(events, s_pHash, isClose: false, startInclusive: firstClose + 1);
        if (secondOpen < 0) return default;
        var secondClose = IndexOf(events, s_pHash, isClose: true, startInclusive: secondOpen + 1);
        if (secondClose < 0) return default;
        return new FenceWindow(firstOpen, secondClose, "p-p-cluster");
    }

    private static FenceWindow FindContentEnd(List<RecordedEvent> events, int afterIdx)
    {
        // First <footer> open after content start.
        var idx = IndexOf(events, s_footerHash, isClose: false, startInclusive: afterIdx + 1);
        if (idx >= 0 && idx + 3 < events.Count)
            return new FenceWindow(idx, idx + 3, "footer-open");
        if (idx >= 0)
            return new FenceWindow(Math.Max(0, idx - 3), idx, "footer-open-prefix");

        // Last </main> or </article> close, scanning forwards.
        var mainCloseIdx = IndexOf(events, s_mainHash, isClose: true, startInclusive: afterIdx + 1);
        if (mainCloseIdx >= 0 && mainCloseIdx >= 3)
            return new FenceWindow(mainCloseIdx - 3, mainCloseIdx, "main-close");

        var articleCloseIdx = IndexOf(events, s_articleHash, isClose: true, startInclusive: afterIdx + 1);
        if (articleCloseIdx >= 0 && articleCloseIdx >= 3)
            return new FenceWindow(articleCloseIdx - 3, articleCloseIdx, "article-close");

        // </body> as fallback.
        var bodyCloseIdx = IndexOf(events, s_bodyHash, isClose: true, startInclusive: afterIdx + 1);
        if (bodyCloseIdx >= 0 && bodyCloseIdx >= 3)
            return new FenceWindow(bodyCloseIdx - 3, bodyCloseIdx, "body-close");

        return default;
    }

    private static int IndexOf(List<RecordedEvent> events, ulong tagHash, bool isClose, int startInclusive)
    {
        for (int i = startInclusive; i < events.Count; i++)
        {
            var e = events[i];
            if (e.IsClose == isClose && e.TagNameHash == tagHash)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Slice events[<paramref name="window"/>] into a fence-event array sized
    /// to <paramref name="windowSize"/>. Pads forwards from the start index
    /// when the natural window is shorter; truncates to the trailing
    /// <c>windowSize</c> events when longer.
    /// </summary>
    private static (ulong tagHash, ulong classHash)[] ToFenceEvents(
        List<RecordedEvent> events,
        FenceWindow window,
        int windowSize)
    {
        var natural = window.EndIndex - window.StartIndex + 1;
        var sliceStart = window.StartIndex;
        var sliceLen = natural;
        if (natural > windowSize)
        {
            // Take the trailing windowSize events so the fence lands at the
            // end-of-window event (closer to the marker that triggered it).
            sliceStart = window.EndIndex - windowSize + 1;
            sliceLen = windowSize;
        }

        var len = Math.Min(sliceLen, windowSize);
        var result = new (ulong, ulong)[len];
        for (int i = 0; i < len; i++)
        {
            var ev = events[sliceStart + i];
            result[i] = (ev.TagNameHash, ev.ClassHash);
        }
        return result;
    }

    private readonly record struct RecordedEvent(ulong TagNameHash, ulong ClassHash, bool IsClose);

    /// <summary>
    /// Window of events identified as a fence-shape source. The default
    /// <c>(0, 0, null)</c> represents "not found" and has <see cref="Count"/>
    /// == 0 so callers can distinguish a real-but-degenerate window from a
    /// missing one.
    /// </summary>
    private readonly record struct FenceWindow(int StartIndex, int EndIndex, string MarkerLabel)
    {
        public int Count => MarkerLabel is null ? 0 : (EndIndex - StartIndex + 1);
    }
}
