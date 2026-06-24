using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core.Llm;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Core.Tests;

public class LlmTemplateInducerTests
{
    private sealed class StubLlm : ILlmTextProvider
    {
        public string Response { get; set; } = "";
        public Exception? Throw { get; set; }
        public string? CapturedSystem { get; private set; }
        public string? CapturedUser { get; private set; }
        public int Calls { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            CapturedSystem = systemPrompt;
            CapturedUser = userPrompt;
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw is not null) throw Throw;
            return Task.FromResult(Response);
        }
    }

    private static AngleSharp.Dom.IDocument Doc(string body)
        => new AngleSharpHtmlDomParser().Parse($"<html><body>{body}</body></html>");

    private const string SimpleArticleHtml = """
        <main><article>
          <h1>Article title</h1>
          <p>Long enough paragraph for the renderer to pick this article up
             as substantive content with enough text mass to be meaningful.</p>
        </article></main>
        """;

    [Fact]
    public async Task Returns_Parsed_Template_When_Llm_Returns_Valid_Yaml_Block()
    {
        var llm = new StubLlm
        {
            Response = """
                ```yaml
                host: example.com
                description: test fixture
                version: 1
                rules:
                  - role: MainContent
                    selectors:
                      - main article
                    confidence: 0.95
                ```
                """,
        };
        var inducer = new LlmTemplateInducer(llm);
        var template = await inducer.InduceAsync(Doc(SimpleArticleHtml), "example.com");

        template.Should().NotBeNull();
        template!.Host.Should().Be("example.com");
        template.Rules.Should().HaveCount(1);
        template.Rules[0].Role.Should().Be(BlockRole.MainContent);
        template.Rules[0].Selectors[0].Should().Be("main article");
        llm.Calls.Should().Be(1);
        llm.CapturedSystem.Should().Contain("BlockRole");
        llm.CapturedUser.Should().Contain("Host: example.com");
        llm.CapturedUser.Should().Contain("ROOT body");
    }

    [Fact]
    public async Task Returns_Null_When_Llm_Response_Has_No_Yaml_Block_Or_Host_Line()
    {
        var inducer = new LlmTemplateInducer(new StubLlm
        {
            Response = "I don't know how to answer this. Sorry.",
        });
        var template = await inducer.InduceAsync(Doc(SimpleArticleHtml), "example.com");
        template.Should().BeNull();
    }

    [Fact]
    public async Task Returns_Null_When_Yaml_Block_Fails_Validation()
    {
        var inducer = new LlmTemplateInducer(new StubLlm
        {
            Response = """
                ```yaml
                host: example.com
                rules:
                  - role: NotARealRole
                    selectors:
                      - main
                ```
                """,
        });
        var template = await inducer.InduceAsync(Doc(SimpleArticleHtml), "example.com");
        template.Should().BeNull();
    }

    [Fact]
    public async Task Returns_Null_And_Logs_When_Provider_Throws_LlmProviderException()
    {
        var inducer = new LlmTemplateInducer(new StubLlm
        {
            Throw = new LlmProviderException("backend HTTP 500"),
        });
        var template = await inducer.InduceAsync(Doc(SimpleArticleHtml), "example.com");
        template.Should().BeNull();
    }

    [Fact]
    public async Task Returns_Null_When_Provider_Throws_Unexpected_Exception()
    {
        var inducer = new LlmTemplateInducer(new StubLlm
        {
            Throw = new InvalidOperationException("boom"),
        });
        var template = await inducer.InduceAsync(Doc(SimpleArticleHtml), "example.com");
        template.Should().BeNull();
    }

    [Fact]
    public async Task Propagates_OperationCanceledException()
    {
        var llm = new StubLlm
        {
            Throw = new OperationCanceledException(),
        };
        var inducer = new LlmTemplateInducer(llm);
        var act = () => inducer.InduceAsync(Doc(SimpleArticleHtml), "example.com");
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Rewrites_Host_When_Model_Hallucinated_A_Different_One()
    {
        var inducer = new LlmTemplateInducer(new StubLlm
        {
            Response = """
                ```yaml
                host: wrong-host-llm-made-up.com
                rules:
                  - role: MainContent
                    selectors:
                      - main
                ```
                """,
        });
        var template = await inducer.InduceAsync(Doc(SimpleArticleHtml), "the-real.host");
        template.Should().NotBeNull();
        template!.Host.Should().Be("the-real.host");
    }

    [Fact]
    public async Task Accepts_Bare_Yaml_When_No_Fence_Present()
    {
        // Some models drop the fence; the recovery path accepts bare YAML
        // when "host:" appears early in the response.
        var inducer = new LlmTemplateInducer(new StubLlm
        {
            Response = """
                host: example.com
                rules:
                  - role: MainContent
                    selectors:
                      - main
                """,
        });
        var template = await inducer.InduceAsync(Doc(SimpleArticleHtml), "example.com");
        template.Should().NotBeNull();
        template!.Rules[0].Selectors[0].Should().Be("main");
    }

    [Fact]
    public async Task Accepts_Generic_Triple_Fence_Without_Yaml_Tag()
    {
        // Some models open the fence as ``` rather than ```yaml.
        var inducer = new LlmTemplateInducer(new StubLlm
        {
            Response = """
                Sure! Here is the template:

                ```
                host: example.com
                rules:
                  - role: MainContent
                    selectors:
                      - main
                ```
                """,
        });
        var template = await inducer.InduceAsync(Doc(SimpleArticleHtml), "example.com");
        template.Should().NotBeNull();
        template!.Host.Should().Be("example.com");
    }

    [Fact]
    public async Task Returns_Null_For_Document_With_Empty_Body()
    {
        var parser = new AngleSharpHtmlDomParser();
        var doc = parser.Parse("<html></html>");
        var inducer = new LlmTemplateInducer(new StubLlm { Response = "anything" });
        var template = await inducer.InduceAsync(doc, "example.com");
        template.Should().BeNull();
    }

    [Fact]
    public void ExtractYamlBlock_Returns_Null_For_Empty_Or_Whitespace_Response()
    {
        LlmTemplateInducer.ExtractYamlBlock("").Should().BeNull();
        LlmTemplateInducer.ExtractYamlBlock("   \n\n  ").Should().BeNull();
    }

    [Fact]
    public void InduceAsync_Requires_Non_Empty_Host()
    {
        var inducer = new LlmTemplateInducer(new StubLlm());
        var act = () => inducer.InduceAsync(Doc(SimpleArticleHtml), "");
        act.Should().ThrowAsync<ArgumentException>();
    }
}
