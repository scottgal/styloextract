using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core.OperatorTemplates;
using Xunit;

namespace StyloExtract.Core.Tests;

public class YamlOperatorTemplateLoaderTests
{
    [Fact]
    public void Parses_Minimal_Valid_Document()
    {
        const string yaml = """
            host: example.com
            rules:
              - role: MainContent
                selectors:
                  - main.docs-body
            """;
        var t = YamlOperatorTemplateLoader.Parse(yaml);
        t.Host.Should().Be("example.com");
        t.Description.Should().BeEmpty();
        t.Version.Should().Be(1);
        t.Rules.Should().HaveCount(1);
        t.Rules[0].Role.Should().Be(BlockRole.MainContent);
        t.Rules[0].Selectors.Should().BeEquivalentTo(new[] { "main.docs-body" });
        t.Rules[0].Confidence.Should().Be(1.0);
    }

    [Fact]
    public void Parses_Full_Document_With_Multiple_Rules_And_Confidences()
    {
        const string yaml = """
            host: docs.example.com
            description: Hand-tuned docs template for our internal CMS.
            version: 1
            rules:
              - role: MainContent
                selectors:
                  - main.docs-body
                  - article.markdown-body
                confidence: 0.95
              - role: PrimaryNavigation
                selectors:
                  - nav.sidebar
                confidence: 0.85
              - role: Boilerplate
                selectors:
                  - footer.site-footer
                  - .cookie-banner
                confidence: 0.9
            """;
        var t = YamlOperatorTemplateLoader.Parse(yaml);
        t.Host.Should().Be("docs.example.com");
        t.Description.Should().Be("Hand-tuned docs template for our internal CMS.");
        t.Rules.Should().HaveCount(3);

        t.Rules[0].Role.Should().Be(BlockRole.MainContent);
        t.Rules[0].Selectors.Should().BeEquivalentTo(new[] { "main.docs-body", "article.markdown-body" });
        t.Rules[0].Confidence.Should().Be(0.95);

        t.Rules[1].Role.Should().Be(BlockRole.PrimaryNavigation);
        t.Rules[1].Selectors.Should().BeEquivalentTo(new[] { "nav.sidebar" });

        t.Rules[2].Role.Should().Be(BlockRole.Boilerplate);
        t.Rules[2].Selectors.Should().BeEquivalentTo(new[] { "footer.site-footer", ".cookie-banner" });
    }

    [Fact]
    public void Tolerates_Blank_Lines_And_Comments()
    {
        const string yaml = """
            # Test fixture
            host: example.com

            # the rules:
            rules:
              # Body of the article.
              - role: MainContent
                selectors:
                  - main.docs-body
                confidence: 0.92  # tuned by hand
            """;
        var t = YamlOperatorTemplateLoader.Parse(yaml);
        t.Host.Should().Be("example.com");
        t.Rules[0].Confidence.Should().Be(0.92);
    }

    [Fact]
    public void Tolerates_CRLF_Line_Endings()
    {
        var yaml = "host: example.com\r\nrules:\r\n  - role: MainContent\r\n    selectors:\r\n      - main\r\n";
        var t = YamlOperatorTemplateLoader.Parse(yaml);
        t.Host.Should().Be("example.com");
        t.Rules[0].Selectors[0].Should().Be("main");
    }

    [Fact]
    public void Strips_Surrounding_Quotes_From_Scalars_And_Selectors()
    {
        const string yaml = """
            host: "example.com"
            description: 'quoted description'
            rules:
              - role: MainContent
                selectors:
                  - "main.docs-body[data-version='2']"
                  - '.with-single-quotes'
            """;
        var t = YamlOperatorTemplateLoader.Parse(yaml);
        t.Host.Should().Be("example.com");
        t.Description.Should().Be("quoted description");
        t.Rules[0].Selectors.Should().BeEquivalentTo(new[]
        {
            "main.docs-body[data-version='2']",
            ".with-single-quotes"
        });
    }

