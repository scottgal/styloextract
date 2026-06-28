using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Pins rejection of blocks whose text is dominated by `key: value` line
/// pairs. These are page metadata / YAML frontmatter / Jekyll-style headers
/// that some sites surface in the DOM (MS Learn renders them next to the
/// article body; certain static-site generators show them in admin panels).
/// They are NOT content and must not win MainContent.
///
/// Architectural rule (not per-site): when a candidate block's text has
/// &gt; 50% of its non-blank lines matching the key:value shape AND the
/// block has no real prose paragraph (no line ending in '.'), it's
/// metadata, not content. Classify as Boilerplate so it can't outscore
/// the real article body.
/// </summary>
public class MetadataShapeRejectionTests
{
    private static IReadOnlyList<ExtractedBlock> Classify(string html)
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        IDomCleaner cleaner = new DomCleaner();
        IBlockSegmenter segmenter = new BlockSegmenter();
        IBlockClassifier classifier = HeuristicBlockClassifier.LoadFromEmbeddedResources();
        IDocument doc = parser.Parse(html);
        cleaner.Clean(doc);
        return classifier.Classify(segmenter.Segment(doc));
    }

    [Fact]
    public void MS_Learn_Shape_Yaml_Frontmatter_Does_Not_Win_MainContent()
    {
        // Models MS Learn's leak: the Playwright-rendered DOM exposes the
        // page's YAML config as plain DOM elements (one line per element)
        // alongside the actual content. The YAML block has substantial char
        // count which can outscore a shorter article intro.
        var yamlPairs = new[]
        {
            ("title", "csharp_tour"),
            ("description", "Get started, tutorials, references - C# tour of C# Microsoft Learn"),
            ("summary", "Learn how to write any application using the C# programming language"),
            ("ms_topic", "tutorial"),
            ("author", "BillWagner"),
            ("ms_author", "wiwagn"),
            ("ms_date", "06/27/2025"),
            ("breadcrumb_path", "/dotnet/breadcrumb/toc.json"),
            ("ms_devlang", "csharp"),
            ("page_type", "conceptual"),
            ("ms_assetid", "a9b8c7d6-e5f4-3210-9876-543210fedcba"),
            ("ms_custom_internal_review", "2024-12"),
            ("ms_reviewer", "billwagn"),
            ("dev_langs", "csharp"),
            ("ms_subservice", "csharp-tour"),
        };
        var yamlBlock = string.Concat(yamlPairs.Select(p =>
            $"<div>{p.Item1}: {p.Item2}</div>"));

        var prose = string.Concat(Enumerable.Repeat(
            "C# is a modern, object-oriented programming language with substantial real prose content describing its features. ",
            8));

        // No <main>/<article>; the YAML and the article body are both
        // generic <div>s. Mirrors the Playwright-rendered MS Learn DOM
        // where the semantic anchors are wrapped in or replaced by
        // framework-generated wrappers.
        var html = "<html><body>" +
            $"<div id='page-metadata' class='ms-frontmatter'>{yamlBlock}</div>" +
            $"<div id='main-column' class='content-body'><h1>A tour of the C# language</h1><p>{prose}</p></div>" +
            "</body></html>";

        var blocks = Classify(html);

        var mainContent = blocks
            .Where(b => b.Role == BlockRole.MainContent || b.Role == BlockRole.Article)
            .ToList();
        mainContent.Should().NotBeEmpty();

        var combinedMainText = string.Concat(mainContent.Select(b => b.Text));
        combinedMainText.Should().Contain("object-oriented programming language",
            "the actual article body must be the MainContent");
        combinedMainText.Should().NotContain("ms_author: wiwagn",
            "YAML metadata must not be classified as MainContent");
        combinedMainText.Should().NotContain("breadcrumb_path",
            "config keys must not be classified as MainContent");
    }

    [Fact]
    public void Code_Block_Is_Not_Misclassified_As_Metadata()
    {
        // Regression guard: code blocks contain colons too (`Dictionary<int, string>`,
        // `if (x): y`, JSON, etc.). They must NOT be rejected as metadata.
        // The shape signal is "key:value line PAIRS dominate", not "any
        // line containing a colon".
        const string codeBlock =
            "public class Example {\n" +
            "    public int Id { get; set; }\n" +
            "    public string Name { get; set; }\n" +
            "    private Dictionary<string, int> _map;\n" +
            "    public void Process(IEnumerable<string> items) {\n" +
            "        foreach (var item in items) {\n" +
            "            _map[item] = ComputeHash(item);\n" +
            "        }\n" +
            "    }\n" +
            "}";

        var prose = string.Concat(Enumerable.Repeat(
            "This article shows how to write a C# class with property accessors and a generic dictionary. ",
            8));

        var html = "<html><body>" +
            "<main>" +
                "<h1>How to write a C# class</h1>" +
                $"<p>{prose}</p>" +
                $"<pre><code class='language-csharp'>{codeBlock}</code></pre>" +
            "</main>" +
            "</body></html>";

        var blocks = Classify(html);

        var combinedText = string.Concat(blocks.Select(b => b.Text));
        combinedText.Should().Contain("public class Example",
            "code blocks must NOT be rejected by the metadata-shape gate");
        combinedText.Should().Contain("property accessors",
            "the article body must remain in content");
    }
}