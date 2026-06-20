using System.Diagnostics;
using StyloExtract.Abstractions;

namespace StyloExtract.Core;

public sealed class LayoutExtractor : ILayoutExtractor
{
    private readonly IHtmlDomParser _parser;
    private readonly IDomCleaner _cleaner;
    private readonly IBlockSegmenter _segmenter;
    private readonly IBlockClassifier _classifier;
    private readonly IMarkdownRenderer _renderer;

    public LayoutExtractor(
        IHtmlDomParser parser,
        IDomCleaner cleaner,
        IBlockSegmenter segmenter,
        IBlockClassifier classifier,
        IMarkdownRenderer renderer)
    {
        _parser = parser;
        _cleaner = cleaner;
        _segmenter = segmenter;
        _classifier = classifier;
        _renderer = renderer;
    }

    public Task<ExtractionResult> ExtractAsync(
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

        var segmented = _segmenter.Segment(doc);
        var blocks = _classifier.Classify(segmented);

        var renderTimer = Stopwatch.StartNew();
        var markdown = _renderer.Render(blocks, options.Profile);
        renderTimer.Stop();

        total.Stop();

        var result = new ExtractionResult
        {
            SourceUri = sourceUri,
            Title = doc.Title,
            Markdown = markdown,
            Blocks = blocks,
            Match = new LayoutMatch
            {
                TemplateId = null,
                TemplateVersion = 0,
                FingerprintHex = "",
                Status = MatchStatus.NovelEphemeral,
                Similarity = 0,
                ObservationCount = 0,
                LatencyMatch = TimeSpan.Zero,
                LatencyTotal = total.Elapsed
            },
            Stats = new ExtractionStats
            {
                BlockCount = blocks.Count,
                FingerprintShingleCount = 0,
                ParseTime = parseTimer.Elapsed,
                FingerprintTime = TimeSpan.Zero,
                MatchTime = TimeSpan.Zero,
                RenderTime = renderTimer.Elapsed
            }
        };
        return Task.FromResult(result);
    }
}
