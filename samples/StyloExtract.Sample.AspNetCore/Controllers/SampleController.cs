using Microsoft.AspNetCore.Mvc;
using StyloExtract.AspNetCore.Markdown;
using StyloExtract.AspNetCore.Policies;

namespace StyloExtract.Sample.AspNetCore.Controllers;

[ApiController]
[Route("sample")]
public sealed class SampleController : ControllerBase
{
    /// <summary>
    /// New-style: [ResponsePolicy] attribute picks up the named policy from ResponsePolicyOptions.
    /// Requires UseStyloExtract() in the pipeline.
    /// </summary>
    [HttpGet("policy-attr")]
    [ResponsePolicy("md")]
    public IActionResult PolicyAttrDemo() =>
        Content(SamplePages.Article(), "text/html");

    /// <summary>
    /// Legacy-style: [NegotiateMarkdown] attribute filter still works in v1.2.
    /// Uses the IAsyncResultFilter path independent of the new framework.
    /// </summary>
    [HttpGet("legacy-attr")]
    [NegotiateMarkdown]
    public IActionResult LegacyAttrDemo() =>
        Content(SamplePages.Article(), "text/html");
}