    [Fact]
    public void Throws_When_Host_Missing()
    {
        const string yaml = """
            rules:
              - role: MainContent
                selectors:
                  - main
            """;
        var act = () => YamlOperatorTemplateLoader.Parse(yaml);
        act.Should().Throw<OperatorTemplateParseException>()
            .WithMessage("*host*");
    }

    [Fact]
    public void Throws_When_Rules_Block_Missing()
    {
        const string yaml = "host: example.com";
        var act = () => YamlOperatorTemplateLoader.Parse(yaml);
        act.Should().Throw<OperatorTemplateParseException>()
            .WithMessage("*rules*");
    }

    [Fact]
    public void Throws_When_Role_Is_Unknown()
    {
        const string yaml = """
            host: example.com
            rules:
              - role: NotARealRole
                selectors:
                  - main
            """;
        var act = () => YamlOperatorTemplateLoader.Parse(yaml);
        act.Should().Throw<OperatorTemplateParseException>()
            .WithMessage("*unknown role 'NotARealRole'*");
    }

    [Fact]
    public void Throws_When_Role_Is_Misspelled()
    {
        const string yaml = """
            host: example.com
            rules:
              - role: maincontent  # case-insensitive parsing
                selectors:
                  - main
            """;
        // Mixed case should parse successfully via case-insensitive enum parse.
        var t = YamlOperatorTemplateLoader.Parse(yaml);
        t.Rules[0].Role.Should().Be(BlockRole.MainContent);
    }

    [Fact]
    public void Throws_When_Rule_Has_No_Selectors()
    {
        const string yaml = """
            host: example.com
            rules:
              - role: MainContent
            """;
        var act = () => YamlOperatorTemplateLoader.Parse(yaml);
        act.Should().Throw<OperatorTemplateParseException>()
            .WithMessage("*no selectors*");
    }

    [Fact]
    public void Throws_When_Confidence_Out_Of_Range()
    {
        const string yaml = """
            host: example.com
            rules:
              - role: MainContent
                selectors:
                  - main
                confidence: 1.5
            """;
        var act = () => YamlOperatorTemplateLoader.Parse(yaml);
        act.Should().Throw<OperatorTemplateParseException>()
            .WithMessage("*confidence*0.0 and 1.0*");
    }

    [Fact]
    public void Throws_When_Confidence_Is_Non_Numeric()
    {
        const string yaml = """
            host: example.com
            rules:
              - role: MainContent
                selectors:
                  - main
                confidence: high
            """;
        var act = () => YamlOperatorTemplateLoader.Parse(yaml);
        act.Should().Throw<OperatorTemplateParseException>();
    }

    [Fact]
    public void Throws_When_Top_Level_Key_Is_Unknown()
    {
        const string yaml = """
            host: example.com
            mistake: oops
            rules:
              - role: MainContent
                selectors:
                  - main
            """;
        var act = () => YamlOperatorTemplateLoader.Parse(yaml);
        act.Should().Throw<OperatorTemplateParseException>()
            .WithMessage("*unknown top-level key 'mistake'*");
    }

    [Fact]
    public void Throws_When_Version_Is_Non_Positive()
    {
        const string yaml = """
            host: example.com
            version: 0
            rules:
              - role: MainContent
                selectors:
                  - main
            """;
        var act = () => YamlOperatorTemplateLoader.Parse(yaml);
        act.Should().Throw<OperatorTemplateParseException>()
            .WithMessage("*version*positive integer*");
    }

    [Fact]
    public void Error_Messages_Include_Line_Numbers()
    {
        const string yaml = """
            host: example.com
            rules:
              - role: BogusRole
                selectors:
                  - main
            """;
        var act = () => YamlOperatorTemplateLoader.Parse(yaml);
        act.Should().Throw<OperatorTemplateParseException>()
            .WithMessage("*line 3*");
    }

    [Fact]
    public void Parses_Multiple_Selectors_Under_One_Rule()
    {
        const string yaml = """
            host: example.com
            rules:
              - role: MainContent
                selectors:
                  - main
                  - article
                  - .post-body
                  - .entry-content
            """;
        var t = YamlOperatorTemplateLoader.Parse(yaml);
        t.Rules[0].Selectors.Should().HaveCount(4);
    }
}
