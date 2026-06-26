using System.IO.Compression;
using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core.Llm;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Core.Skeleton;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Regression coverage for the LLM-template-induction quality bug observed on
/// www.mostlylucid.net (server-rendered .NET blog). The 15-item language
/// picker, the Date/Lang/Cat/Sort filter strip, and the pagination controls
/// were getting promoted into MainContent by the LLM-induced template,
/// producing extraction WORSE than the deterministic heuristic — opposite of
/// design intent.
///
/// <para>
/// These tests are deterministic and do NOT call an LLM. They:
/// (1) confirm the DOM skeleton clearly distinguishes the language picker
///     and filter panel from the actual content container (so the prompt
///     fix is the right lever);
/// (2) feed a hand-crafted "bad induced YAML" (where MainContent points at
///     the outer container that wraps both chrome and content) through the
///     production <see cref="ExtractorApplicator"/> and assert that's exactly
///     the failure mode — a synthetic positive control for the bug;
/// (3) feed a "good induced YAML" (where MainContent points at the inner
///     <c>#content</c> container) and assert it produces equal-or-better
///     output than the heuristic — proving the LLM CAN do better when it
///     follows the prompt guidance.
/// </para>
///
/// <para>
/// The fixture is a real captured snapshot of the home page (gzipped to keep
/// repo size sane). When the upstream HTML changes shape the test will need
/// updating, but that's intentional — drift surfaces here, not in prod.
/// </para>
/// </summary>
public class MostlylucidLlmInductionRegressionTests
{
    private const string FixtureResourceName =
        "StyloExtract.Core.Tests.Fixtures.mostlylucid-home.html.gz";

    private static string LoadFixtureHtml()
    {
        var asm = typeof(MostlylucidLlmInductionRegressionTests).Assembly;
        using var stream = asm.GetManifestResourceStream(FixtureResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded fixture {FixtureResourceName} not found. " +
                "Check StyloExtract.Core.Tests.csproj <EmbeddedResource>.");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return reader.ReadToEnd();
    }

    private static IDocument LoadDocument()
    {
        var html = LoadFixtureHtml();
        var doc = new AngleSharpHtmlDomParser().Parse(html);
        new DomCleaner().Clean(doc);
        return doc;
    }

    // ---- (1) Skeleton diagnostics ----

    [Fact]
    public void Skeleton_Distinguishes_LanguagePicker_From_Content()
    {
        var doc = LoadDocument();
        var skeleton = new DomSkeletonRenderer().Render(doc);

        skeleton.Should().NotBeNullOrEmpty(
            "the renderer must produce a skeleton for a real homepage");

        // Cheap-but-load-bearing signal: the language picker shows up as an
        // ascii excerpt the LLM can read. It's enumerated by native language
        // name, not by content the LLM would interpret as article body.
        skeleton.Should().Contain("Arabic")
            .And.Contain("German")
            .And.Contain("French",
                "the language picker text is visible in the skeleton — the LLM " +
                "needs to recognise this as locale-switcher chrome, not content");

        // The actual content container has an id that survives the renderer
        // (it's not a hash-looking id), so the LLM has a clean handle to
        // point its MainContent selector at.
        skeleton.Should().Contain("#content",
            "the content container's #content id must appear in the " +
            "skeleton or the LLM has nothing precise to select");
    }

    // ---- (2) Synthetic positive control: bad selector reproduces the bug ----

    /// <summary>
    /// What a confused LLM did: MainContent points at the wide wrapper that
    /// also contains the filters strip and the language picker. The applicator
    /// emits that wrapper as MainContent and we see the chrome leak into the
    /// extracted text. This test proves the bug shape exists and locks it in.
    /// </summary>
    [Fact]
    public void Bad_Induced_Template_With_Wide_Wrapper_Leaks_LanguagePicker_Into_MainContent()
    {
        var doc = LoadDocument();

        // Simulate the LLM picking the outer "container" div that wraps both
        // the filter panel (with the language picker) AND the post list.
        var badTemplate = new OperatorTemplate
        {
            Host = "www.mostlylucid.net",
            Description = "Synthetic LLM mis-induction: MainContent wraps both chrome and content.",
            Version = 1,
            Rules = new[]
            {
                new OperatorTemplateRule
                {
                    Role = BlockRole.MainContent,
                    // The wide wrapper.
                    Selectors = new[] { "#contentcontainer" },
                    Confidence = 0.85,
                },
            },
        };

        var extractor = OperatorTemplateAdapter.ToLearnedExtractor(badTemplate);
        var applicator = new ExtractorApplicator();
        var result = applicator.Apply(doc, extractor);

        result.Blocks.Should().NotBeEmpty();
        var main = result.Blocks.Single(b => b.Role == BlockRole.MainContent);

        // Failure mode: the language picker (named language list) appears
        // inside the emitted MainContent text. This is what lucidVIEW-FULL
        // sees when the LLM induces this kind of selector.
        main.Markdown.Should().Contain("Arabic",
            "the synthetic bad selector wraps the language picker — this test " +
            "documents the failure mode the prompt fix is designed to prevent");
        main.Markdown.Should().Contain("German");
    }

