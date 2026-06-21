using Microsoft.AspNetCore.Mvc;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore.Markdown;

namespace StyloExtract.Sample.AspNetCore.Controllers;

[ApiController]
[Route("product")]
public sealed class ProductController : ControllerBase
{
    /// <summary>
    /// Returns the product catalog page. No <c>[NegotiateMarkdown]</c> attribute is used here:
    /// the global middleware intercepts this response when the client prefers Markdown,
    /// demonstrating that middleware coverage is unconditional.
    /// </summary>
    [HttpGet]
    public IActionResult Get() =>
        Content(SamplePages.Product(), "text/html");

    /// <summary>
    /// Returns the featured product page with the <c>[NegotiateMarkdown]</c> attribute pinned
    /// to <see cref="ExtractionProfile.RagFull"/>. Works independently of the global middleware.
    /// </summary>
    [HttpGet("featured")]
    [NegotiateMarkdown(ExtractionProfile.RagFull)]
    public IActionResult Featured() =>
        Content(SamplePages.ProductFeatured(), "text/html");
}
