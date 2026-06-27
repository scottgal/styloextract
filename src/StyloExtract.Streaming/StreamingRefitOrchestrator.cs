using System.Collections.Concurrent;

namespace StyloExtract.Streaming;

/// <summary>
/// V1 streaming-template refit / versioning. Sibling to the layout-extractor
/// <c>RefitOrchestrator</c> in <c>StyloExtract.Templates</c>, but reshaped for
/// what a streaming template actually carries.
///
/// Per host the orchestrator tracks two signals after each
/// <see cref="ScanVerdict.Captured"/>:
///
/// <list type="bullet">
///   <item><description><b>Capture-range EWMA drift.</b> A sliding-window mean
///   of (capture_end - capture_start) bytes; when a new captured length lands
///   more than <see cref="RelativeDriftThreshold"/> away from the mean, the
///   drift counter increments. This catches "site changed shape" — the same
///   fences still match but the captured content region has shifted.</description></item>
///   <item><description><b>Refit cadence.</b> Every <see cref="ScansPerForcedRefit"/>
///   captured scans we re-induce regardless of drift, persisting the new
///   template only if its fence bytes differ from the current one's. This
///   catches slow structural evolution that doesn't tip the EWMA but does
///   move the inducer's heuristic markers.</description></item>
/// </list>
///
/// Both checks queue work onto a fire-and-forget channel-style background task
/// (<see cref="Task.Run"/>) so the synchronous hot-path
/// (<see cref="StreamingPathSelector.ScanByHost"/>) is never blocked.
///
/// On a successful refit: version is incremented, the store is upserted,
/// the version sink fires a <see cref="StreamingTemplateRefitEvent"/>. The
/// reason field carries "drift" (EWMA exceeded) or "cadence" (forced refit
/// found different fences).
/// </summary>
public sealed class StreamingRefitOrchestrator
{
    /// <summary>
    /// Default relative-distance threshold for the capture-range EWMA. When the
    /// new captured length differs from the EWMA by more than 30%, the
    /// observation counts toward the drift bailout.
    /// </summary>
    public const double DefaultRelativeDriftThreshold = 0.30;

    /// <summary>Consecutive drifty observations needed before a forced refit fires.</summary>
    public const int DefaultDriftBailoutCount = 3;

    /// <summary>Every Nth captured scan triggers a cadence refit regardless of drift.</summary>
    public const int DefaultScansPerForcedRefit = 10;

    /// <summary>EWMA smoothing factor for the capture-range mean.</summary>
    private const double EwmaAlpha = 0.2;

    private readonly IStreamingTemplateStore _store;
    private readonly StreamingTemplateInducer _inducer;
    private readonly IStreamingTemplateVersionSink _sink;
    private readonly ConcurrentDictionary<string, HostState> _byHost = new(StringComparer.OrdinalIgnoreCase);

    public double RelativeDriftThreshold { get; }
    public int DriftBailoutCount { get; }
    public int ScansPerForcedRefit { get; }

    public StreamingRefitOrchestrator(
        IStreamingTemplateStore store,
        StreamingTemplateInducer inducer,
        IStreamingTemplateVersionSink? sink = null,
        double relativeDriftThreshold = DefaultRelativeDriftThreshold,
        int driftBailoutCount = DefaultDriftBailoutCount,
        int scansPerForcedRefit = DefaultScansPerForcedRefit)
    {
        _store = store;
        _inducer = inducer;
        _sink = sink ?? new NoopStreamingTemplateVersionSink();
        RelativeDriftThreshold = relativeDriftThreshold;
        DriftBailoutCount = driftBailoutCount;
        ScansPerForcedRefit = scansPerForcedRefit;
    }

    /// <summary>
    /// Record a captured-scan observation for <paramref name="host"/> and kick
    /// the off-hot-path refit pipeline if drift or cadence threshold is hit.
    /// Returns immediately; refit work runs on a background task. Callers from
    /// the hot path (see <see cref="StreamingPathSelector.ScanByHost"/>) should
    /// pass <paramref name="latestBytes"/> as a copy / pinned snapshot since
    /// the background task may dereference it long after the synchronous scan
    /// returns. The byte array is held by reference — do not mutate it.
    /// </summary>
    public void RecordCaptured(
        string host,
        long captureStartByte,
        long captureEndByte,
        byte[] latestBytes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(host)) return;
        if (latestBytes.Length == 0) return;

