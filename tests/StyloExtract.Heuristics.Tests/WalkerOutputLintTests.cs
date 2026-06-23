using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Structural lint over walker output, catching whole bug classes that
/// <c>Contains("[link](url)")</c> assertions miss. Each test renders one
/// representative shape of source HTML and runs the markdown through
/// <see cref="MarkdownOutputLint"/>, which parses with Markdig and asserts on
/// the CommonMark AST.
///
/// The 1.7.1 lucidVIEW regression (indented anchors becoming code blocks)
/// would have failed every test in this file at the AST level even though
/// the surface text contained <c>[Post title](/blog/post)</c>.
/// </summary>
public class WalkerOutputLintTests
{
    private static IElement Parse(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = ctx.OpenAsync(req => req.Content($"<html><body>{html}</body></html>"))
            .GetAwaiter().GetResult();
        return doc.Body!;
    }

    private static string Render(string html) => DomMarkdownWalker.Render(Parse(html));

    [Fact]
    public void Tailwind_Style_Indented_Card_Markup_Yields_Clean_Markdown()
    {
        // Real shape from lucidVIEW / mostlylucid.net: blog-card grid with
        // anchors behind two and three levels of indentation. Pre-1.7.1 every
        // card after the first parsed as an indented code block.
        var md = Render("""
            <div class="grid grid-cols-3 gap-4">
                <div class="card bg-white p-6 rounded-lg shadow">
                    <a href="/blog/post-a" class="text-blue-600">Post A</a>
                    <p>Summary A.</p>
                </div>
                <div class="card bg-white p-6 rounded-lg shadow">
                    <a href="/blog/post-b" class="text-blue-600">Post B</a>
                    <p>Summary B.</p>
                </div>
                <div class="card bg-white p-6 rounded-lg shadow">
                    <a href="/blog/post-c" class="text-blue-600">Post C</a>
                    <p>Summary C.</p>
                </div>
            </div>
            """);
        MarkdownOutputLint.AssertHealthy(md, expectedFenced: 0);
        MarkdownOutputLint.AssertLinkSurvived(md, "/blog/post-a", "Post A");
        MarkdownOutputLint.AssertLinkSurvived(md, "/blog/post-b", "Post B");
        MarkdownOutputLint.AssertLinkSurvived(md, "/blog/post-c", "Post C");
    }

    [Fact]
    public void HTMX_Hugo_Style_Article_With_Indented_Sections_Yields_Real_Headings_And_Links()
    {
        var md = Render("""
            <article class="prose mx-auto">
                <header>
                    <h1>Article title here</h1>
                    <p class="lead">
                        Lead paragraph with an inline
                        <a href="/about">about link</a> woven in.
                    </p>
                </header>
                <section>
                    <h2>First section</h2>
                    <p>
                        Body of first section with
                        <strong>important</strong> content and a
                        <a href="/refs/r1">reference link</a>.
                    </p>
                </section>
                <section>
                    <h2>Second section</h2>
                    <p>Body of second section.</p>
                </section>
            </article>
            """);
        MarkdownOutputLint.AssertHealthy(md, expectedFenced: 0);
        MarkdownOutputLint.AssertLinkSurvived(md, "/about", "about link");
        MarkdownOutputLint.AssertLinkSurvived(md, "/refs/r1", "reference link");
        MarkdownOutputLint.CountLinks(md).Should().Be(2);
    }

    [Fact]
    public void Indented_List_Of_Links_Survives_As_Markdown_List_With_Links()
    {
        var md = Render("""
            <nav class="my-4 space-y-2">
                <ul>
                    <li>
                        <a href="/docs/one">Docs page one</a>
                    </li>
                    <li>
                        <a href="/docs/two">Docs page two</a>
                    </li>
                    <li>
                        <a href="/docs/three">Docs page three</a>
                    </li>
                </ul>
            </nav>
            """);
        MarkdownOutputLint.AssertHealthy(md, expectedFenced: 0);
        MarkdownOutputLint.AssertLinkSurvived(md, "/docs/one", "Docs page one");
        MarkdownOutputLint.AssertLinkSurvived(md, "/docs/two", "Docs page two");
        MarkdownOutputLint.AssertLinkSurvived(md, "/docs/three", "Docs page three");
    }

    [Fact]
    public void Pre_Code_Source_Produces_One_Fenced_Block_And_Nothing_Else()
    {
        // Sanity: legitimate code blocks in source survive as exactly one
        // fenced block. No spurious indented-code-blocks from surrounding
        // indented prose.
        var md = Render("""
            <section>
                <p>
                    Example invocation:
                </p>
                <pre><code class="language-bash">curl https://example.com/</code></pre>
                <p>
                    Trailing prose paragraph.
                </p>
            </section>
            """);
        MarkdownOutputLint.AssertHealthy(md, expectedFenced: 1);
    }

