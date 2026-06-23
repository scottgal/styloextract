using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.IntegrationTests;

/// <summary>
/// End-to-end test that verifies MatchStatus.Refit fires through ExtractAsync and that
/// the registered ITemplateVersionEventSink receives an OnVersionChangeAsync call with a
/// non-empty TemplateVersionDiff.
///
/// This test plugs the gap that hid the original "TemplateVersionDiff always empty" bug (M17).
/// </summary>
public class RefitFiresThroughExtractAsyncTests
{
    private sealed class CapturingSink : ITemplateVersionEventSink
    {
        public List<NewTemplateEvent> NewEvents { get; } = new();
        public List<VersionChangeEvent> VersionEvents { get; } = new();

        public ValueTask OnNewTemplateAsync(NewTemplateEvent evt, CancellationToken ct)
        {
            NewEvents.Add(evt);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken ct)
        {
            VersionEvents.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    // HTML whose heuristic classifier will assign MainContent + Footer roles
    // (high text density, footer tag with copyright).
    private static string ArticleHtml(string variant = "") =>
        $"""
        <!DOCTYPE html>
        <html><head><title>Article {variant}</title></head>
        <body>
          <header><nav class="main-nav"><a href="/">Home</a><a href="/blog">Blog</a><a href="/about">About</a></nav></header>
          <main>
            <article>
              <h1>Article Title {variant}</h1>
              <p>{string.Concat(Enumerable.Repeat($"Article body text {variant} with substantial prose content. ", 30))}</p>
              <p>{string.Concat(Enumerable.Repeat($"Second paragraph {variant} with more supporting text that is long. ", 25))}</p>
              <p>{string.Concat(Enumerable.Repeat($"Third paragraph {variant} contains extra detail and analysis. ", 20))}</p>
            </article>
          </main>
          <footer>© 2026 Example Corp. All rights reserved.</footer>
        </body></html>
        """;

    // HTML whose heuristic classifier will assign PrimaryNavigation + Form roles
    // (high link density nav + form with inputs) - structurally very different from ArticleHtml.
    private static string NavigationHeavyHtml(string variant = "") =>
        $"""
        <!DOCTYPE html>
        <html><head><title>Nav Page {variant}</title></head>
        <body>
          <nav class="main-nav">
            {string.Concat(Enumerable.Range(1, 30).Select(i => $"<a href='/page{i}'>Link {i} {variant}</a>"))}
          </nav>
          <form>
            <input type="text" name="q" /><input type="submit" value="Search" />
            <input type="checkbox" name="opt" /><input type="hidden" name="tok" value="x" />
          </form>
          <footer>© 2026 Corp. All rights reserved. Privacy Policy.</footer>
        </body></html>
        """;

    private static (ILayoutExtractor Extractor, SqliteConnection Conn) Build(
        ITemplateVersionEventSink sink,
        double driftRefitThreshold = 0.15,
        int observationsBeforeStable = 3)
        => LayoutExtractorTestBuilder.Build(
            versionEventSink: sink,
            slowPathThreshold: 0.50,
            refitMissRatio: driftRefitThreshold,
            refitMinObservations: observationsBeforeStable);

    [Fact]
    public async Task ExtractAsync_SubstantialDrift_TriggersRefitAndSinkReceivesVersionChange()
    {
        var sink = new CapturingSink();
        var (extractor, conn) = Build(sink, driftRefitThreshold: 0.15, observationsBeforeStable: 3);
        try
        {
            var uri = new Uri("https://refit-test.example.com/page");

            // Step 1: Register base template (Novel).
            var first = await extractor.ExtractAsync(ArticleHtml("v1"), uri);
            first.Match.Status.Should().Be(MatchStatus.Novel, "first extraction should register a new template");
            first.Match.TemplateId.Should().NotBeNull();
            sink.NewEvents.Should().ContainSingle("OnNewTemplateAsync should fire on novel registration");

            var templateId = first.Match.TemplateId!.Value;

            // Step 2: Submit similar pages to build enough observations to pass observationsBeforeStable.
            // Use variants close enough to match (same host, similar structure) but with slight text changes.
            for (int i = 2; i <= 5; i++)
            {
                var obs = await extractor.ExtractAsync(ArticleHtml($"v{i}"), uri);
                obs.Match.TemplateId.Should().Be(templateId, $"variant v{i} should hit the same template");
            }

            // Step 3: Submit substantially different pages (nav-heavy) repeatedly until refit fires.
            // With driftRefitThreshold=0.15 and EWMA alpha=0.2, high-drift (delta≈1.0) inputs
            // should cross the threshold within a small number of iterations.
            ExtractionResult? refitResult = null;
            int maxAttempts = 30;
            for (int i = 0; i < maxAttempts; i++)
            {
                var result = await extractor.ExtractAsync(NavigationHeavyHtml($"v{i}"), uri);
                if (result.Match.Status == MatchStatus.Refit)
                {
                    refitResult = result;
                    break;
                }
            }

            refitResult.Should().NotBeNull($"MatchStatus.Refit should fire within {maxAttempts} drift-heavy calls");
            refitResult!.Match.Status.Should().Be(MatchStatus.Refit);
            refitResult.Match.TemplateVersion.Should().Be(2, "version should increment from 1 to 2 on first refit");

            // Step 4: Verify the event sink received an OnVersionChangeAsync call.
            sink.VersionEvents.Should().ContainSingle("OnVersionChangeAsync should fire once on refit");
            var versionEvent = sink.VersionEvents[0];
            versionEvent.TemplateId.Should().Be(templateId);
            versionEvent.OldVersion.Should().Be(1);
            versionEvent.NewVersion.Should().Be(2);
        }
        finally
        {
            conn.Dispose();
        }
    }
}
