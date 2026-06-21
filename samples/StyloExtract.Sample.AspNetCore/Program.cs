using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using StyloExtract.AspNetCore.CacheHints;
using StyloExtract.AspNetCore.Markdown;
using StyloExtract.AspNetCore.Policies;
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

// Register named policies via the fluent builder (v1.2 recommended path).
// AddStyloExtractMarkdownNegotiation() must be called first so MarkdownNegotiationPolicy is in DI.
builder.Services.AddStyloExtract(b =>
{
    b.AddPolicy("md", p => p.NegotiateMarkdown());
    b.AddPolicy("cache", p => p.CacheHints(o =>
    {
        o.MaxAge = TimeSpan.FromMinutes(5);
        o.Public = true;
        o.EmitETag = true;
        o.HonorIfNoneMatch = true;
    }));
});

builder.Services.AddControllers();

var app = builder.Build();

// Legacy middleware path: still active for backward-compat endpoints (MVC controllers, /spa-like, /inline).
app.UseStyloExtractMarkdownNegotiation();

// New response-policy pipeline: runs AFTER the legacy middleware so both paths coexist.
// Picks up endpoints annotated with [ResponsePolicy(...)], WithResponsePolicy(...), etc.
app.UseStyloExtract();

app.MapControllers();

// New-style: Minimal API endpoint demonstrating chained WithResponsePolicy.
// Markdown negotiation then cache hints (ETag computed from Markdown body).
app.MapGet("/api/policy-demo",
    () => Results.Content(SamplePages.PolicyDemo(), "text/html"))
    .WithResponsePolicy("md")
    .WithResponsePolicy("cache");

// New-style: cache hints only (no Markdown conversion).
app.MapGet("/api/cache-demo",
    () => Results.Content(SamplePages.Article(), "text/html"))
    .WithResponsePolicy("cache");

// Legacy: Minimal API endpoint demonstrating .WithMarkdownNegotiation() (unchanged from v1.1).
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