    [Fact]
    public void GFM_Table_With_Indented_Surroundings_Has_No_Spurious_Code_Block()
    {
        var md = Render("""
            <article>
                <p>
                    Lookup table:
                </p>
                <table>
                    <thead>
                        <tr><th>Key</th><th>Value</th></tr>
                    </thead>
                    <tbody>
                        <tr><td>alpha</td><td>1</td></tr>
                        <tr><td>beta</td><td>2</td></tr>
                    </tbody>
                </table>
                <p>
                    Footnote paragraph.
                </p>
            </article>
            """);
        MarkdownOutputLint.AssertHealthy(md, expectedFenced: 0);
    }

    [Fact]
    public void Lint_Fails_When_Markdown_Has_Accumulated_Leading_Whitespace()
    {
        // Direct proof the harness has teeth. Hand-craft the exact shape the
        // pre-1.7.1 walker would have emitted from indented blog-card source HTML:
        // a blank line followed by a five-space-indented "link" that Markdig will
        // parse as CodeBlock + literal text, not as a Paragraph + LinkInline.
        const string broken =
            "Some prose paragraph that runs across one line.\n" +
            "\n" +
            "     [Post title](/blog/post-a)\n" +
            "\n" +
            "Trailing prose paragraph.\n";

        // Sanity: the bug presents in Markdig's AST as an indented-code-block.
        MarkdownOutputLint.CountLinks(broken).Should().Be(0,
            because: "indented to a code block, the bracket text is literal, not a link");

        var act = () => MarkdownOutputLint.AssertHealthy(broken, expectedFenced: 0);
        act.Should().Throw<Xunit.Sdk.XunitException>("the lint must reject markdown containing an indented code block");
    }

    [Fact]
    public void Empty_Inline_Siblings_With_Indented_Whitespace_Do_Not_Accumulate_Leading_Spaces()
    {
        // The exact reproducer for the 1.7.1 bug shape, confirmed by tracing the
        // mostlylucid.net markup pattern. EnsureBlankLine after the first <p> drops
        // the cursor at line-start. The second <p>'s inline children are four
        // text-nodes-of-whitespace ("\n    ") interleaved with empty <span>s.
        // Without the fix, each text node emits one collapsed leading space (because
        // local prevWs resets per AppendEscapedInline call), accumulating four
        // spaces before the anchor. CommonMark then parses the line as an indented
        // code block and "[Link](/url)" becomes literal bracket text instead of a
        // real link.
        //
        // With the fix, AppendEscapedInline primes prevWs=true when at line-start
        // and skips leading whitespace from each text node, leaving the anchor at
        // column zero where Markdig parses it as a real LinkInline.
        var md = Render("""
            <p>First paragraph anchors output so document-end Trim cannot mask the bug.</p>
            <p>
                <span></span>
                <span></span>
                <span></span>
                <a href="/blog/post-a">Post title here</a>
            </p>
            """);
        MarkdownOutputLint.AssertHealthy(md, expectedFenced: 0);
        MarkdownOutputLint.AssertLinkSurvived(md, "/blog/post-a", "Post title here");
    }

    [Fact]
    public void Deeply_Nested_Wrapper_Markup_Lints_Clean()
    {
        // The pattern from mostlylucid.net that triggered 1.7.1: 4+ levels of
        // Tailwind wrapper divs each contributing their own indentation. With
        // the fix in place, leading whitespace at line-start is dropped and the
        // anchor parses as a real link. Without the fix this lint trips.
        var md = Render("""
            <section class="py-12">
                <div class="container mx-auto px-6">
                    <div class="grid grid-cols-3 gap-6">
                        <div class="card bg-white p-6 rounded-lg shadow">
                            <a href="/blog/post-a" class="text-blue-600">Post A</a>
                            <div class="mt-2 text-gray-600">Summary A.</div>
                        </div>
                        <div class="card bg-white p-6 rounded-lg shadow">
                            <a href="/blog/post-b" class="text-blue-600">Post B</a>
                            <div class="mt-2 text-gray-600">Summary B.</div>
                        </div>
                    </div>
                </div>
            </section>
            """);
        MarkdownOutputLint.AssertHealthy(md, expectedFenced: 0);
        MarkdownOutputLint.AssertLinkSurvived(md, "/blog/post-a", "Post A");
        MarkdownOutputLint.AssertLinkSurvived(md, "/blog/post-b", "Post B");
    }

    [Fact]
    public void Realworld_Shape_Article_With_Inline_Code_And_Emphasis_Lints_Clean()
    {
        var md = Render("""
            <article>
                <h1>Configuration reference</h1>
                <p>
                    Register the service in <code>Program.cs</code> using
                    <strong>AddStyloExtract</strong> as shown in
                    <a href="/docs/quickstart">the quickstart</a>.
                </p>
                <h2>Options</h2>
                <p>
                    Two thresholds: <em>fast-path</em> and <em>slow-path</em>.
                </p>
            </article>
            """);
        MarkdownOutputLint.AssertHealthy(md, expectedFenced: 0);
        MarkdownOutputLint.AssertLinkSurvived(md, "/docs/quickstart", "the quickstart");
    }
}
