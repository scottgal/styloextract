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

    /// <summary>
    /// Phase-1 Task 51 / 2.1 regression. On the alpha.19 mostlylucid.net smoke
    /// the PrimaryNavigation anchor came out as <c>ul.sm:gap-2</c> — a Tailwind
    /// responsive utility class. After the stability filter was extended to
    /// reject Tailwind utilities, NO emitted claim chain should contain a
    /// Tailwind variant prefix (<c>sm:</c>, <c>md:</c>, <c>dark:</c>, etc.)
    /// or a known utility class (<c>gap-N</c>, <c>p-N</c>, <c>flex</c>, etc.).
    /// </summary>
    [Fact]
    public void Induce_FromMostlylucidHome_EmitsNoTailwindUtilityClasses()
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

        // Collect every class token across every claim across every rule.
        var allClasses = extractor.Rules
            .Where(r => r.Claims is not null)
            .SelectMany(r => r.Claims!)
            .SelectMany(c => c.Classes)
            .ToList();

        // Tailwind variant classes always contain ':' — sm:, md:, lg:, dark:,
        // hover:, focus:, etc. None should leak through.
        var variantClasses = allClasses.Where(c => c.Contains(':')).ToList();
        variantClasses.Should().BeEmpty(
            "no Tailwind variant class (containing ':') may anchor a claim — found: "
            + string.Join(", ", variantClasses));

        // Known utility classes that appear in mostlylucid-home.html — none
        // should leak through.
        var bannedUtilities = new[]
        {
            "sm:gap-2", "gap-1", "gap-2", "gap-4", "gap-6", "gap-8",
            "p-2", "p-4", "p-6", "px-4", "py-2", "m-2", "mb-3", "mt-3",
            "flex", "grid", "block", "hidden", "absolute", "relative", "sticky", "fixed",
            "text-xl", "text-2xl", "text-sm", "text-base",
            "bg-white", "bg-gray-100", "rounded", "rounded-md", "rounded-lg",
            "w-full", "h-screen", "z-10", "z-40", "z-50",
        };
        foreach (var u in bannedUtilities)
        {
            allClasses.Should().NotContain(u,
                $"Tailwind utility '{u}' must be rejected by the stability filter");
        }

        // Sanity: still produced rules with identity. (If everything got
        // filtered out we'd have a different problem.)
        extractor.Rules.Should().Contain(r => r.Claims != null && r.Claims.Count > 0);
    }
}
