using Microsoft.AspNetCore.Mvc;
using StyloExtract.AspNetCore.Policies;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// MVC controller used by MvcController_ResponsePolicyAttribute_AppliesPolicy in ReviewFixTests.
/// Proves that [ResponsePolicy] on an MVC action is discovered through the endpoint metadata system.
/// </summary>
[ApiController]
[Route("api/review-policy")]
public sealed class ReviewPolicyTestController : ControllerBase
{
    [HttpGet("article")]
    [ResponsePolicy("md")]
    public IActionResult GetArticle() =>
        Content(ReviewFixTests.SampleHtmlForController, "text/html");
}
