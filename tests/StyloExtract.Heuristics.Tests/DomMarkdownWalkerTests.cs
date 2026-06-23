using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class DomMarkdownWalkerTests
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
    public void Heading_Level_Is_Preserved_Per_Tag()
    {
        var md = Render("<h1>One</h1><h2>Two</h2><h3>Three</h3><h4>Four</h4><h5>Five</h5><h6>Six</h6>");
        md.Should().Contain("# One");
        md.Should().Contain("## Two");
        md.Should().Contain("### Three");
        md.Should().Contain("#### Four");
        md.Should().Contain("##### Five");
        md.Should().Contain("###### Six");
    }

    [Fact]
    public void Inline_Link_Is_Preserved()
    {
        Render("<p>See <a href=\"/docs\">the docs</a> for more.</p>")
            .Should().Contain("[the docs](/docs)");
    }

    [Fact]
    public void Strong_And_Em_Map_To_Bold_And_Italic()
    {
        var md = Render("<p>An <em>italic</em> and <strong>bold</strong> word.</p>");
        md.Should().Contain("*italic*");
        md.Should().Contain("**bold**");
    }

    [Fact]
    public void Inline_Code_Is_Backticked()
    {
        Render("<p>The <code>foo()</code> function.</p>")
            .Should().Contain("`foo()`");
    }

    [Fact]
    public void Inline_Image_Becomes_Markdown_Image()
    {
        Render("<p>See <img src=\"/cat.png\" alt=\"cat\"> here.</p>")
            .Should().Contain("![cat](/cat.png)");
    }

    [Fact]
    public void Unordered_List_Renders_As_Bullets()
    {
        var md = Render("<ul><li>first</li><li>second</li></ul>");
        md.Should().Contain("- first");
        md.Should().Contain("- second");
    }

    [Fact]
    public void Ordered_List_Renders_With_Numbers()
    {
        var md = Render("<ol><li>one</li><li>two</li><li>three</li></ol>");
        md.Should().Contain("1. one");
        md.Should().Contain("2. two");
        md.Should().Contain("3. three");
    }

    [Fact]
    public void Fenced_Code_Block_Has_Language_Hint()
    {
        var md = Render("<pre><code class=\"language-csharp\">var x = 1;</code></pre>");
        md.Should().Contain("```csharp");
        md.Should().Contain("var x = 1;");
        md.Should().Contain("```\n");
    }

    [Fact]
    public void Fenced_Code_Block_Without_Language_Still_Fences()
    {
        var md = Render("<pre>raw code</pre>");
        md.Should().Contain("```\n");
        md.Should().Contain("raw code");
    }

    [Fact]
    public void Blockquote_Prefixes_Each_Line()
    {
        Render("<blockquote><p>quoted</p></blockquote>")
            .Should().Contain("> quoted");
    }

    [Fact]
    public void Hr_Becomes_Horizontal_Rule()
    {
        Render("<p>before</p><hr><p>after</p>")
            .Should().Contain("---");
    }

    [Fact]
    public void Simple_Table_Renders_GFM_Pipes()
    {
        var md = Render("""
            <table>
              <thead><tr><th>Name</th><th>Age</th></tr></thead>
              <tbody>
                <tr><td>Ada</td><td>36</td></tr>
                <tr><td>Bob</td><td>40</td></tr>
              </tbody>
            </table>
            """);
        md.Should().Contain("| Name | Age |");
        md.Should().Contain("| --- | --- |");
        md.Should().Contain("| Ada | 36 |");
        md.Should().Contain("| Bob | 40 |");
    }

    [Fact]
    public void Table_With_Caption_Emits_Bold_Caption_Above()
    {
        var md = Render("""
            <table>
              <caption>Crew manifest</caption>
              <thead><tr><th>Name</th></tr></thead>
              <tbody><tr><td>Ada</td></tr></tbody>
            </table>
            """);
        var captionIdx = md.IndexOf("**Crew manifest**");
        var headerIdx = md.IndexOf("| Name |");
        captionIdx.Should().BeGreaterThan(-1);
        headerIdx.Should().BeGreaterThan(captionIdx);
    }

    [Fact]
    public void Table_With_Colspan_Repeats_Content_Across_Spanned_Columns()
    {
        var md = Render("""
            <table>
              <thead><tr><th>A</th><th>B</th><th>C</th></tr></thead>
              <tbody>
                <tr><td colspan="2">merged</td><td>x</td></tr>
                <tr><td>1</td><td>2</td><td>3</td></tr>
              </tbody>
            </table>
            """);
        md.Should().Contain("| merged | merged | x |");
        md.Should().Contain("| 1 | 2 | 3 |");
    }

    [Fact]
    public void Table_With_Rowspan_Anchors_Content_In_First_Row_And_Blanks_Below()
    {
        var md = Render("""
            <table>
              <thead><tr><th>Group</th><th>Name</th></tr></thead>
              <tbody>
                <tr><td rowspan="2">Team A</td><td>Ada</td></tr>
                <tr><td>Bob</td></tr>
              </tbody>
            </table>
            """);
        // First row keeps the rowspan content; second row's first column is blank,
        // and the explicit cell for "Bob" lands in the second column.
        md.Should().Contain("| Team A | Ada |");
        md.Should().Contain("|  | Bob |");
    }

    [Fact]
    public void Table_With_Th_First_Row_Without_Thead_Treats_It_As_Header()
    {
        var md = Render("""
            <table>
              <tr><th>A</th><th>B</th></tr>
              <tr><td>1</td><td>2</td></tr>
            </table>
            """);
        md.Should().Contain("| A | B |");
        md.Should().Contain("| --- | --- |");
        md.Should().Contain("| 1 | 2 |");
    }

    [Fact]
    public void Table_With_Cell_Containing_Block_Content_Falls_Back_To_Raw_Html()
    {
        var md = Render("""
            <table>
              <thead><tr><th>A</th></tr></thead>
              <tbody>
                <tr><td><ul><li>x</li><li>y</li></ul></td></tr>
              </tbody>
            </table>
            """);
        // Block content in a cell → raw HTML fallback.
        md.Should().Contain("<table");
        md.Should().Contain("<li>x</li>");
        md.Should().NotContain("| --- |");
    }

    [Fact]
    public void Table_With_Nested_Table_Falls_Back_To_Raw_Html()
    {
        var md = Render("""
            <table>
              <thead><tr><th>A</th></tr></thead>
              <tbody>
                <tr><td><table><tr><td>nested</td></tr></table></td></tr>
              </tbody>
            </table>
            """);
        md.Should().Contain("<table");
        md.Should().Contain("nested");
        md.Should().NotContain("| A |\n| --- |");
    }

    [Fact]
    public void Table_With_Multi_Row_Thead_Falls_Back_To_Raw_Html()
    {
        var md = Render("""
            <table>
              <thead>
                <tr><th>Group</th><th>Sub</th></tr>
                <tr><th>g</th><th>s</th></tr>
              </thead>
              <tbody><tr><td>1</td><td>2</td></tr></tbody>
            </table>
            """);
        md.Should().Contain("<table");
        md.Should().NotContain("| --- |");
    }

    [Fact]
    public void Table_Alignment_Reads_Align_Attribute_With_Column_Consensus()
    {
        var md = Render("""
            <table>
              <thead><tr><th>L</th><th>C</th><th>R</th></tr></thead>
              <tbody>
                <tr><td align="left">a</td><td align="center">b</td><td align="right">c</td></tr>
                <tr><td align="left">d</td><td align="center">e</td><td align="right">f</td></tr>
              </tbody>
            </table>
            """);
        md.Should().Contain("| :--- | :---: | ---: |");
    }

    [Fact]
    public void Table_Cell_With_Pipe_Character_Gets_Escaped()
    {
        var md = Render("""
            <table>
              <thead><tr><th>k</th></tr></thead>
              <tbody><tr><td>a | b</td></tr></tbody>
            </table>
            """);
        md.Should().Contain(@"| a \| b |");
    }

    [Fact]
    public void Figure_With_Image_And_Caption_Renders_Image_And_Caption_Text()
    {
        var md = Render("""
            <figure>
              <img src="/hero.jpg" alt="hero image">
              <figcaption>The hero shot</figcaption>
            </figure>
            """);
        md.Should().Contain("![hero image](/hero.jpg)");
        md.Should().Contain("The hero shot");
    }

    [Fact]
    public void Scripts_And_Styles_Are_Dropped()
    {
        Render("<p>keep me</p><script>alert(1)</script><style>p{}</style>")
            .Should().NotContain("alert");
    }

    // ----- Inline composition edge cases -----

    [Fact]
    public void Strong_Inside_Em_Nests_Cleanly()
    {
        Render("<p><em>soft <strong>and loud</strong> together</em></p>")
            .Should().Contain("*soft **and loud** together*");
    }

    [Fact]
    public void Link_With_Strong_Children_Keeps_Bold_Inside_Brackets()
    {
        Render("<p>see <a href=\"/x\"><strong>BIG</strong> link</a> here</p>")
            .Should().Contain("[**BIG** link](/x)");
    }

    [Fact]
    public void Image_Inside_Link_Renders_Image_Then_Wraps_In_Link()
    {
        Render("<p><a href=\"/post\"><img src=\"/cover.jpg\" alt=\"cover\"></a></p>")
            .Should().Contain("[![cover](/cover.jpg)](/post)");
    }

    [Fact]
    public void Anchor_With_No_Href_Falls_Through_To_Plain_Text()
    {
        var md = Render("<p>hello <a>plain</a> world</p>");
        md.Should().Contain("plain");
        md.Should().NotContain("[plain]");
    }

    [Fact]
    public void Image_With_No_Src_Is_Dropped()
    {
        Render("<p>before <img alt=\"orphan\"> after</p>")
            .Should().NotContain("orphan");
    }

    [Fact]
    public void Image_With_Empty_Alt_Still_Renders_With_Empty_Brackets()
    {
        Render("<p><img src=\"/x.png\"></p>")
            .Should().Contain("![](/x.png)");
    }

    [Fact]
    public void Br_Inside_Paragraph_Becomes_Hard_Line_Break()
    {
        Render("<p>line one<br>line two</p>")
            .Should().Contain("line one  \nline two");
    }

    [Fact]
    public void Inline_Code_Preserves_Special_Characters_Verbatim()
    {
        Render("<p>Look at <code>a*b_c\\d</code>.</p>")
            .Should().Contain("`a*b_c\\d`");
    }

    [Fact]
    public void Heading_With_Inline_Link_Keeps_Link_Inside_Heading()
    {
        Render("<h2>See <a href=\"/x\">here</a></h2>")
            .Should().Contain("## See [here](/x)");
    }

    [Fact]
    public void Heading_With_Inline_Code_Renders_Backticks_Inside_Heading()
    {
        Render("<h3>The <code>foo()</code> function</h3>")
            .Should().Contain("### The `foo()` function");
    }

    [Fact]
    public void Span_Sub_Sup_Mark_Kbd_Pass_Through_As_Plain_Text()
    {
        var md = Render("<p>x<sub>2</sub> + y<sup>3</sup> = <kbd>Enter</kbd> <mark>hit</mark> <span>span</span></p>");
        md.Should().Contain("x2 + y3 = Enter hit span");
    }

    [Fact]
    public void Whitespace_In_Text_Is_Collapsed_To_Single_Spaces()
    {
        Render("<p>one  \n  two\t\tthree</p>")
            .Should().Contain("one two three");
    }

    [Fact]
    public void Empty_Element_Returns_Just_A_Newline()
    {
        DomMarkdownWalker.Render(Parse("")).Should().Be("\n");
    }

    [Fact]
    public void Two_Paragraphs_Are_Separated_By_Blank_Line()
    {
        var md = Render("<p>first paragraph</p><p>second paragraph</p>");
        md.Should().Contain("first paragraph\n\nsecond paragraph");
    }

    [Fact]
    public void Heading_Then_Paragraph_Are_Separated_By_Blank_Line()
    {
        var md = Render("<h1>Title</h1><p>body text</p>");
        md.Should().Contain("# Title\n\nbody text");
    }

    // ----- List edge cases -----

    [Fact]
    public void List_Items_With_Inline_Formatting_Survive()
    {
        var md = Render("<ul><li>plain item</li><li>item with <strong>bold</strong></li><li><a href=\"/x\">link item</a></li></ul>");
        md.Should().Contain("- plain item");
        md.Should().Contain("- item with **bold**");
        md.Should().Contain("- [link item](/x)");
    }

    [Fact]
    public void Ordered_List_Numbers_Are_Sequential_Even_With_Embedded_Text_Nodes()
    {
        var md = Render("<ol><li>a</li>\n<li>b</li>\n<li>c</li></ol>");
        md.Should().Contain("1. a");
        md.Should().Contain("2. b");
        md.Should().Contain("3. c");
    }

    [Fact]
    public void Nested_List_Flattens_To_Single_Line_Items_Today()
    {
        // Known limitation per the walker's design: nested lists collapse into the
        // parent item's line. Asserting current behaviour so a future regression to
        // a worse rendering is caught, and the explicit intent stays documented.
        var md = Render("<ul><li>outer<ul><li>inner</li></ul></li></ul>");
        md.Should().Contain("- outer");
        md.Should().Contain("inner");
    }

    // ----- Code block edge cases -----

    [Fact]
    public void Pre_Without_Inner_Code_Element_Still_Fences()
    {
        Render("<pre>raw block\nline two\n</pre>")
            .Should().Contain("```\nraw block\nline two\n```");
    }

    [Fact]
    public void Pre_With_Multiple_Language_Class_Tokens_Picks_The_Language_One()
    {
        Render("<pre><code class=\"hljs language-python copyable\">x = 1</code></pre>")
            .Should().Contain("```python\nx = 1\n```");
    }

    // ----- Table edge cases -----

    [Fact]
    public void Table_Alignment_From_Style_Attribute_Is_Detected()
    {
        var md = Render("""
            <table>
              <thead><tr><th>A</th><th>B</th></tr></thead>
              <tbody>
                <tr><td style="text-align:right">1</td><td style="text-align: center">2</td></tr>
                <tr><td style="text-align:right">3</td><td style="text-align: center">4</td></tr>
              </tbody>
            </table>
            """);
        md.Should().Contain("| ---: | :---: |");
    }

    [Fact]
    public void Table_Alignment_With_Tied_Vote_Returns_Default()
    {
        var md = Render("""
            <table>
              <thead><tr><th>A</th></tr></thead>
              <tbody>
                <tr><td align="left">1</td></tr>
                <tr><td align="right">2</td></tr>
              </tbody>
            </table>
            """);
        md.Should().Contain("| --- |");
        md.Should().NotContain(":---");
    }

    [Fact]
    public void Table_With_Direct_Tr_Children_Without_Tbody_Is_Parsed()
    {
        var md = Render("""
            <table>
              <thead><tr><th>A</th></tr></thead>
              <tr><td>1</td></tr>
              <tr><td>2</td></tr>
            </table>
            """);
        md.Should().Contain("| A |");
        md.Should().Contain("| 1 |");
        md.Should().Contain("| 2 |");
    }

    [Fact]
    public void Table_With_No_Header_Treats_First_Body_Row_As_Header()
    {
        // Fall-through case when there's no <thead> and the first row has no all-<th>.
        var md = Render("""
            <table>
              <tbody>
                <tr><td>A</td><td>B</td></tr>
                <tr><td>1</td><td>2</td></tr>
              </tbody>
            </table>
            """);
        md.Should().Contain("| A | B |");
        md.Should().Contain("| --- | --- |");
        md.Should().Contain("| 1 | 2 |");
    }

    [Fact]
    public void Table_With_Newline_In_Cell_Renders_As_Br()
    {
        // Walker only puts <br> in a cell when an inline <br> existed in the source.
        var md = Render("""
            <table>
              <thead><tr><th>A</th></tr></thead>
              <tbody><tr><td>line one<br>line two</td></tr></tbody>
            </table>
            """);
        md.Should().Contain("| line one<br>line two |");
    }

    [Fact]
    public void Table_With_Empty_Cells_Renders_Empty_Slots_Between_Pipes()
    {
        var md = Render("""
            <table>
              <thead><tr><th>A</th><th>B</th></tr></thead>
              <tbody>
                <tr><td></td><td>x</td></tr>
                <tr><td>y</td><td></td></tr>
              </tbody>
            </table>
            """);
        md.Should().Contain("|  | x |");
        md.Should().Contain("| y |  |");
    }

    [Fact]
    public void Table_With_Caption_Containing_Inline_Markup_Strips_To_Bold_Text()
    {
        // Caption renders via TextContent.Trim() per Joplin convention. Inline markup
        // in captions is rare; a future enhancement would walk inline children.
        var md = Render("""
            <table>
              <caption>Manifest <em>v2</em></caption>
              <thead><tr><th>A</th></tr></thead>
              <tbody><tr><td>1</td></tr></tbody>
            </table>
            """);
        md.Should().Contain("**Manifest v2**");
    }

    [Fact]
    public void Table_Rowspan_Greater_Than_Remaining_Rows_Does_Not_Throw()
    {
        var md = Render("""
            <table>
              <thead><tr><th>A</th><th>B</th></tr></thead>
              <tbody>
                <tr><td rowspan="99">anchor</td><td>x</td></tr>
                <tr><td>y</td></tr>
              </tbody>
            </table>
            """);
        md.Should().Contain("| anchor | x |");
        md.Should().Contain("|  | y |");
    }
}