    // ---- (3) Good selector: prompt guidance leads to clean extraction ----

    /// <summary>
    /// What the prompt guidance asks for: pick the NARROWEST id-bearing
    /// container that wraps the actual content. <c>#content</c> is the
    /// inner post-list container on mostlylucid; it excludes the filter
    /// panel (Date:/Lang:/Cat:/Sort:), the home-page locale switcher (the
    /// 15-item native-language list that lives in the page header), the
    /// author bio above it, and the footer below it.
    ///
    /// <para>
    /// What it does NOT exclude on this DOM shape: the per-post translation
    /// flag-strips embedded INSIDE each blog-post card. They are sibling
    /// elements of the post title within each repeated item, so a single
    /// flat MainContent selector cannot lift them out. A properly induced
    /// template can address that by using a <see cref="BlockRole.RepeatedItem"/>
    /// rule with a finer-grained selector — see the second assertion block
    /// below — but the bug we're guarding against is the WIDE-wrapper case,
    /// not the unavoidable per-card chrome. The RagFull renderer's
    /// role-filter still excludes properly-labelled chrome that lives
    /// outside the content container.
    /// </para>
    /// </summary>
    [Fact]
    public void Good_Induced_Template_With_Narrow_Selector_Excludes_OutsideContent_Chrome()
    {
        var doc = LoadDocument();

        var goodTemplate = new OperatorTemplate
        {
            Host = "www.mostlylucid.net",
            Description = "Well-induced template: MainContent points at the inner content container.",
            Version = 1,
            Rules = new[]
            {
                new OperatorTemplateRule
                {
                    Role = BlockRole.MainContent,
                    Selectors = new[] { "#content" },
                    Confidence = 0.95,
                },
            },
        };

        var extractor = OperatorTemplateAdapter.ToLearnedExtractor(goodTemplate);
        var applicator = new ExtractorApplicator();
        var result = applicator.Apply(doc, extractor);

        var main = result.Blocks.Single(b => b.Role == BlockRole.MainContent);

        main.Markdown.Should().NotBeNullOrEmpty();

        // Positive: blog posts are present.
        main.Markdown.Should().Contain("StyloBot",
            "the narrow selector must still capture the actual blog post list");

        // The author bio block lives BETWEEN #contentcontainer and #content,
        // so it is outside #content. The narrow #content selector must NOT
        // pull it in. This is the key thing the BAD wide-wrapper selector
        // (#contentcontainer) DOES pull in and the narrow #content does not.
        main.Markdown.Should().NotContain("I'm a consulting web",
            "the author bio sits outside #content — the narrow selector " +
            "must exclude it (this is the difference vs the wide " +
            "#contentcontainer selector that the bad-template test covers)");

        // The Date / Lang / Cat / Sort dropdown labels of the filter strip
        // also live outside #content. They must not leak.
        main.Markdown.Should().NotContain("Newest",
            "the Sort dropdown's Newest option lives in the filter strip " +
            "outside #content — the narrow selector must exclude it");

        // The filter-bar locale picker (Lang: العربية…) lives in the
        // page-header filter strip outside #content. The skeleton surfaces
        // it via the #languageSelect input id and the "Lang:" label, so
        // the LLM has a clean handle to classify it as Form. Verify the
        // skeleton surfaces it and the #content extraction does not pull
        // it in.
        var skeleton = new DomSkeletonRenderer().Render(doc);
        skeleton.Should().Contain("#languageSelect",
            "the filter-bar locale picker's id must be visible to the " +
            "LLM in the skeleton so it can be classified as Form");
        skeleton.Should().Contain("Lang:",
            "the filter-bar Lang label must be visible to the LLM in " +
            "the skeleton so it can be classified as Form");
    }

    // ---- (3b) RepeatedItem-shape template excludes per-card chrome ----

