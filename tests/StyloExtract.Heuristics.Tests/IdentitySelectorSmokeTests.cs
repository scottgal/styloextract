using System.Text.RegularExpressions;
using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Phase-1 Task 2 user-visible canary. Inducts a template from a real-world
/// fixture and asserts the emitted CSS-selector strings now carry identity
/// affixes (id / class / data-* / aria-* / role) - not bare positional tag
/// chains like <c>body &gt; div &gt; div &gt; main</c>.
///
/// Uses the existing mostlylucid-home.html fixture (no BBC News fixture
/// exists in the repo as of Task 2 commit).
/// </summary>
public class IdentitySelectorSmokeTests
{
    private static string ReadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return File.ReadAllText(path);
    }

    [Fact]
    public void Induce_FromMostlylucidHome_EmitsIdentityRichSelectors()
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        IDomCleaner cleaner = new DomCleaner();
        IBlockSegmenter segmenter = new BlockSegmenter();
        IBlockClassifier classifier = HeuristicBlockClassifier.LoadFromEmbeddedResources();
        IExtractorInducer inducer = new ExtractorInducer();

        var html = ReadFixture("mostlylucid-home.html");
        var doc = parser.Parse(html);
        cleaner.Clean(doc);
        var blocks = classifier.Classify(segmenter.Segment(doc));

        var extractor = inducer.Induce(Guid.NewGuid(), blocks, doc);

        extractor.Rules.Should().NotBeEmpty("a real-world fixture must produce at least one rule");

        // At least one emitted selector must carry an identity affix - the
        // entire point of Phase 1 Task 2.
        var selectors = extractor.Rules.SelectMany(r => r.CssSelectors).ToList();
        var identityRich = new Regex(@"#[A-Za-z_][\w-]*|\.[A-Za-z_][\w-]*|\[(data|aria)-[^=\]]+=|\[role=", RegexOptions.Compiled);

        selectors.Should().Contain(s => identityRich.IsMatch(s),
            "at least one CSS selector must contain an id/class/data-*/aria-*/role affix, not just bare tag chains");

        // At least one rule must carry a non-null Claims chain so Task 3 can
        // pick it up via BlockRule.Claims.
        extractor.Rules.Should().Contain(r => r.Claims != null && r.Claims.Count > 0,
            "at least one BlockRule must have an identity-claim chain populated for Task 3");
    }
}
