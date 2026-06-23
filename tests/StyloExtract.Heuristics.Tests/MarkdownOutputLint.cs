using FluentAssertions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Structural assertions on rendered markdown output. Parses the markdown with
/// Markdig (the same CommonMark engine real consumers use) and asserts:
///
///   * No line begins with four or more spaces unless it's inside a fenced
///     code block. This catches the lucidVIEW 1.7.1 regression class:
///     accumulated leading whitespace from indented source HTML triggering
///     CommonMark's indented-code-block rule and turning links into code text.
///   * Every link in the rendered output parses as an actual Link inline,
///     not as raw bracket text.
///   * Spurious fenced code blocks are not emitted (the count must match the
///     declared expectation; pass <c>expectedFenced</c> when the source HTML
///     legitimately contained <c>&lt;pre&gt;</c>/<c>&lt;code&gt;</c> blocks).
///
/// Surface-text <c>Contains</c> assertions miss every one of these: the test
/// passes because the literal "[link](url)" appears in the output, but the
/// parser sees an indented-code-block-with-bracket-text and the rendered
/// HTML never produces a clickable link. The lint helper closes that gap.
/// </summary>
internal static class MarkdownOutputLint
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static void AssertHealthy(string markdown, int expectedFenced = -1)
    {
        var doc = Markdown.Parse(markdown, Pipeline);

        var fenced = doc.Descendants<FencedCodeBlock>().Count();
        var indented = doc.Descendants<CodeBlock>().Count() - fenced;
        indented.Should().Be(0,
            because: "indented code blocks in the output are almost always the result of accumulated " +
                     "leading whitespace from source-HTML indentation triggering CommonMark's >=4-space rule. " +
                     "Walker output should never produce one; <pre>/<code> source produces fenced blocks. " +
                     "Markdown:\n" + Snip(markdown));

        if (expectedFenced >= 0)
        {
            fenced.Should().Be(expectedFenced,
                because: "the walker should emit exactly the fenced blocks the source HTML had");
        }
    }

    /// <summary>
    /// Asserts that every (href, text) pair the source intended survives as a
    /// real Link inline in the rendered AST. Use this when a test wants to
    /// prove a particular anchor didn't degrade to plain text.
    /// </summary>
    public static void AssertLinkSurvived(string markdown, string expectedHref, string expectedText)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        var links = doc.Descendants<LinkInline>().ToList();
        var match = links.FirstOrDefault(l =>
            l.Url == expectedHref &&
            l.FirstChild is LiteralInline lit &&
            lit.Content.ToString() == expectedText);
        match.Should().NotBeNull(
            because: $"link [{expectedText}]({expectedHref}) should survive as a real Markdig LinkInline. " +
                     $"Found links: {string.Join(", ", links.Select(l => $"[{((l.FirstChild as LiteralInline)?.Content.ToString() ?? "?")}]({l.Url})"))}");
    }

    /// <summary>
    /// Returns the count of LinkInline entries in the parsed AST. Useful for
    /// "every anchor in the source survived as a link" assertions.
    /// </summary>
    public static int CountLinks(string markdown)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        return doc.Descendants<LinkInline>().Count();
    }

    private static string Snip(string s) => s.Length > 400 ? s[..400] + "..." : s;
}
