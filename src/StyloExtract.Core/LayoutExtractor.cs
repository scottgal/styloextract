using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Templates;

namespace StyloExtract.Core;

public sealed class LayoutExtractor : ILayoutExtractor
{
    private readonly IHtmlDomParser _parser;
    private readonly IDomCleaner _cleaner;
    private readonly IStructuralFingerprinter _fingerprinter;
    private readonly IBlockSegmenter _segmenter;
    private readonly IBlockClassifier _classifier;
    private readonly IMarkdownRenderer _renderer;
    private readonly ITemplateIndex _index;
    private readonly HostHasher _hostHasher;
    private readonly IExtractorInducer _inducer;
    private readonly IExtractorApplicator _applicator;
    private readonly double _fastPathThreshold;
    private readonly double _slowPathThreshold;
    private readonly RefitOrchestrator _refit;
    private readonly ITemplateVersionEventSink _eventSink;
    private readonly TypedSignalSink<StyloExtractSignal>? _signals;
    private readonly ILogger<LayoutExtractor>? _logger;

    public LayoutExtractor(
        IHtmlDomParser parser,
        IDomCleaner cleaner,
        IStructuralFingerprinter fingerprinter,
        IBlockSegmenter segmenter,
        IBlockClassifier classifier,
        IMarkdownRenderer renderer,
        ITemplateIndex index,
        HostHasher hostHasher,
        IExtractorInducer inducer,
        IExtractorApplicator applicator,
        double fastPathThreshold,
        double slowPathThreshold,
        RefitOrchestrator refit,
        ITemplateVersionEventSink eventSink,
        TypedSignalSink<StyloExtractSignal>? signals = null,
        ILogger<LayoutExtractor>? logger = null)
    {
        _parser = parser; _cleaner = cleaner; _fingerprinter = fingerprinter;
        _segmenter = segmenter; _classifier = classifier; _renderer = renderer;
        _index = index; _hostHasher = hostHasher; _inducer = inducer; _applicator = applicator;
        _fastPathThreshold = fastPathThreshold; _slowPathThreshold = slowPathThreshold;
        _refit = refit;
        _eventSink = eventSink;
        _signals = signals;
        _logger = logger;
    }

