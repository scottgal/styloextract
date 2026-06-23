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
/// Spec-driven assertions for the markdown-formatting gaps in
/// <c>docs/styloextract-markdown-spec.md</c>: heading levels, inline links,
/// emphasis, inline code, lists, GFM tables. Before
/// <c>DomMarkdownWalker</c> populated <c>ExtractedBlock.Markdown</c>, all
/// inline structure inside MainContent collapsed to plain text and the
/// integration output was a wall of paragraphs.
/// </summary>
public class MarkdownStructurePreservedTests
{
    private static (ILayoutExtractor, SqliteConnection) Build()
        => LayoutExtractorTestBuilder.Build();

    [Fact]
    public async Task Heading_Levels_Are_Preserved_In_MainContent()
    {
        var (e, conn) = Build();
        try
        {
            const string html = """
                <!DOCTYPE html>
                <html><head><title>Test</title></head>
                <body>
                  <main>
                    <article>
                      <h1>Top heading</h1>
                      <p>Lead paragraph providing enough textual mass to clear the classifier's content thresholds without any tricks.</p>
                      <h2>Subhead</h2>
                      <p>Body paragraph after the subhead, again with enough text to stay above the quality gate inside the renderer.</p>
                      <h3>Deeper subhead</h3>
                      <p>Final paragraph rounding out the article so the heuristic classifier emits the article block rather than discarding it as boilerplate.</p>
                    </article>
                  </main>
                </body></html>
                """;
            var result = await e.ExtractAsync(html, new Uri("https://example.com/post"));
            result.Markdown.Should().Contain("# Top heading");
            result.Markdown.Should().Contain("## Subhead");
            result.Markdown.Should().Contain("### Deeper subhead");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task Inline_Links_Emphasis_And_Code_Survive_Through_The_Pipeline()
    {
        var (e, conn) = Build();
        try
        {
            const string html = """
                <!DOCTYPE html>
                <html><head><title>Test</title></head>
                <body>
                  <main>
                    <article>
                      <h1>Inline things</h1>
                      <p>See <a href="https://example.com/docs">the docs</a> and use <code>foo()</code>. This sentence is <strong>important</strong> and slightly <em>nuanced</em> to keep the body length comfortable for the classifier gate.</p>
                      <p>One more paragraph so the article body sits above the threshold the renderer enforces for emit.</p>
                    </article>
                  </main>
                </body></html>
                """;
            var result = await e.ExtractAsync(html, new Uri("https://example.com/post"));
            result.Markdown.Should().Contain("[the docs](https://example.com/docs)");
            result.Markdown.Should().Contain("`foo()`");
            result.Markdown.Should().Contain("**important**");
            result.Markdown.Should().Contain("*nuanced*");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task Unordered_Lists_And_Fenced_Code_Survive_The_Pipeline()
    {
        var (e, conn) = Build();
        try
        {
            const string html = """
                <!DOCTYPE html>
                <html><head><title>Test</title></head>
                <body>
                  <main>
                    <article>
                      <h1>Lists and code</h1>
                      <p>Intro paragraph wide enough to clear the textual-mass gate the renderer uses to filter low-signal blocks.</p>
                      <ul>
                        <li>first item with some text</li>
                        <li>second item with more text</li>
                        <li>third item rounding it out</li>
                      </ul>
                      <pre><code class="language-csharp">var sample = "code";</code></pre>
                      <p>Outro paragraph to keep the overall article past the classifier threshold for emission.</p>
                    </article>
                  </main>
                </body></html>
                """;
            var result = await e.ExtractAsync(html, new Uri("https://example.com/post"));
            result.Markdown.Should().Contain("- first item");
            result.Markdown.Should().Contain("- second item");
            result.Markdown.Should().Contain("```csharp");
            result.Markdown.Should().Contain("var sample = \"code\";");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task Simple_Table_Renders_As_GFM_Pipes()
    {
        var (e, conn) = Build();
        try
        {
            const string html = """
                <!DOCTYPE html>
                <html><head><title>Test</title></head>
                <body>
                  <main>
                    <article>
                      <h1>Table test</h1>
                      <p>Intro paragraph providing enough textual mass to keep the article past the classifier's content threshold for emission.</p>
                      <table>
                        <thead><tr><th>Name</th><th>Age</th></tr></thead>
                        <tbody>
                          <tr><td>Ada</td><td>36</td></tr>
                          <tr><td>Bob</td><td>40</td></tr>
                        </tbody>
                      </table>
                      <p>Outro paragraph again to keep the article comfortably above the textual gate.</p>
                    </article>
                  </main>
                </body></html>
                """;
            var result = await e.ExtractAsync(html, new Uri("https://example.com/post"));
            result.Markdown.Should().Contain("| Name | Age |");
            result.Markdown.Should().Contain("| --- | --- |");
            result.Markdown.Should().Contain("| Ada | 36 |");
        }
        finally { conn.Dispose(); }
    }
}
