using Microsoft.AspNetCore.Mvc;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore.Markdown;

namespace StyloExtract.Sample.AspNetCore.Controllers;

[ApiController]
[Route("article")]
public sealed class ArticleController : ControllerBase
{
    /// <summary>
    /// Returns an article page. The global middleware handles negotiation: clients
    /// sending <c>Accept: text/markdown</c> (or <c>?format=markdown</c>) receive
    /// extracted Markdown; everyone else receives the original HTML.
    /// </summary>
    [HttpGet]
    public IActionResult Get() =>
        Content(SamplePages.Article(), "text/html");
}
