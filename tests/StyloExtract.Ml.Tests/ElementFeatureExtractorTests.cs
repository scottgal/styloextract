using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Ml.Features;
using Xunit;

namespace StyloExtract.Ml.Tests;

public class ElementFeatureExtractorTests
{
    private static IElement First(string html, string selector)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        // Synchronous parse via in-memory content fetch; AngleSharp resolves
        // immediately so awaiting isn't required, but the analyzer wants the
        // Wait to be explicit instead of inlined into a property chain.
        var task = ctx.OpenAsync(req => req.Content($"<html><body>{html}</body></html>"));
        task.Wait();
        return task.Result.QuerySelector(selector)!;
    }

    private static float[] Extract(IElement el)
    {
        var ex = new ElementFeatureExtractor();
        var buf = new float[FeatureNames.Dim];
        ex.Extract(el, buf);
        return buf;
    }

    [Fact]
    public void Dim_Matches_Names_Array()
    {
        FeatureNames.Names.Length.Should().Be(FeatureNames.Dim);
    }

    [Fact]
    public void Tag_OneHot_Fires_For_Main()
    {
        var el = First("<main><p>x</p></main>", "main");
        var f = Extract(el);
        f[FeatureNames.TagMain].Should().Be(1f);
        f[FeatureNames.TagArticle].Should().Be(0f);
        f[FeatureNames.TagOther].Should().Be(0f);
    }

    [Fact]
    public void Tag_OneHot_Falls_Through_To_Other_For_Unknown_Tags()
    {
        var el = First("<custom-widget>x</custom-widget>", "custom-widget");
        var f = Extract(el);
        f[FeatureNames.TagOther].Should().Be(1f);
        f[FeatureNames.TagDiv].Should().Be(0f);
    }

    [Fact]
    public void Class_Hash_Buckets_Fire_Deterministically_And_Case_Insensitively()
    {
        var a = Extract(First("<div class=\"button\">x</div>", "div"));
        var b = Extract(First("<div class=\"BUTTON\">x</div>", "div"));
        var c = Extract(First("<div class=\"Button\">x</div>", "div"));
        // Same token, three case variants -> same bucket fires for all three.
        for (int i = 0; i < 8; i++)
        {
            a[FeatureNames.ClassBucket0 + i].Should().Be(b[FeatureNames.ClassBucket0 + i]);
            b[FeatureNames.ClassBucket0 + i].Should().Be(c[FeatureNames.ClassBucket0 + i]);
        }
        // Exactly one bucket fired.
        var fired = Enumerable.Range(0, 8).Count(i => a[FeatureNames.ClassBucket0 + i] > 0);
        fired.Should().Be(1);
    }

    [Fact]
    public void Empty_Class_Attribute_Fires_No_Buckets()
    {
        var f = Extract(First("<div>x</div>", "div"));
        for (int i = 0; i < 8; i++)
            f[FeatureNames.ClassBucket0 + i].Should().Be(0f);
    }

    [Fact]
    public void Multiple_Class_Tokens_Can_Fire_Multiple_Buckets()
    {
        var f = Extract(First("<div class=\"foo bar baz qux quux corge\">x</div>", "div"));
        var fired = Enumerable.Range(0, 8).Count(i => f[FeatureNames.ClassBucket0 + i] > 0);
        fired.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Text_Length_And_Word_Count_Use_Log1p()
    {
        var el = First("<div>hello world this is a test</div>", "div");
        var f = Extract(el);
        // "hello world this is a test" = 26 chars, 6 words.
        f[FeatureNames.LogTextLength].Should().BeApproximately((float)Math.Log(1 + 26), 1e-4f);
        f[FeatureNames.LogWordCount].Should().BeApproximately((float)Math.Log(1 + 6), 1e-4f);
    }

    [Fact]
    public void Empty_Element_Has_Zero_Text_Features()
    {
        var f = Extract(First("<div></div>", "div"));
        f[FeatureNames.LogTextLength].Should().Be(0f);
        f[FeatureNames.LogWordCount].Should().Be(0f);
        f[FeatureNames.LinkDensity].Should().Be(0f);
    }

    [Fact]
    public void Link_Density_Computes_Correctly()
    {
        // TextContent = "hello worldfive!" = 16 chars; link text = "worldfive!" = 10
        // chars; link density = 10/16 = 0.625.
        var el = First("<div>hello <a href=\"/\">world</a><a href=\"/\">five!</a></div>", "div");
        var f = Extract(el);
        f[FeatureNames.LinkDensity].Should().BeApproximately(0.625f, 0.01f);
    }

    [Fact]
    public void Heading_And_Paragraph_Counts_Are_Log_Scaled()
    {
        var html = "<div>" +
                   "<h1>a</h1><h2>b</h2><h3>c</h3>" +
                   "<p>p1</p><p>p2</p><p>p3</p><p>p4</p>" +
                   "</div>";
        var f = Extract(First(html, "div"));
        f[FeatureNames.LogHeadingCount].Should().BeApproximately((float)Math.Log(1 + 3), 1e-4f);
        f[FeatureNames.LogParagraphCount].Should().BeApproximately((float)Math.Log(1 + 4), 1e-4f);
    }

    [Fact]
    public void Para_To_Heading_Ratio_Handles_Zero_Headings_Gracefully()
    {
        var f = Extract(First("<div><p>a</p><p>b</p></div>", "div"));
        f[FeatureNames.ParaToHeadingRatio].Should().Be(2f);
    }

    [Fact]
    public void Depth_Reflects_Walk_To_Root()
    {
        var html = "<section><div><article><h1 id=\"t\">x</h1></article></div></section>";
        var h = First(html, "#t");
        var f = new float[FeatureNames.Dim];
        new ElementFeatureExtractor().Extract(h, f);
        // h1 -> article -> div -> section -> body -> html. Depth = 5.
        f[FeatureNames.Depth].Should().Be(5f);
    }

    [Fact]
    public void Position_From_Start_And_End_Are_Normalised()
    {
        // 5 siblings, candidate is the 3rd (index 2).
        var html = "<ul><li>a</li><li>b</li><li class=\"target\">c</li><li>d</li><li>e</li></ul>";
        var el = First(html, ".target");
        var f = Extract(el);
        f[FeatureNames.PositionFromStart].Should().BeApproximately(0.5f, 1e-4f);
        f[FeatureNames.PositionFromEnd].Should().BeApproximately(0.5f, 1e-4f);
        f[FeatureNames.ParentChildCount].Should().Be(5f);
    }

    [Fact]
    public void Repeated_Sibling_Count_Identifies_Item_In_Homogeneous_Run()
    {
        var html = "<ul><li>a</li><li>b</li><li class=\"target\">c</li><li>d</li></ul>";
        var f = Extract(First(html, ".target"));
        // 3 other <li>s.
        f[FeatureNames.RepeatedSiblingCount].Should().Be(3f);
        f[FeatureNames.RepeatedShapeScore].Should().Be(1f);
    }

    [Fact]
    public void Sibling_Tag_Entropy_Is_Zero_For_Homogeneous_Siblings()
    {
        var html = "<ul><li>a</li><li>b</li><li class=\"target\">c</li><li>d</li></ul>";
        var f = Extract(First(html, ".target"));
        f[FeatureNames.SiblingTagEntropy].Should().Be(0f);
    }

    [Fact]
    public void Sibling_Tag_Entropy_Is_Nonzero_For_Heterogeneous_Siblings()
    {
        var html = "<section><h1>t</h1><p class=\"target\">x</p><ul><li>y</li></ul></section>";
        var f = Extract(First(html, ".target"));
        f[FeatureNames.SiblingTagEntropy].Should().BeGreaterThan(0f);
    }

    [Fact]
    public void Ancestor_Main_Fires_Only_When_Main_Is_An_Ancestor()
    {
        var inMain = Extract(First("<main><div><p class=\"t\">x</p></div></main>", ".t"));
        var inDiv  = Extract(First("<div><div><p class=\"t\">x</p></div></div>", ".t"));
        inMain[FeatureNames.AncestorMain].Should().Be(1f);
        inDiv[FeatureNames.AncestorMain].Should().Be(0f);
    }

    [Fact]
    public void Ancestor_Form_Fires_For_Inputs_Inside_Forms()
    {
        var f = Extract(First("<form><input id=\"t\"/></form>", "#t"));
        f[FeatureNames.AncestorForm].Should().Be(1f);
    }

    [Fact]
    public void Extract_Throws_When_Dest_Buffer_Is_Too_Small()
    {
        var el = First("<div>x</div>", "div");
        var dest = new float[FeatureNames.Dim - 1];
        var act = () => new ElementFeatureExtractor().Extract(el, dest);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Extract_Clears_The_Destination_So_Reuse_Is_Safe()
    {
        var dest = new float[FeatureNames.Dim];
        Array.Fill(dest, 99f);
        new ElementFeatureExtractor().Extract(First("<div>x</div>", "div"), dest);
        // Tag-other fires (1.0); rest of vector includes some text values but
        // the slots NOT written by this element should be back to 0, not 99.
        dest[FeatureNames.TagMain].Should().Be(0f);
        dest[FeatureNames.AncestorMain].Should().Be(0f);
    }
}
