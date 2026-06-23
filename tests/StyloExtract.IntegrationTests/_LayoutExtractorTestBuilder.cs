using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;

namespace StyloExtract.IntegrationTests;

/// <summary>
/// Single source of truth for the LayoutExtractor wiring that tests use.
///
/// <para>
/// Before this helper there were a dozen near-identical hand-rolled
/// `new LayoutExtractor(...)` sites across the integration / benchmark /
/// Playwright test projects. The 16-arg constructor meant every signature
/// change (e.g. the operatorTemplates parameter that landed in
/// the operator-template editing pass) had to be replicated in twelve
/// places, and a forgotten one would compile only by accident.
/// </para>
///
/// <para>
/// The builder collapses every call site to a single line that returns a
/// (LayoutExtractor, SqliteConnection) tuple. The connection is the caller's
/// to dispose. Per-test overrides go through the optional named parameters;
/// every threshold defaults to the production AddStyloExtract value so the
/// builder isn't a parallel configuration surface.
/// </para>
/// </summary>
internal static class LayoutExtractorTestBuilder
{
    public static (ILayoutExtractor Extractor, SqliteConnection Conn) Build(
        IOperatorTemplateStore? operatorTemplates = null,
        Mostlylucid.Ephemeral.TypedSignalSink<StyloExtractSignal>? signals = null,
        ITemplateVersionEventSink? versionEventSink = null,
        double fastPathThreshold = 0.85,
        double slowPathThreshold = 0.75,
        double refitMissRatio = 0.35,
        int refitMinObservations = 5,
        int refitMinRules = 3)
    {
        var cs = $"Data Source=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fp = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        var extractor = new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold, slowPathThreshold,
            new RefitOrchestrator(index, new ExtractorInducer(), refitMissRatio, refitMinObservations, refitMinRules),
            versionEventSink ?? new DefaultNoopVersionEventSink(),
            signals: signals,
            operatorTemplates: operatorTemplates);
        return (extractor, conn);
    }
}