        var captureLen = Math.Max(0, captureEndByte - captureStartByte);
        var state = _byHost.GetOrAdd(host, _ => new HostState());

        bool needsRefit;
        string reason;
        lock (state)
        {
            state.CapturedCount++;
            if (state.EwmaCaptureLen <= 0)
            {
                state.EwmaCaptureLen = captureLen;
            }
            else
            {
                var prev = state.EwmaCaptureLen;
                var newEwma = EwmaAlpha * captureLen + (1 - EwmaAlpha) * prev;
                state.EwmaCaptureLen = newEwma;

                if (prev > 0)
                {
                    var relDelta = Math.Abs(captureLen - prev) / prev;
                    if (relDelta > RelativeDriftThreshold)
                        state.ConsecutiveDriftHits++;
                    else
                        state.ConsecutiveDriftHits = 0;
                }
            }

            var driftBail = state.ConsecutiveDriftHits >= DriftBailoutCount;
            var cadenceBail = state.CapturedCount % ScansPerForcedRefit == 0;
            needsRefit = driftBail || cadenceBail;
            reason = driftBail ? "drift" : "cadence";

            if (driftBail) state.ConsecutiveDriftHits = 0;
        }

        if (!needsRefit) return;

        // Fire-and-forget — never block the hot path. Exceptions are swallowed
        // (logged in v1 only via Console.WriteLine — wire to ILogger if needed).
        _ = Task.Run(() => RefitAsync(host, latestBytes, reason, cancellationToken));
    }

    /// <summary>
    /// Test/diagnostic entry-point: drive a refit synchronously without going
    /// through the cadence/drift gating. Bumps version + persists + fires
    /// the sink if (and only if) the freshly induced template differs from
    /// the current one. Returns the new template on refit, null on no-op.
    /// </summary>
    public async Task<StreamingTemplate?> ForceRefitAsync(
        string host,
        byte[] latestBytes,
        string reason,
        CancellationToken cancellationToken = default)
        => await RefitAsync(host, latestBytes, reason, cancellationToken).ConfigureAwait(false);

    private async Task<StreamingTemplate?> RefitAsync(
        string host,
        byte[] latestBytes,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _store.GetByHostAsync(host, cancellationToken).ConfigureAwait(false);
            if (current is null) return null;

            var fresh = _inducer.Induce(host, latestBytes);
            if (fresh is null) return null;

            if (FencesEqual(current, fresh))
                return null; // No structural change — skip the bump.

            var refitted = fresh with
            {
                TemplateId = Guid.NewGuid(),
                Version = current.Version + 1,
            };
            await _store.UpsertAsync(refitted, cancellationToken).ConfigureAwait(false);

            var evt = new StreamingTemplateRefitEvent(
                Host: host,
                OldTemplateId: current.TemplateId,
                NewTemplateId: refitted.TemplateId,
                OldVersion: current.Version,
                NewVersion: refitted.Version,
                Reason: reason,
                DetectedAt: DateTimeOffset.UtcNow);
            await _sink.OnRefittedAsync(evt, cancellationToken).ConfigureAwait(false);

            return refitted;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[streaming-refit] {host} refit failed: {ex.Message}");
            return null;
        }
    }

    private static bool FencesEqual(StreamingTemplate a, StreamingTemplate b)
    {
        return FenceEqual(a.PrefixFence, b.PrefixFence)
            && FenceEqual(a.ContentStartFence, b.ContentStartFence)
            && FenceEqual(a.ContentEndFence, b.ContentEndFence);

        static bool FenceEqual(TemplateFence x, TemplateFence y)
        {
            if (x.LshBands.Length != y.LshBands.Length) return false;
            for (int i = 0; i < x.LshBands.Length; i++)
                if (x.LshBands[i] != y.LshBands[i]) return false;
            return true;
        }
    }

    private sealed class HostState
    {
        public long CapturedCount;
        public double EwmaCaptureLen;
        public int ConsecutiveDriftHits;
    }
}