    public async Task<ExtractionResult> ExtractAsync(
        string html,
        Uri? sourceUri = null,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExtractionOptions();
        var total = Stopwatch.StartNew();

        var parseTimer = Stopwatch.StartNew();
        var doc = _parser.Parse(html, sourceUri);
        _cleaner.Clean(doc);
        parseTimer.Stop();
        _signals?.Raise(StyloExtractSignals.ParseDone, default);

        var fpTimer = Stopwatch.StartNew();
        var fp = _fingerprinter.Compute(doc);
        fpTimer.Stop();
        _signals?.Raise(StyloExtractSignals.FingerprintComputed, new StyloExtractSignal(FingerprintHex: fp.Hex));

        var hostHash = _hostHasher.Hash(options.HostOverride ?? sourceUri?.Host ?? "");
        var status = MatchStatus.NovelEphemeral;
        Guid? templateId = null;
        int templateVersion = 0;
        double similarity = 0;
        int observationCount = 0;
        IReadOnlyList<ExtractedBlock> blocks;

        var matchTimer = Stopwatch.StartNew();
        var fastHit = await _index.ProbeFastPathAsync(hostHash, fp, _fastPathThreshold, cancellationToken);
        if (fastHit is not null)
        {
            var ex = await _index.GetExtractorAsync(fastHit.Value, cancellationToken);
            if (ex is not null)
            {
                blocks = _applicator.Apply(doc, ex);
                templateId = fastHit;
                templateVersion = ex.Version;
                similarity = 1.0;
                // Read observation count before the RecordObservationAsync write that follows;
                // the post-match block increments it by 1 so the returned count is current.
                observationCount = await _index.GetObservationCountAsync(fastHit.Value, cancellationToken);
                status = MatchStatus.FastPathHit;
                _signals?.Raise(StyloExtractSignals.MatchFastPathHit,
                    new StyloExtractSignal(TemplateId: fastHit, TemplateVersion: templateVersion, Similarity: similarity),
                    key: fastHit.Value.ToString("N"));
            }
            else
            {
                // Fast-path hit returned a template ID but the extractor blob is missing (corrupt DB).
                // Self-heal: re-classify, induce a fresh extractor, and register it (M15).
                _logger?.LogWarning("Fast-path hit templateId {TemplateId} has no extractor blob — self-healing by inducing fresh extractor.", fastHit.Value);
                blocks = _classifier.Classify(_segmenter.Segment(doc));
                if (options.LearnNewTemplates)
                {
                    var freshEx = _inducer.Induce(fastHit.Value, blocks);
                    templateId = await _index.RegisterAsync(hostHash, fp, freshEx, cancellationToken);
                    templateVersion = 1;
                    observationCount = 1;
                    status = MatchStatus.Novel;
                    _signals?.Raise(StyloExtractSignals.TemplateNovel,
                        new StyloExtractSignal(TemplateId: templateId, FingerprintHex: fp.Hex, HostDisplayName: sourceUri?.Host),
                        key: templateId.Value.ToString("N"));
                }
                else
                {
                    _signals?.Raise(StyloExtractSignals.MatchFastPathMiss, default);
                }
            }
        }
        else
        {
            var slow = await _index.ProbeSlowPathAsync(hostHash, fp, _slowPathThreshold, cancellationToken);
            if (slow is not null)
            {
                var ex = await _index.GetExtractorAsync(slow.Value.TemplateId, cancellationToken);
                if (ex is not null)
                {
                    blocks = _applicator.Apply(doc, ex);
                    templateId = slow.Value.TemplateId;
                    templateVersion = ex.Version;
                    similarity = slow.Value.Cosine;
                    observationCount = await _index.GetObservationCountAsync(templateId.Value, cancellationToken);
                    status = MatchStatus.SlowPathMatch;
                    _signals?.Raise(StyloExtractSignals.MatchSlowPathMatch,
                        new StyloExtractSignal(TemplateId: templateId, TemplateVersion: templateVersion, Similarity: similarity),
                        key: templateId.Value.ToString("N"));
                }
                else
                {
                    // Slow-path match hit a template ID but extractor is missing. Log and treat as novel.
                    _logger?.LogWarning("Slow-path match templateId {TemplateId} has no extractor blob — treating as novel.", slow.Value.TemplateId);
                    blocks = _classifier.Classify(_segmenter.Segment(doc));
                    _signals?.Raise(StyloExtractSignals.MatchSlowPathMiss, default);
                }
            }
            else
            {
                blocks = _classifier.Classify(_segmenter.Segment(doc));
                _signals?.Raise(StyloExtractSignals.MatchFastPathMiss, default);
                if (options.LearnNewTemplates)
                {
                    var newId = Guid.NewGuid();
                    var ex = _inducer.Induce(newId, blocks);
                    templateId = await _index.RegisterAsync(hostHash, fp, ex, cancellationToken);
                    templateVersion = 1;
                    observationCount = 1;
                    status = MatchStatus.Novel;
                    _signals?.Raise(StyloExtractSignals.TemplateNovel,
                        new StyloExtractSignal(TemplateId: templateId, FingerprintHex: fp.Hex, HostDisplayName: sourceUri?.Host),
                        key: templateId.Value.ToString("N"));
                }
            }
        }

        if (templateId is not null && status is MatchStatus.FastPathHit or MatchStatus.SlowPathMatch)
        {
            await _index.RecordObservationAsync(templateId.Value, fp, similarity, cancellationToken);
            // Increment the locally-captured count by 1 so the returned ExtractionResult
            // reflects the post-write value without an extra round-trip (M10).
            observationCount++;

            // Gate heuristic re-classification on accumulated drift score to avoid
            // re-classifying every hit when no refit is imminent (M9).
            // The gate predicate is cheap (one cached extractor read) and skips the
            // O(N) heuristic segmentation + classification for stable templates.
            var gateExtractor = await _index.GetExtractorAsync(templateId.Value, cancellationToken);
            var driftGateThreshold = _refit.DriftRefitThreshold * 0.7;
            var accumulatedDrift = gateExtractor?.Centroid.OverallDriftScore ?? 0.0;
            var freshBlocks = accumulatedDrift >= driftGateThreshold
                ? _classifier.Classify(_segmenter.Segment(doc))
                : blocks; // reuse existing classified blocks from the applicator path
            var refit = await _refit.MaybeRefitAsync(templateId.Value, fp, freshBlocks, cancellationToken);
            if (refit.Refitted)
            {
                status = MatchStatus.Refit;
                templateVersion = refit.NewVersion;
                // Use the old fingerprint from BumpVersionAsync so SignatureJaccardDelta is non-zero.
                var oldFp = refit.OldFingerprint ?? fp;
                var diff = TemplateVersionDiffer.Diff(refit.OldExtractor!, refit.NewExtractor!, oldFp, fp, oldFp.PqGramCounts, fp.PqGramCounts);
                _signals?.Raise(StyloExtractSignals.DriftObserved,
                    new StyloExtractSignal(TemplateId: templateId, DriftDelta: diff.SignatureJaccardDelta,
                        OldVersion: refit.OldVersion, NewVersion: refit.NewVersion),
                    key: templateId.Value.ToString("N"));
                _signals?.Raise(StyloExtractSignals.TemplateRefit,
                    new StyloExtractSignal(TemplateId: templateId, OldVersion: refit.OldVersion, NewVersion: refit.NewVersion),
                    key: templateId.Value.ToString("N"));
                _signals?.Raise(StyloExtractSignals.VersionDetected,
                    new StyloExtractSignal(TemplateId: templateId, OldVersion: refit.OldVersion, NewVersion: refit.NewVersion, DriftDelta: diff.SignatureJaccardDelta),
                    key: templateId.Value.ToString("N"));
                await _eventSink.OnVersionChangeAsync(new VersionChangeEvent
                {
                    TemplateId = templateId.Value,
                    HostDisplayName = sourceUri?.Host ?? "",
                    OldVersion = refit.OldVersion,
                    NewVersion = refit.NewVersion,
                    DetectedAt = DateTimeOffset.UtcNow,
                    Diff = diff
                }, cancellationToken);
                blocks = freshBlocks;
            }
        }

        if (status == MatchStatus.Novel && templateId is not null)
        {
            await _eventSink.OnNewTemplateAsync(new NewTemplateEvent
            {
                TemplateId = templateId.Value,
                HostDisplayName = sourceUri?.Host ?? "",
                DetectedAt = DateTimeOffset.UtcNow,
                FingerprintHex = fp.Hex,
                InitialBlockCount = blocks.Count
            }, cancellationToken);
        }

        matchTimer.Stop();

        // Schema.org JSON-LD fallback: when the heuristic produced essentially no content,
        // try to extract from any application/ld+json blobs the page ships. Activates only
        // when the heuristic returned < FallbackMinTextLength chars of trimmed text. The
        // resulting plain text is wrapped in a synthetic MainContent block so the renderer
        // emits it under the chosen profile.
        // Empirical: WCXB diagnostic surfaced 48 pages where the heuristic emits 1-200 chars
        // but the page ships substantial schema.org content (QAPage, DiscussionForumPosting,
        // FAQPage, ItemList, NewsArticle, BlogPosting) that contains the gold answer.
        const int FallbackMinTextLength = 200;
        var combinedText = blocks.Sum(b => b.Text.Length);
        if (combinedText < FallbackMinTextLength)
        {
            var jsonLdText = JsonLdContentExtractor.ExtractMainContent(doc);
            if (!string.IsNullOrWhiteSpace(jsonLdText) && jsonLdText.Length >= FallbackMinTextLength)
            {
                var fallbackBlock = new ExtractedBlock
                {
                    Id = "json-ld",
                    Role = BlockRole.MainContent,
                    Confidence = 0.7,
                    Text = jsonLdText,
                    Markdown = "",
                    XPath = "/structured-data",
                    CssSelector = "script[type='application/ld+json']",
                    TextLength = jsonLdText.Length,
                    LinkDensity = 0.0,
                    Links = Array.Empty<ExtractedLink>(),
                };
                var augmented = new List<ExtractedBlock>(blocks.Count + 1) { fallbackBlock };
                augmented.AddRange(blocks);
                blocks = augmented;
            }
        }

        var renderTimer = Stopwatch.StartNew();
        var markdown = _renderer.Render(blocks, options.Profile);
        renderTimer.Stop();
        total.Stop();

        return new ExtractionResult
        {
            SourceUri = sourceUri,
            Title = doc.Title,
            Markdown = markdown,
            Blocks = blocks,
            Match = new LayoutMatch
            {
                TemplateId = templateId,
                TemplateVersion = templateVersion,
                FingerprintHex = fp.Hex,
                Status = status,
                Similarity = similarity,
                ObservationCount = observationCount,
                LatencyMatch = matchTimer.Elapsed,
                LatencyTotal = total.Elapsed
            },
            Stats = new ExtractionStats
            {
                BlockCount = blocks.Count,
                FingerprintShingleCount = fp.ShingleCount,
                ParseTime = parseTimer.Elapsed,
                FingerprintTime = fpTimer.Elapsed,
                MatchTime = matchTimer.Elapsed,
                RenderTime = renderTimer.Elapsed
            }
        };
    }
}
