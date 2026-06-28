using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral;
using StyloExtract.Abstractions;
using StyloExtract.Abstractions.TemplateEnrichment;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Core.Skeleton;
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

    private readonly IOperatorTemplateStore? _operatorTemplates;
    private readonly ITemplateEnrichmentQueue? _enrichmentQueue;
    private readonly DomSkeletonRenderer? _skeletonRenderer;
    private readonly DeterministicTemplateYamlSink? _deterministicYamlSink;
    private readonly IClassStabilityFilter _stabilityFilter;
    private readonly bool _evaluateEvolvedCandidatesDefault;

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
        ILogger<LayoutExtractor>? logger = null,
        IOperatorTemplateStore? operatorTemplates = null,
        ITemplateEnrichmentQueue? enrichmentQueue = null,
        DomSkeletonRenderer? skeletonRenderer = null,
        DeterministicTemplateYamlSink? deterministicYamlSink = null,
        IClassStabilityFilter? stabilityFilter = null,
        bool evaluateEvolvedCandidatesDefault = false)
    {
        _parser = parser; _cleaner = cleaner; _fingerprinter = fingerprinter;
        _segmenter = segmenter; _classifier = classifier; _renderer = renderer;
        _index = index; _hostHasher = hostHasher; _inducer = inducer; _applicator = applicator;
        _fastPathThreshold = fastPathThreshold; _slowPathThreshold = slowPathThreshold;
        _refit = refit;
        _eventSink = eventSink;
        _signals = signals;
        _logger = logger;
        _operatorTemplates = operatorTemplates;
        _enrichmentQueue = enrichmentQueue;
        // Lazily allocate the renderer only if a queue is wired. The skeleton
        // is the queue payload; without a queue, no renderer is needed.
        _skeletonRenderer = enrichmentQueue is not null ? (skeletonRenderer ?? new DomSkeletonRenderer()) : null;
        _deterministicYamlSink = deterministicYamlSink;
        // Symmetric with inducer + applicator: the same filter the inducer used
        // when emitting an IdentityClaim chain has to evaluate the apply-time
        // match, or a stricter filter here will drop anchor classes from the
        // element set and silently break the match. When no filter is wired in
        // DI the default keeps Task 9 evaluations cheap and consistent.
        _stabilityFilter = stabilityFilter ?? new DefaultClassStabilityFilter();
        _evaluateEvolvedCandidatesDefault = evaluateEvolvedCandidatesDefault;
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
        // Capture schema.org JSON-LD text BEFORE DomCleaner strips the <script> blobs.
        // The fallback later (post-Classify) uses it when the heuristic emits less than
        // FallbackMinTextLength chars. Reading from the cleaned document would always
        // find zero blobs because DomCleaner's combined selector removes all <script>
        // tags including application/ld+json. Keeping JSON-LD scripts in the DOM through
        // Clean would leak their text into the classifier's content blocks (mostlylucid
        // jumps from 635 to 666 lines because the JSON blob's TextContent appears inside
        // <main>); the right shape is "lift out, then strip".
        var preCleanJsonLdText = JsonLdContentExtractor.ExtractMainContent(doc);
        // Discourse pages embed every post in a <div id="data-preloaded"
        // data-preloaded="...JSON..."> blob. The div survives DomCleaner (it's
        // a <div>, not a <script>) but capturing it here keeps the symmetric
        // shape with JSON-LD. Empty for non-Discourse pages.
        var preCleanDiscourseText = DiscourseRehydrationExtractor.ExtractMainContent(doc);
        // Next.js apps embed their page state in <script id="__NEXT_DATA__"
        // type="application/json">. DomCleaner strips ALL scripts so we must
        // capture this before Clean. Empty for non-Next.js pages.
        var preCleanNextDataText = NextDataRehydrationExtractor.ExtractMainContent(doc);
        _cleaner.Clean(doc);
        parseTimer.Stop();
        _signals?.Raise(StyloExtractSignals.ParseDone, default);

        // Operator-template override path. When a host has an operator-authored
        // template the entire induction pipeline (fingerprint, index probe, classify,
        // induce/refit) is skipped — we synthesise a LearnedExtractor from the
        // operator rules and apply it directly. The "hard override" semantics are
        // documented in docs/operator-templates-design.md.
        var resolvedHost = options.HostOverride ?? sourceUri?.Host ?? "";
        if (_operatorTemplates is not null
            && !string.IsNullOrEmpty(resolvedHost)
            && _operatorTemplates.TryGet(resolvedHost, out var operatorTemplate))
        {
            var overrideMatchTimer = Stopwatch.StartNew();
            var synthetic = OperatorTemplateAdapter.ToLearnedExtractor(operatorTemplate);
            var appliedOverride = _applicator.Apply(doc, synthetic);
            overrideMatchTimer.Stop();
            // The override path skips fingerprint + index + classify entirely.
            // Without raising a signal here, ephemeral consumers (the gateway
            // dashboard, the operator metrics surface, the WCXB diagnostic
            // harness) see ParseDone followed by silence and treat the
            // request as a hung extraction. The MatchOperatorOverride signal
            // is the equivalent of MatchFastPathHit for the operator-template
            // branch: it's how downstream surfaces know an override fired.
            _signals?.Raise(StyloExtractSignals.MatchOperatorOverride,
                new StyloExtractSignal(TemplateId: synthetic.TemplateId));
            var overrideRenderTimer = Stopwatch.StartNew();
            var overrideMarkdown = _renderer.Render(appliedOverride.Blocks, options.Profile);
            overrideRenderTimer.Stop();
            total.Stop();
            return new ExtractionResult
            {
                SourceUri = sourceUri,
                Title = null,
                Match = new LayoutMatch
                {
                    Status = MatchStatus.OperatorOverride,
                    TemplateId = synthetic.TemplateId,
                    TemplateVersion = synthetic.Version,
                    FingerprintHex = "",
                    Similarity = 1.0,
                    ObservationCount = 0,
                    LatencyMatch = overrideMatchTimer.Elapsed,
                    LatencyTotal = total.Elapsed,
                },
                Markdown = overrideMarkdown,
                Blocks = appliedOverride.Blocks,
                Stats = new ExtractionStats
                {
                    BlockCount = appliedOverride.Blocks.Count,
                    FingerprintShingleCount = 0,
                    ParseTime = parseTimer.Elapsed,
                    FingerprintTime = TimeSpan.Zero,
                    MatchTime = overrideMatchTimer.Elapsed,
                    RenderTime = overrideRenderTimer.Elapsed,
                },
            };
        }

        var fpTimer = Stopwatch.StartNew();
        var fp = _fingerprinter.Compute(doc);
        fpTimer.Stop();
        _signals?.Raise(StyloExtractSignals.FingerprintComputed, new StyloExtractSignal(FingerprintHex: fp.Hex));

        var hostHash = _hostHasher.Hash(resolvedHost);
        var status = MatchStatus.NovelEphemeral;
        Guid? templateId = null;
        int templateVersion = 0;
        double similarity = 0;
        int observationCount = 0;
        IReadOnlyList<ExtractedBlock> blocks;

        // Bug-out signal: when the cached extractor's selectors produce broken
        // output (empty, chrome-only, or content that's actually noise), force
        // a refit so the next request gets a fresh extractor. The full ruleset
        // and its rationale live in ApplicatorBrokenCheck — including the
        // Move 2 noisy-content gate that catches the Wikipedia / mostlylucid
        // language-picker leak shape.
        static bool IsApplicatorBroken(ApplicatorResult applied) =>
            ApplicatorBrokenCheck.IsBroken(applied);

        bool applicatorBugOut = false;
        bool llmInductionFired = false;
        var matchTimer = Stopwatch.StartNew();
        var fastHit = await _index.ProbeFastPathAsync(hostHash, fp, _fastPathThreshold, cancellationToken);
        if (fastHit is not null)
        {
            var ex = await _index.GetExtractorAsync(fastHit.Value, cancellationToken);
            if (ex is not null)
            {
                var applied = _applicator.Apply(doc, ex);
                blocks = applied.Blocks;
                if (IsApplicatorBroken(applied))
                {
                    _logger?.LogInformation("Fast-path applicator broken (text={Text}, ruleMisses={Miss}/{Total}) on template {TemplateId} v{Version}; bugging out.",
                        applied.Blocks.Sum(b => b.Text.Length), applied.RulesMissed, applied.RulesApplied + applied.RulesMissed, fastHit.Value, ex.Version);
                    blocks = _classifier.Classify(_segmenter.Segment(doc));
                    applicatorBugOut = true;
                }
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
                    var freshEx = _inducer.Induce(fastHit.Value, blocks, doc);
                    templateId = await _index.RegisterAsync(hostHash, fp, freshEx, cancellationToken);
                    templateVersion = 1;
                    observationCount = 1;
                    status = MatchStatus.Novel;
                    _signals?.Raise(StyloExtractSignals.TemplateNovel,
                        new StyloExtractSignal(TemplateId: templateId, FingerprintHex: fp.Hex, HostDisplayName: sourceUri?.Host),
                        key: templateId.Value.ToString("N"));
                    _deterministicYamlSink?.Persist(resolvedHost, freshEx);
                    await AppendObservationsAsync(freshEx, resolvedHost, fp, InducerKind.Heuristic, cancellationToken).ConfigureAwait(false);
                    llmInductionFired |= await MaybeEnqueueEnrichmentAsync(doc, resolvedHost, fp.Hex, cancellationToken).ConfigureAwait(false);
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
                    var applied = _applicator.Apply(doc, ex);
                    blocks = applied.Blocks;
                    if (IsApplicatorBroken(applied))
                    {
                        _logger?.LogInformation("Slow-path applicator broken (text={Text}, ruleMisses={Miss}/{Total}) on template {TemplateId} v{Version}; bugging out.",
                            applied.Blocks.Sum(b => b.Text.Length), applied.RulesMissed, applied.RulesApplied + applied.RulesMissed, slow.Value.TemplateId, ex.Version);
                        blocks = _classifier.Classify(_segmenter.Segment(doc));
                        applicatorBugOut = true;
                    }
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
                    var ex = _inducer.Induce(newId, blocks, doc);
                    templateId = await _index.RegisterAsync(hostHash, fp, ex, cancellationToken);
                    templateVersion = 1;
                    observationCount = 1;
                    status = MatchStatus.Novel;
                    _signals?.Raise(StyloExtractSignals.TemplateNovel,
                        new StyloExtractSignal(TemplateId: templateId, FingerprintHex: fp.Hex, HostDisplayName: sourceUri?.Host),
                        key: templateId.Value.ToString("N"));
                    _deterministicYamlSink?.Persist(resolvedHost, ex);
                    await AppendObservationsAsync(ex, resolvedHost, fp, InducerKind.Heuristic, cancellationToken).ConfigureAwait(false);
                    llmInductionFired |= await MaybeEnqueueEnrichmentAsync(doc, resolvedHost, fp.Hex, cancellationToken).ConfigureAwait(false);
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
            //
            // When applicatorBugOut is set, we already re-classified above. Pass the
            // bug-out flag through to force MaybeRefit irrespective of accumulated drift -
            // the cached extractor is known to be broken on THIS page, no point waiting
            // for EWMA accumulation.
            var gateExtractor = await _index.GetExtractorAsync(templateId.Value, cancellationToken);
            var driftGateThreshold = _refit.DriftRefitThreshold * 0.7;
            var accumulatedDrift = gateExtractor?.Centroid.OverallDriftScore ?? 0.0;
            var freshBlocks = (applicatorBugOut || accumulatedDrift >= driftGateThreshold)
                ? (applicatorBugOut ? blocks : _classifier.Classify(_segmenter.Segment(doc)))
                : blocks; // reuse existing classified blocks from the applicator path
            var refit = await _refit.MaybeRefitAsync(templateId.Value, fp, freshBlocks, applicatorBugOut, cancellationToken);
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
        // Gate on CONTENT-role text mass, not all-block sum. The renderer's
        // MainContentOnly / Wcxb profiles drop Header/Footer/Boilerplate/Nav
        // regardless, so a page whose heuristic emitted 3 KB of chrome but
        // zero MainContent renders to ~0 markdown. WCXB diagnostic: wral.com
        // and similar Next.js pages had this exact shape — bunch of nav +
        // boilerplate found, no actual article body in static DOM, and the
        // sum-based gate prevented the __NEXT_DATA__ rehydration fallback
        // from firing.
        var combinedText = blocks
            .Where(b => b.Role is BlockRole.MainContent or BlockRole.Article
                or BlockRole.Title or BlockRole.Heading or BlockRole.Summary or BlockRole.Table
                or BlockRole.CodeBlock or BlockRole.RepeatedItem)
            .Sum(b => b.Text.Length);
        if (combinedText < FallbackMinTextLength)
        {
            // Use the text we captured before Clean stripped the JSON-LD blobs.
            var jsonLdText = preCleanJsonLdText;
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
            else if (!string.IsNullOrWhiteSpace(preCleanNextDataText) && preCleanNextDataText.Length >= FallbackMinTextLength)
            {
                // Next.js __NEXT_DATA__ rehydration: the page is a Next.js SPA
                // whose content lives under props.pageProps in the JSON blob,
                // not the static DOM. WCXB diagnostic 2026-06-25: wral.com,
                // ruggable.com, nike.com and similar caught here.
                var fallbackBlock = new ExtractedBlock
                {
                    Id = "nextdata-rehydration",
                    Role = BlockRole.MainContent,
                    Confidence = 0.65,
                    Text = preCleanNextDataText,
                    Markdown = "",
                    XPath = "/nextdata-rehydration",
                    CssSelector = "script#__NEXT_DATA__",
                    TextLength = preCleanNextDataText.Length,
                    LinkDensity = 0.0,
                    Links = Array.Empty<ExtractedLink>(),
                };
                var augmented = new List<ExtractedBlock>(blocks.Count + 1) { fallbackBlock };
                augmented.AddRange(blocks);
                blocks = augmented;
            }
            else if (!string.IsNullOrWhiteSpace(preCleanDiscourseText) && preCleanDiscourseText.Length >= FallbackMinTextLength)
            {
                // Discourse data-preloaded rehydration: the page is a Discourse
                // forum SPA whose post bodies live in JSON, not the DOM. Recover
                // the topic title + every post's "cooked" HTML, stripped to
                // text. WCXB diagnostic 2026-06-25: 13 catastrophic forum pages
                // (forums.eveonline.com, forum.level1techs.com, forum.lingq.com,
                // boards.straightdope.com, forum.mssociety.org.uk, etc.) all
                // share this pattern; Discourse powers 5 000+ public forums.
                var fallbackBlock = new ExtractedBlock
                {
                    Id = "discourse-rehydration",
                    Role = BlockRole.MainContent,
                    Confidence = 0.7,
                    Text = preCleanDiscourseText,
                    Markdown = "",
                    XPath = "/discourse-rehydration",
                    CssSelector = "div#data-preloaded",
                    TextLength = preCleanDiscourseText.Length,
                    LinkDensity = 0.0,
                    Links = Array.Empty<ExtractedLink>(),
                };
                var augmented = new List<ExtractedBlock>(blocks.Count + 1) { fallbackBlock };
                augmented.AddRange(blocks);
                blocks = augmented;
            }
            else
            {
                // Body-text fallback: old-school single-page HTML (no <main>/<article>/
                // structural <div> wrappers) gives BlockSegmenter no usable candidates, so
                // the classifier emits ~nothing. Walk body, skip chrome semantic children
                // (<header>/<footer>/<nav>/<aside>), and emit the residue as a synthetic
                // MainContent block.
                //
                // Empirical: WCXB diagnostic post-contamination-fix surfaced 7 article
                // pages still emitting pred_chars=1 (erikdemaine.org/foldcut and similar
                // flat HTML pages with H1/H2/P directly under body). All have meaningful
                // body text once chrome semantic siblings are skipped.
                var bodyText = ExtractBodyTextSkippingChrome(doc);
                if (!string.IsNullOrWhiteSpace(bodyText) && bodyText.Length >= FallbackMinTextLength)
                {
                    var fallbackBlock = new ExtractedBlock
                    {
                        Id = "body-text",
                        Role = BlockRole.MainContent,
                        Confidence = 0.5,
                        Text = bodyText,
                        Markdown = "",
                        XPath = "/html/body",
                        CssSelector = "body",
                        TextLength = bodyText.Length,
                        LinkDensity = 0.0,
                        Links = Array.Empty<ExtractedLink>(),
                    };
                    var augmented = new List<ExtractedBlock>(blocks.Count + 1) { fallbackBlock };
                    augmented.AddRange(blocks);
                    blocks = augmented;
                }
            }
        }

        var renderTimer = Stopwatch.StartNew();
        var markdown = _renderer.Render(blocks, options.Profile);
        renderTimer.Stop();
        total.Stop();

        // Post-render repair-enqueue (Move 3 of the apply-time-quality-gate
        // spec): when an EXISTING template was used (fast-path hit / slow-path
        // match, NOT novel) and the apply-time quality check flagged it broken
        // (empty / chrome-heavy / signal-loss / noisy MainContent), OR the
        // rendered Markdown came out below the FallbackMinTextLength
        // threshold, ask the LLM to repair the template's selectors.
        //
        // Move 3 dropped two preconditions the original gate had:
        //  - the "hand-authored operator template exists" requirement, so that
        //    auto-induced templates also get a second look from the LLM when
        //    they go bad
        //  - the implicit "only fires on empty output" semantics, so that the
        //    Move 2 noisy-content gate (Wikipedia / mostlylucid leak shape) can
        //    actually reach repair
        //
        // Hot-path overhead is one queue lookup + cooldown check. The
        // InMemoryTemplateEnrichmentQueue per-host cooldown (1 hour by
        // default) prevents runaway repair attempts on a host whose template
        // the LLM can't improve.
        // Status set covers every existing-template case: fast/slow path hits
        // are the obvious targets; Refit lands here when the IsApplicatorBroken
        // gate fired earlier and forced a heuristic refit, in which case we
        // STILL want the LLM to look at the host since the heuristic just
        // re-emitted the same shape that went bad. Novel is excluded because
        // MaybeEnqueueEnrichmentAsync already enqueued an Induce job for it.
        const int RepairMarkdownMinLength = FallbackMinTextLength;
        if (status is MatchStatus.FastPathHit or MatchStatus.SlowPathMatch or MatchStatus.Refit &&
            (applicatorBugOut || markdown.Trim().Length < RepairMarkdownMinLength))
        {
            await MaybeEnqueueRepairAsync(doc, resolvedHost, fp.Hex, markdown, cancellationToken).ConfigureAwait(false);
        }

        // Phase 2 Task 9: passive evaluation of evolved-selector candidates.
        // Observation-only — the markdown/blocks above are the user-visible
        // result and are NOT replaced by candidate output. Per-call options
        // take precedence over the builder default so a single extraction can
        // opt in / out independently of the registered StyloExtractOptions.
        if (options.EvaluateEvolvedCandidates || _evaluateEvolvedCandidatesDefault)
        {
            await EvaluateEvolvedCandidatesAsync(resolvedHost, doc, cancellationToken).ConfigureAwait(false);
        }

        return new ExtractionResult
        {
            SourceUri = sourceUri,
            Title = doc.Title,
            Markdown = markdown,
            Blocks = blocks,
            LlmInductionFired = llmInductionFired,
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

    /// <summary>
    /// Enqueue an LLM template-enrichment job for the host that just
    /// produced a novel template. No-op if:
    ///   * no <see cref="ITemplateEnrichmentQueue"/> is wired (the
    ///     deployment isn't running the LLM coordinator);
    ///   * the host is empty (file extraction without a host override);
    ///   * a hand-authored operator template already exists for the host
    ///     (hard-override wins; LLM induction is unnecessary).
    /// On enqueue failure (queue full, cooldown active) the producer
    /// just moves on — the heuristic-induced template covers the request.
    /// Returns true when the job was successfully enqueued (i.e. the LLM
    /// inducer will run for this host), false for every no-op path.
    /// </summary>
    private async Task AppendObservationsAsync(
        LearnedExtractor extractor,
        string host,
        StructuralFingerprint fp,
        InducerKind kind,
        CancellationToken cancellationToken)
    {
        if (extractor.Rules.Count == 0) return;
        // LSH bucket: first band hash cast to a non-negative int. Phase 2 mining
        // can cluster across rows by this bucket. Bands beyond [0] aren't lost —
        // the active template still carries them via SqliteTemplateIndex's
        // template_lsh_band_index table; the observation row only needs one
        // bucket-scope key for cluster queries.
        var bucket = fp.LshBands.Length > 0
            ? unchecked((int)(fp.LshBands[0] & 0x7FFFFFFFUL))
            : 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var rule in extractor.Rules)
        {
            try
            {
                var claims = rule.Claims ?? Array.Empty<IdentityClaim>();
                // Target signature: hash leaf claim's identifying surface. Cardinality
                // proxy: count of selectors emitted for this rule (1 for singleton
                // rules; >1 only on repeated-role chains from Task 52).
                var leaf = claims.Count > 0 ? claims[^1] : null;
                var targetSig = leaf is not null
                    ? leaf.TagHash ^ (leaf.IdHash ?? 0UL)
                    : 0UL;
                await _index.AppendObservationAsync(new TemplateObservation
                {
                    ObservationId = Guid.NewGuid(),
                    Host = host,
                    LshBucket = bucket,
                    Role = rule.Role,
                    Claims = claims,
                    TargetSignature = targetSig,
                    Cardinality = Math.Max(1, rule.CssSelectors.Count),
                    Confidence = rule.MeanConfidence,
                    InducedAt = now,
                    InducerKind = kind,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Audit-only path; failure must not abort extraction.
                _logger?.LogDebug(ex, "AppendObservation failed for host {Host} role {Role}", host, rule.Role);
            }
        }
    }

    private async Task<bool> MaybeEnqueueEnrichmentAsync(
        AngleSharp.Dom.IDocument doc, string host, string fingerprintHex, CancellationToken cancellationToken)
    {
        if (_enrichmentQueue is null || _skeletonRenderer is null) return false;
        if (string.IsNullOrEmpty(host)) return false;
        if (_operatorTemplates is not null && _operatorTemplates.TryGet(host, out _)) return false;

        string skeleton;
        try
        {
            skeleton = _skeletonRenderer.Render(doc);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "skeleton render failed for {Host}; skipping enrichment", host);
            return false;
        }
        if (string.IsNullOrEmpty(skeleton)) return false;

        try
        {
            return await _enrichmentQueue.TryEnqueueAsync(new TemplateEnrichmentJob
            {
                Host = host,
                Skeleton = skeleton,
                FingerprintHex = fingerprintHex,
                CreatedAt = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "enrichment enqueue failed for {Host}", host);
            return false;
        }
    }

    /// <summary>
    /// Enqueue a repair job for an existing operator template that produced
    /// low-quality Markdown for the current page. Counterpart of
    /// <see cref="MaybeEnqueueEnrichmentAsync"/>; the runtime path never
    /// blocks on the result.
    /// </summary>
    private async Task MaybeEnqueueRepairAsync(
        AngleSharp.Dom.IDocument doc, string host, string fingerprintHex,
        string badMarkdown, CancellationToken cancellationToken)
    {
        if (_enrichmentQueue is null || _skeletonRenderer is null) return;
        if (string.IsNullOrEmpty(host)) return;

        string skeleton;
        try
        {
            skeleton = _skeletonRenderer.Render(doc);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "skeleton render failed for {Host}; skipping repair enqueue", host);
            return;
        }
        if (string.IsNullOrEmpty(skeleton)) return;

        // Truncate the bad markdown sample fed to the LLM repair prompt.
        // Bumped from 400 → 2000: 400 chars often didn't contain enough of
        // the broken output to show the LLM where the template went wrong
        // (e.g. a Wikipedia language-picker leak runs well past 400 chars
        // before the actual article body shows up). 2000 still keeps the
        // queue job small while giving the LLM enough context.
        const int MaxBadSampleChars = 2000;
        var sample = badMarkdown.Length <= MaxBadSampleChars
            ? badMarkdown
            : badMarkdown[..MaxBadSampleChars];

        try
        {
            await _enrichmentQueue.TryEnqueueAsync(new TemplateEnrichmentJob
            {
                Host = host,
                Skeleton = skeleton,
                FingerprintHex = fingerprintHex,
                CreatedAt = DateTimeOffset.UtcNow,
                Kind = StyloExtract.Abstractions.TemplateEnrichment.EnrichmentJobKind.Repair,
                BadMarkdownSample = sample,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "repair enqueue failed for {Host}", host);
        }
    }

    /// <summary>
    /// Phase 2 Task 9: passive evaluation of mined evolved-selector candidates
    /// for <paramref name="host"/>. For every candidate row, apply its claim
    /// chain against the document and record win (>= 1 match) or loss (0 match)
    /// via <see cref="ITemplateIndex.RecordCandidateOutcomeAsync"/>. Cached
    /// extraction output is unaffected; this is observation-only telemetry
    /// that lets Task 11 promote high-reputation candidates safely.
    ///
    /// Per-host candidate count is bounded (the corpus miner caps emissions)
    /// so the synchronous await keeps the implementation simple. Failures are
    /// best-effort — a single bad candidate must not abort extraction.
    /// </summary>
    private async Task EvaluateEvolvedCandidatesAsync(
        string host,
        AngleSharp.Dom.IDocument doc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(host)) return;

        IReadOnlyList<EvolvedSelectorCandidate> candidates;
        try
        {
            candidates = await _index.GetCandidatesByHostAsync(host, role: null, limit: 100, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "GetCandidatesByHost failed for host {Host}", host);
            return;
        }
        if (candidates.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in candidates)
        {
            try
            {
                var matched = IdentityClaimApplicator.Apply(candidate.Claims, doc, _stabilityFilter);
                var won = matched.Count > 0;
                await _index.RecordCandidateOutcomeAsync(candidate.CandidateId, won, now, cancellationToken)
                    .ConfigureAwait(false);

                _signals?.Raise(StyloExtractSignals.CandidateOutcome,
                    new StyloExtractSignal(
                        CandidateId: candidate.CandidateId,
                        HostDisplayName: host,
                        Won: won,
                        MatchedElementCount: matched.Count),
                    key: candidate.CandidateId.ToString("N"));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Candidate {Id} evaluation failed for host {Host}",
                    candidate.CandidateId, host);
            }
        }
    }

    private static string ExtractBodyTextSkippingChrome(AngleSharp.Dom.IDocument doc)
    {
        var body = doc.Body;
        if (body is null) return string.Empty;

        // Walk body's direct + transitive children, skipping subtrees rooted at chrome
        // semantic tags so site nav/header/footer/aside don't leak into the fallback.
        // No allocations beyond the StringBuilder; iterative DFS uses a Stack.
        var sb = new System.Text.StringBuilder();
        var stack = new Stack<AngleSharp.Dom.IElement>();
        foreach (var child in body.Children) stack.Push(child);

        while (stack.Count > 0)
        {
            var el = stack.Pop();
            var tag = el.LocalName;
            if (tag is "nav" or "header" or "footer" or "aside") continue;

            // Leaf-ish element with no element children: append its text directly.
            if (el.ChildElementCount == 0)
            {
                var text = el.TextContent;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append(text);
                    sb.Append(' ');
                }
                continue;
            }

            // Has element children: push children to walk into them.
            foreach (var c in el.Children) stack.Push(c);
        }

        return sb.ToString().Trim();
    }
}