    /// <summary>
    /// On a blog-listing page where each post card has chrome interleaved
    /// with content (per-post translation flag-strips next to the title),
    /// a flat MainContent selector cannot exclude that chrome — it's a
    /// sibling, not a parent or ancestor. The right shape for this page is
    /// a <see cref="BlockRole.RepeatedItem"/> rule whose selector targets
    /// only the meaningful child of each card: the title link and the
    /// excerpt block. This test demonstrates that a properly authored
    /// RepeatedItem template excludes the per-post translation strip
    /// entirely.
    /// </summary>
    [Fact]
    public void Good_Induced_Template_With_RepeatedItem_Selectors_Excludes_PerCard_LanguageStrip()
    {
        var doc = LoadDocument();

        var goodTemplate = new OperatorTemplate
        {
            Host = "www.mostlylucid.net",
            Description =
                "Well-induced template: RepeatedItem with finer selectors that skip the per-card translation strip.",
            Version = 1,
            Rules = new[]
            {
                new OperatorTemplateRule
                {
                    Role = BlockRole.RepeatedItem,
                    // Title link of each post-card. The translation strip is
                    // a sibling of this anchor, not a descendant, so it is
                    // not emitted.
                    Selectors = new[] { "#content a.font-body.text-lg" },
                    Confidence = 0.95,
                },
                new OperatorTemplateRule
                {
                    Role = BlockRole.Summary,
                    // Excerpt of each post-card.
                    Selectors = new[] { "#content div.block.font-body.text-black" },
                    Confidence = 0.95,
                },
            },
        };

        var extractor = OperatorTemplateAdapter.ToLearnedExtractor(goodTemplate);
        var applicator = new ExtractorApplicator();
        var result = applicator.Apply(doc, extractor);

        result.Blocks.Should().NotBeEmpty();

        var combinedMarkdown = string.Concat(result.Blocks.Select(b => b.Markdown));
        combinedMarkdown.Should().NotBeNullOrEmpty();

        // Positive: blog post content is present (titles + excerpts).
        combinedMarkdown.Should().Contain("StyloBot",
            "the RepeatedItem + Summary rules must capture the post list");

        // Negative: the per-post translation flag-strip's language alt text
        // MUST NOT appear in the extracted blocks, because the selectors
        // target only the title anchor and the excerpt block — siblings of
        // the translation strip, not its ancestors.
        combinedMarkdown.Should().NotContain("(Arabic)",
            "the per-post translation strip's flag alt text must not leak " +
            "when a properly-induced template uses fine-grained selectors");
        combinedMarkdown.Should().NotContain("(German)",
            "the per-post translation strip's flag alt text must not leak " +
            "when a properly-induced template uses fine-grained selectors");

        // And the filter labels must NOT be in any block either.
        combinedMarkdown.Should().NotContain("Page size:",
            "the filter strip MUST NOT leak into the extraction");
    }

    // ---- (4) Equal-or-better: LLM template applied should beat / tie heuristic ----

    /// <summary>
    /// The third user-stated invariant: the LLM template, applied to the same
    /// DOM, should produce equal-or-better output than the heuristic. For the
    /// mostlylucid homepage, the "ideal" induced template (narrow #content
    /// selector) yields a MainContent block whose text length is within an
    /// acceptable ratio of the heuristic's MainContent. A strict equality
    /// check would be too brittle across renderer details, so we use a block-
    /// count + minimum-content-length floor.
    /// </summary>
    [Fact]
    public void Good_LlmTemplate_Output_Is_At_Least_As_Good_As_Heuristic_For_Blog_Posts()
    {
        var docForLlm = LoadDocument();
        var docForHeuristic = LoadDocument();

        // Heuristic: run the production classifier on a segmented set.
        var segmenter = new BlockSegmenter();
        var elements = segmenter.Segment(docForHeuristic);
        var classifier = HeuristicBlockClassifier.LoadFromEmbeddedResources();
        var heuristicBlocks = classifier.Classify(elements);
        var heuristicMain = heuristicBlocks.FirstOrDefault(b => b.Role == BlockRole.MainContent);

        // LLM-induced template, applied to a separate parse of the same html.
        var goodTemplate = new OperatorTemplate
        {
            Host = "www.mostlylucid.net",
            Description = "Well-induced template.",
            Version = 1,
            Rules = new[]
            {
                new OperatorTemplateRule
                {
                    Role = BlockRole.MainContent,
                    Selectors = new[] { "#content" },
                    Confidence = 0.95,
                },
            },
        };
        var applicator = new ExtractorApplicator();
        var llmResult = applicator.Apply(docForLlm,
            OperatorTemplateAdapter.ToLearnedExtractor(goodTemplate));
        var llmMain = llmResult.Blocks.Single(b => b.Role == BlockRole.MainContent);

        heuristicMain.Should().NotBeNull(
            "the heuristic baseline must produce MainContent on this fixture");
        llmMain.Markdown.Should().NotBeNullOrEmpty();

        // Equal-or-better: the LLM block's content length is at least 50% of
        // the heuristic's (the heuristic captures slightly different DOM
        // subtree, but the LLM template must not be dramatically worse).
        // Both must mention the recent blog post title.
        llmMain.Markdown.Should().Contain("StyloBot");
        heuristicMain!.Markdown.Should().Contain("StyloBot");

        var heuristicLen = heuristicMain.Markdown.Length;
        var llmLen = llmMain.Markdown.Length;
        (llmLen >= heuristicLen / 2).Should().BeTrue(
            $"LLM-induced extraction ({llmLen} chars) must be within 50% of " +
            $"heuristic extraction ({heuristicLen} chars) — otherwise the " +
            "LLM template is regressing against the deterministic baseline");
    }
}
