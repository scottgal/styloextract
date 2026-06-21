using System.Diagnostics;
using StyloExtract.Abstractions;
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
        ITemplateVersionEventSink eventSink)
    {
        _parser = parser; _cleaner = cleaner; _fingerprinter = fingerprinter;
        _segmenter = segmenter; _classifier = classifier; _renderer = renderer;
        _index = index; _hostHasher = hostHasher; _inducer = inducer; _applicator = applicator;
        _fastPathThreshold = fastPathThreshold; _slowPathThreshold = slowPathThreshold;
        _refit = refit;
        _eventSink = eventSink;
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

        var fpTimer = Stopwatch.StartNew();
        var fp = _fingerprinter.Compute(doc);
        fpTimer.Stop();

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
                observationCount = await _index.GetObservationCountAsync(fastHit.Value, cancellationToken);
                status = MatchStatus.FastPathHit;
            }
            else
            {
                blocks = _classifier.Classify(_segmenter.Segment(doc));
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
                }
                else { blocks = _classifier.Classify(_segmenter.Segment(doc)); }
            }
            else
            {
                blocks = _classifier.Classify(_segmenter.Segment(doc));
                if (options.LearnNewTemplates)
                {
                    var newId = Guid.NewGuid();
                    var ex = _inducer.Induce(newId, blocks);
                    templateId = await _index.RegisterAsync(hostHash, fp, ex, cancellationToken);
                    templateVersion = 1;
                    observationCount = 1;
                    status = MatchStatus.Novel;
                }
            }
        }

        if (templateId is not null && status is MatchStatus.FastPathHit or MatchStatus.SlowPathMatch)
        {
            await _index.RecordObservationAsync(templateId.Value, fp, similarity, cancellationToken);
            var freshBlocks = _classifier.Classify(_segmenter.Segment(doc));
            var refit = await _refit.MaybeRefitAsync(templateId.Value, fp, freshBlocks, cancellationToken);
            if (refit.Refitted)
            {
                status = MatchStatus.Refit;
                templateVersion = refit.NewVersion;
                // Use the old fingerprint from BumpVersionAsync so SignatureJaccardDelta is non-zero.
                var oldFp = refit.OldFingerprint ?? fp;
                var diff = TemplateVersionDiffer.Diff(refit.OldExtractor!, refit.NewExtractor!, oldFp, fp, oldFp.PqGramCounts, fp.PqGramCounts);
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
