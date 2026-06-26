using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// When the deterministic heuristic inducer fires (Novel match), <see cref="LayoutExtractor"/>
/// must also write a <c>{host}-deterministic.yaml</c> file alongside the SQLite registration.
/// The YAML is best-effort / non-blocking — the SQLite store remains the source of truth at
/// match time — but the file is the auditable, hand-editable mirror that mirrors how
/// LLM-induced templates are written by <c>TemplateEnrichmentCoordinator</c>.
///
/// <para>The rule set in the YAML must carry every role the heuristic detected — not just
/// MainContent — so that an operator browsing the file can see the full classification
/// (Title, MainContent, PrimaryNavigation, Footer, etc.).</para>
/// </summary>
public class DeterministicTemplateYamlPersistenceTests
{
    private static (ILayoutExtractor Extractor, SqliteConnection Conn, string Root) Build()
    {
        var cs = $"Data Source=file:testdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fingerprinter = new StructuralFingerprinter(
            new ShingleGenerator(noise),
            sketcher,
            new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher),
            new PqGramExtractor());

        var root = Path.Combine(Path.GetTempPath(), "stylo-det-yaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new DeterministicTemplateYamlSink(root);

        var extractor = new LayoutExtractor(
            new AngleSharpHtmlDomParser(),
            new DomCleaner(),
            fingerprinter,
            new BlockSegmenter(),
            HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(),
            index,
            new HostHasher(new byte[32]),
            new ExtractorInducer(),
            new ExtractorApplicator(),
            fastPathThreshold: 0.85,
            slowPathThreshold: 0.75,
            new RefitOrchestrator(index, new ExtractorInducer(), 0.35, 5, 3),
            new DefaultNoopVersionEventSink(),
            deterministicYamlSink: sink);
        return (extractor, conn, root);
    }

    [Fact]
    public async Task ExtractAsync_NovelMatch_WritesDeterministicYamlWithMultipleRoles()
    {
        // A page with substantial body, nav, and footer so the heuristic identifies
        // multiple distinct roles. The YAML written by the sink must carry rules for
        // each role the heuristic emitted — not just MainContent.
        var html =
            "<html><body>" +
            "<header><nav class='primary-nav'>" +
            "<a href='/'>Home</a><a href='/about'>About</a><a href='/blog'>Blog</a>" +
            "<a href='/docs'>Docs</a><a href='/contact'>Contact</a>" +
            "</nav></header>" +
            "<main><article>" +
            "<h1>The Substantial Article Title</h1>" +
            "<p>" + new string('x', 600) + "</p>" +
            "</article></main>" +
            "<footer>© 2026 Acme. All rights reserved worldwide and on the moon.</footer>" +
            "</body></html>";

        var (e, conn, root) = Build();
        try
        {
            var result = await e.ExtractAsync(
                html,
                sourceUri: new Uri("https://example-yaml-test.com/post/123"),
                options: new ExtractionOptions { LearnNewTemplates = true });

            result.Match.Status.Should().Be(MatchStatus.Novel,
                "the test relies on the heuristic inducer firing on a first-seen page");

            var expectedPath = Path.Combine(root, "example-yaml-test.com-deterministic.yaml");
            File.Exists(expectedPath).Should().BeTrue(
                "deterministic inducer must persist YAML alongside the SQLite write");

            var yaml = File.ReadAllText(expectedPath);
            yaml.Should().Contain("host: example-yaml-test.com");
            yaml.Should().Contain("rules:");

            // The blocks the heuristic emitted should each surface as a rule.
            var distinctRoles = result.Blocks.Select(b => b.Role).Distinct().ToList();
            distinctRoles.Count.Should().BeGreaterThan(1,
                "the test fixture is shaped so the heuristic classifies at least two distinct roles");

            // Every detected role appears in the YAML rules.
            foreach (var role in distinctRoles)
            {
                yaml.Should().Contain($"role: {role}",
                    $"the deterministic YAML must carry a rule for every role the heuristic emitted; '{role}' missing");
            }

            // Round-trip: the YAML must re-parse via the canonical loader.
            var parsed = YamlOperatorTemplateLoader.Parse(yaml);
            parsed.Host.Should().Be("example-yaml-test.com");
            parsed.Rules.Should().HaveCountGreaterThan(0);
        }
        finally
        {
            conn.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
