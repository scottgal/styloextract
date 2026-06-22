using System;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Tests for framework-content-class-hints: CMS templates wrap the article body in
/// recognised classes (WordPress entry-content / post-content / wp-block-post-content,
/// Ghost gh-content / kg-content, Drupal field--name-body, Magento magento-content-area).
/// The classifier promotes these to MainContent with high confidence even when no
/// semantic <main>/<article> tag wraps them.
/// </summary>
public class FrameworkContentClassTests
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

    private static string Body() =>
        "<p>" + string.Join("</p><p>", Enumerable.Range(1, 5).Select(i =>
            $"Paragraph {i}. " + new string('x', 200))) + "</p>";

    [Fact]
    public void WordPress_EntryContent_ClassifiesAsMainContent()
    {
        var html = $"<html><body><header><nav>Top nav</nav></header>"
            + $"<div class=\"site-content\"><div class=\"entry-content\">{Body()}</div></div>"
            + $"<footer>Footer text</footer></body></html>";

        var blocks = Classify(html);

        bool hasMainContent = blocks.Any(b =>
            b.Role is BlockRole.MainContent or BlockRole.Article
            && b.Text.Contains("Paragraph 1"));
        hasMainContent.Should().BeTrue("entry-content is the canonical WordPress article wrapper");
    }

    [Fact]
    public void Gutenberg_WpBlockPostContent_ClassifiesAsMainContent()
    {
        var html = $"<html><body><header>Site head</header>"
            + $"<div class=\"wp-site-blocks\"><div class=\"wp-block-post-content\">{Body()}</div></div>"
            + $"<footer>Site foot</footer></body></html>";

        var blocks = Classify(html);

        bool hasMainContent = blocks.Any(b =>
            b.Role is BlockRole.MainContent or BlockRole.Article
            && b.Text.Contains("Paragraph 1"));
        hasMainContent.Should().BeTrue("wp-block-post-content is the Gutenberg block-theme wrapper");
    }

    [Fact]
    public void Ghost_GhContent_ClassifiesAsMainContent()
    {
        var html = $"<html><body><header>Ghost header</header>"
            + $"<div class=\"gh-canvas\"><div class=\"gh-content\">{Body()}</div></div></body></html>";

        var blocks = Classify(html);

        bool hasMainContent = blocks.Any(b =>
            b.Role is BlockRole.MainContent or BlockRole.Article
            && b.Text.Contains("Paragraph 1"));
        hasMainContent.Should().BeTrue("gh-content is the Ghost Casper-theme wrapper");
    }

    [Fact]
    public void Drupal_FieldNameBody_ClassifiesAsMainContent()
    {
        var html = $"<html><body><header>Drupal header</header>"
            + $"<div class=\"node\"><div class=\"field field--name-body field--type-text-with-summary\">{Body()}</div></div></body></html>";

        var blocks = Classify(html);

        bool hasMainContent = blocks.Any(b =>
            b.Role is BlockRole.MainContent or BlockRole.Article
            && b.Text.Contains("Paragraph 1"));
        hasMainContent.Should().BeTrue("field--name-body is the Drupal article body wrapper");
    }

    [Fact]
    public void LinkHeavyContentWrapper_DoesNotMatch()
    {
        // A wrapper using "main-content" class but consisting entirely of <a> links is
        // navigation, not article body. The classifier must NOT promote it to MainContent.
        var links = string.Join("", Enumerable.Range(1, 20).Select(i =>
            $"<a href='/x{i}'>Link {i} description text here for padding</a>"));
        var html = $"<html><body><div class=\"main-content\">{links}</div></body></html>";

        var blocks = Classify(html);

        bool hasMainContent = blocks.Any(b => b.Role == BlockRole.MainContent && b.TextLength > 100);
        hasMainContent.Should().BeFalse(
            "link-heavy 'main-content' wrappers are navigation; framework hint should not promote them");
    }
}
