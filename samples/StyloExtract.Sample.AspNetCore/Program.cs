using Microsoft.AspNetCore.Http.Extensions;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using StyloExtract.AspNetCore.Markdown;
using StyloExtract.Sample.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Wire the full StyloExtract stack.
builder.Services.AddStyloExtract(o =>
{
    o.StorePath = ":memory:"; // sample uses in-memory SQLite; swap for a real path in production
    o.DefaultProfile = ExtractionProfile.RagFull;
});

// Wire the negotiation middleware with caching and query override on by default for the demo.
builder.Services.AddStyloExtractMarkdownNegotiation(o =>
{
    o.DefaultProfile = ExtractionProfile.RagFull;
    o.AcceptOverrideQueryName = "format";
    o.Cache.Enabled = true;
    o.Cache.AbsoluteExpiration = TimeSpan.FromMinutes(5);
    o.Cache.EnableEtag = true;
    o.Cache.EmitCacheControlHeader = false; // set to true to emit Cache-Control: public
});

builder.Services.AddControllers();

var app = builder.Build();

// Register the negotiation middleware before routing so all endpoints are covered.
app.UseStyloExtractMarkdownNegotiation();
app.MapControllers();

// Minimal API endpoint demonstrating .WithMarkdownNegotiation().
app.MapGet("/spa-like", SamplePages.SpaLike)
   .WithMarkdownNegotiation();

// Inline IResult demo using StyloExtractResults.HtmlOrMarkdown.
app.MapGet("/inline/{id:int}", (int id, HttpContext ctx) =>
    StyloExtractResults.HtmlOrMarkdown(
        SamplePages.InlineArticle(id),
        new Uri(ctx.Request.GetDisplayUrl())));

// Root: index of demo endpoints.
app.MapGet("/", () => Results.Content(SamplePages.Index(), "text/html"));

app.Run();
