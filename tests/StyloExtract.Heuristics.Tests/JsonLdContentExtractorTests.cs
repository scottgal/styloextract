using AngleSharp.Html.Parser;
using FluentAssertions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Unit tests for <see cref="JsonLdContentExtractor"/>. Each test exercises one or more
/// schema.org types via an in-memory AngleSharp document so we do not need a real URL.
/// </summary>
public sealed class JsonLdContentExtractorTests
{
    private static AngleSharp.Dom.IDocument Parse(string ldJson)
    {
        var html = $"""
            <html><head>
              <script type="application/ld+json">{ldJson}</script>
            </head><body></body></html>
            """;
        var parser = new HtmlParser();
        return parser.ParseDocument(html);
    }

    private static AngleSharp.Dom.IDocument ParseNoScript()
    {
        return new HtmlParser().ParseDocument("<html><body><p>no ld</p></body></html>");
    }

    // ---------------------------------------------------------------------------
    // Null-return guards
    // ---------------------------------------------------------------------------

    [Fact]
    public void Returns_null_when_no_ld_json_script_present()
    {
        var doc = ParseNoScript();
        JsonLdContentExtractor.ExtractMainContent(doc).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_unrecognised_type_with_no_content_fields()
    {
        var doc = Parse("""{"@type":"Product","name":"Widget","price":9.99}""");
        // Product is explicitly skipped.
        JsonLdContentExtractor.ExtractMainContent(doc).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_json_is_malformed()
    {
        var doc = Parse("{not valid json}}");
        JsonLdContentExtractor.ExtractMainContent(doc).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_all_text_fields_empty()
    {
        var doc = Parse("""{"@type":"Article","name":"","headline":"","articleBody":""}""");
        JsonLdContentExtractor.ExtractMainContent(doc).Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // Article / BlogPosting / NewsArticle / TechArticle / Report
    // ---------------------------------------------------------------------------

    [Fact]
    public void Article_extracts_articleBody()
    {
        var doc = Parse("""
            {
              "@type": "Article",
              "headline": "The Headline",
              "articleBody": "This is the full article body text."
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("The Headline");
        result.Should().Contain("This is the full article body text.");
    }

    [Fact]
    public void BlogPosting_extracts_articleBody_and_description()
    {
        var doc = Parse("""
            {
              "@type": "BlogPosting",
              "name": "My Blog Post",
              "description": "A short description.",
              "articleBody": "Full post body text goes here."
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("My Blog Post");
        result.Should().Contain("Full post body text goes here.");
    }

    [Fact]
    public void NewsArticle_extracts_headline_and_articleBody()
    {
        var doc = Parse("""
            {
              "@type": "NewsArticle",
              "headline": "Breaking News",
              "articleBody": "Detailed news content here."
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Breaking News");
        result.Should().Contain("Detailed news content here.");
    }

    [Fact]
    public void TechArticle_extracts_articleBody()
    {
        var doc = Parse("""
            {"@type":"TechArticle","headline":"Tech Guide","articleBody":"Technical content about the subject."}
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Technical content about the subject.");
    }

    [Fact]
    public void Report_extracts_articleBody()
    {
        var doc = Parse("""
            {"@type":"Report","name":"Annual Report","articleBody":"Report body with findings."}
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Report body with findings.");
    }

    // ---------------------------------------------------------------------------
    // QAPage / Question / Answer
    // ---------------------------------------------------------------------------

    [Fact]
    public void QAPage_extracts_mainEntity_question_and_answers()
    {
        var doc = Parse("""
            {
              "@type": "QAPage",
              "name": "How do I do X?",
              "mainEntity": {
                "@type": "Question",
                "name": "How do I do X?",
                "text": "I need to do X but cannot figure out how.",
                "acceptedAnswer": {
                  "@type": "Answer",
                  "text": "You do X by following these steps."
                }
              }
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("How do I do X?");
        result.Should().Contain("I need to do X but cannot figure out how.");
        result.Should().Contain("You do X by following these steps.");
    }

    [Fact]
    public void Question_with_suggestedAnswer_extracts_text()
    {
        var doc = Parse("""
            {
              "@type": "Question",
              "text": "What is the best approach?",
              "suggestedAnswer": [
                {"@type": "Answer", "text": "Approach A is best."},
                {"@type": "Answer", "text": "Approach B works too."}
              ]
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Approach A is best.");
        result.Should().Contain("Approach B works too.");
    }

    // ---------------------------------------------------------------------------
    // FAQPage
    // ---------------------------------------------------------------------------

    [Fact]
    public void FAQPage_extracts_questions_and_answers()
    {
        var doc = Parse("""
            {
              "@type": "FAQPage",
              "name": "Frequently Asked Questions",
              "mainEntity": [
                {
                  "@type": "Question",
                  "name": "What is X?",
                  "acceptedAnswer": {"@type": "Answer", "text": "X is a thing."}
                },
                {
                  "@type": "Question",
                  "name": "What is Y?",
                  "acceptedAnswer": {"@type": "Answer", "text": "Y is another thing."}
                }
              ]
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("What is X?");
        result.Should().Contain("X is a thing.");
        result.Should().Contain("What is Y?");
        result.Should().Contain("Y is another thing.");
    }

    // ---------------------------------------------------------------------------
    // DiscussionForumPosting / Comment / SocialMediaPosting
    // ---------------------------------------------------------------------------

    [Fact]
    public void DiscussionForumPosting_extracts_body_and_comments()
    {
        var doc = Parse("""
            {
              "@type": "DiscussionForumPosting",
              "name": "Thread Title",
              "articleBody": "Original post body text.",
              "comment": [
                {"@type": "Comment", "text": "Reply one."},
                {"@type": "Comment", "text": "Reply two."}
              ]
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Original post body text.");
        result.Should().Contain("Reply one.");
        result.Should().Contain("Reply two.");
    }

    [Fact]
    public void SocialMediaPosting_extracts_text_field()
    {
        var doc = Parse("""
            {
              "@type": "SocialMediaPosting",
              "name": "A Post",
              "text": "The text of the social media post."
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("The text of the social media post.");
    }

    // ---------------------------------------------------------------------------
    // ItemList / ListItem
    // ---------------------------------------------------------------------------

    [Fact]
    public void ItemList_extracts_itemListElements()
    {
        var doc = Parse("""
            {
              "@type": "ItemList",
              "name": "Top 3 Things",
              "itemListElement": [
                {"@type": "ListItem", "name": "Item Alpha", "description": "About Alpha."},
                {"@type": "ListItem", "name": "Item Beta", "description": "About Beta."},
                {"@type": "ListItem", "name": "Item Gamma", "description": "About Gamma."}
              ]
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Item Alpha");
        result.Should().Contain("About Alpha.");
        result.Should().Contain("Item Gamma");
    }

    // ---------------------------------------------------------------------------
    // WebPage
    // ---------------------------------------------------------------------------

    [Fact]
    public void WebPage_extracts_description_and_text()
    {
        var doc = Parse("""
            {
              "@type": "WebPage",
              "name": "Page Title",
              "description": "Page description text.",
              "text": "Longer page body content."
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Page description text.");
        result.Should().Contain("Longer page body content.");
    }

    [Fact]
    public void WebPage_with_mainEntity_delegates_recursively()
    {
        var doc = Parse("""
            {
              "@type": "WebPage",
              "name": "Container Page",
              "mainEntity": {
                "@type": "Article",
                "articleBody": "Article nested inside WebPage."
              }
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Article nested inside WebPage.");
    }

    // ---------------------------------------------------------------------------
    // Recipe / Event
    // ---------------------------------------------------------------------------

    [Fact]
    public void Recipe_extracts_description_and_instructions()
    {
        var doc = Parse("""
            {
              "@type": "Recipe",
              "name": "Chocolate Cake",
              "description": "A rich and moist chocolate cake.",
              "recipeInstructions": "Mix ingredients. Bake at 180C for 30 min."
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("A rich and moist chocolate cake.");
        result.Should().Contain("Mix ingredients.");
    }

    [Fact]
    public void Event_extracts_name_and_description()
    {
        var doc = Parse("""
            {
              "@type": "Event",
              "name": "Tech Conference 2026",
              "description": "Annual gathering of tech professionals."
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Tech Conference 2026");
        result.Should().Contain("Annual gathering of tech professionals.");
    }

    // ---------------------------------------------------------------------------
    // StripHtml: inline HTML inside field values
    // ---------------------------------------------------------------------------

    [Fact]
    public void StripHtml_removes_inline_html_from_text_fields()
    {
        var doc = Parse("""
            {
              "@type": "Article",
              "articleBody": "<p>This is <strong>bold</strong> and <em>italic</em> text.</p>"
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("This is");
        result.Should().Contain("bold");
        result.Should().Contain("italic text.");
        result.Should().NotContain("<p>");
        result.Should().NotContain("<strong>");
    }

    // ---------------------------------------------------------------------------
    // @type as array (Publisher uses multiple types)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Type_as_array_uses_first_recognisable_type()
    {
        var doc = Parse("""
            {
              "@type": ["NewsArticle", "Article"],
              "headline": "Array type headline",
              "articleBody": "Content under array @type."
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Content under array @type.");
    }

    // ---------------------------------------------------------------------------
    // Multiple scripts on the same page
    // ---------------------------------------------------------------------------

    [Fact]
    public void Multiple_ld_json_scripts_are_combined()
    {
        var html = """
            <html><head>
              <script type="application/ld+json">{"@type":"Article","articleBody":"First article."}</script>
              <script type="application/ld+json">{"@type":"BlogPosting","articleBody":"Second post."}</script>
            </head><body></body></html>
            """;
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("First article.");
        result.Should().Contain("Second post.");
    }

    // ---------------------------------------------------------------------------
    // Top-level array (some sites emit an array as the root JSON)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Root_array_of_schemas_is_enumerated()
    {
        var doc = Parse("""
            [
              {"@type":"Article","articleBody":"Article from array."},
              {"@type":"Event","name":"Event from array","description":"Desc."}
            ]
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Article from array.");
        result.Should().Contain("Event from array");
    }

    // ---------------------------------------------------------------------------
    // Unknown type falls back to probing common fields
    // ---------------------------------------------------------------------------

    [Fact]
    public void Unknown_type_probes_articleBody_and_description()
    {
        var doc = Parse("""
            {
              "@type": "SomeCustomType",
              "articleBody": "Custom type body content.",
              "description": "Custom description."
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Custom type body content.");
    }

    // ---------------------------------------------------------------------------
    // Product is explicitly skipped
    // ---------------------------------------------------------------------------

    [Fact]
    public void Product_type_is_skipped_entirely()
    {
        var doc = Parse("""
            {
              "@type": "Product",
              "name": "Widget X1000",
              "description": "Premium widget with chrome finish.",
              "articleBody": "Would be extracted from a non-product type."
            }
            """);

        // Product has a description, but the type is explicitly in the skip branch.
        // Confirmed by code: the Product case has an empty body (no emit calls).
        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().BeNull("Product type is explicitly suppressed to avoid shopping metadata noise");
    }

    // ---------------------------------------------------------------------------
    // recipeInstructions as array of HowToStep objects
    // ---------------------------------------------------------------------------

    [Fact]
    public void Recipe_with_HowToStep_array_instructions_extracts_text()
    {
        var doc = Parse("""
            {
              "@type": "Recipe",
              "name": "Pasta",
              "description": "Classic pasta dish.",
              "recipeInstructions": [
                {"@type": "HowToStep", "text": "Boil the water.", "name": "Step 1"},
                {"@type": "HowToStep", "text": "Add the pasta.", "name": "Step 2"}
              ]
            }
            """);

        var result = JsonLdContentExtractor.ExtractMainContent(doc);
        result.Should().NotBeNull();
        result.Should().Contain("Boil the water.");
        result.Should().Contain("Add the pasta.");
    }
}
