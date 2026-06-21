using Microsoft.AspNetCore.Mvc;
using StyloExtract.AspNetCore.Markdown;
using StyloExtract.Abstractions;

namespace StyloExtract.AspNetCore.Tests.TestWebApp;

[ApiController]
[Route("api")]
public sealed class TestController : ControllerBase
{
    [HttpGet("html-attribute")]
    [NegotiateMarkdown]
    public IActionResult HtmlWithAttribute() =>
        Content(MarkdownMiddlewareFactory.SampleHtml, "text/html");

    [HttpGet("html-attribute-profile")]
    [NegotiateMarkdown(ExtractionProfile.AgentNavigation)]
    public IActionResult HtmlWithProfile() =>
        Content(MarkdownMiddlewareFactory.SampleHtml, "text/html");

    [HttpGet("plain-html")]
    public IActionResult HtmlNoAttribute() =>
        Content(MarkdownMiddlewareFactory.SampleHtml, "text/html");
}
