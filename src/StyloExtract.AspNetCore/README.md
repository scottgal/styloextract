# Mostlylucid.StyloExtract.AspNetCore

`AddStyloExtract()` DI extensions for ASP.NET Core and any `Microsoft.Extensions.DependencyInjection` host, plus opt-in Markdown content negotiation middleware.

## What this package is

The canonical way to register StyloExtract in any .NET application that uses `IServiceCollection`. It depends on `Core`, `Html`, `Fingerprint`, `Templates`, `Heuristics`, and `Markdown` and wires them all up correctly.

Since v1.1.0 it also ships the Markdown content negotiation suite: a global middleware, a per-action MVC attribute, and a Minimal API extension that all transparently return Markdown instead of HTML when a client sends `Accept: text/markdown`.

## When to depend on this directly

This is the package most application code should reference directly. Add it to your web API, worker service, or console application, call `AddStyloExtract()`, and inject `ILayoutExtractor` wherever you need it.

```bash
dotnet add package Mostlylucid.StyloExtract.AspNetCore
```

## Usage

```csharp
// Program.cs (ASP.NET Core)
builder.Services.AddStyloExtract(o =>
{
    o.StorePath    = "styloextract-templates.db";
    o.HostHashKey  = Environment.GetEnvironmentVariable("STYLOEXTRACT_HMAC_KEY");
    o.DefaultProfile = ExtractionProfile.RagFull;

    o.Match.FastPathJaccardThreshold = 0.85;
    o.Match.SlowPathCosineThreshold  = 0.75;
    o.Centroid.DriftRefitThreshold   = 0.35;
});
```

```csharp
// Inject ILayoutExtractor in a controller, service, or background worker
public class ContentService(ILayoutExtractor extractor)
{
    public async Task<string> GetMarkdownAsync(string html, Uri uri)
    {
        var result = await extractor.ExtractAsync(html, uri);
        return result.Markdown;
    }
}
```

### Version event sink

To receive template version change events, register an `ITemplateVersionEventSink` before calling `AddStyloExtract`:

```csharp
services.AddSingleton<ITemplateVersionEventSink, MyVersionEventSink>();
services.AddStyloExtract(o => { ... });
```

If no sink is registered, `DefaultNoopVersionEventSink` is used (events discarded).

## Markdown content negotiation

StyloExtract can transparently serve Markdown instead of HTML when a client sends `Accept: text/markdown`. Three opt-in paths are provided; choose the one that fits your app.

### 1. Global middleware

Call `AddStyloExtractMarkdownNegotiation()` in your services and `UseStyloExtractMarkdownNegotiation()` in your pipeline. Every HTML response on every route is subject to negotiation.

```csharp
// Program.cs
builder.Services.AddStyloExtract(o => o.StorePath = "styloextract.db");
builder.Services.AddStyloExtractMarkdownNegotiation(o =>
{
    o.DefaultProfile = ExtractionProfile.RagFull;
    o.EmitVaryHeader = true;       // adds Vary: Accept to negotiated responses
    o.MaxBodyBytes   = 4 * 1024 * 1024; // skip bodies larger than 4 MB
});

// ...
app.UseRouting();
app.UseStyloExtractMarkdownNegotiation(); // after UseRouting
app.MapControllers();
```

A client that sends `Accept: text/markdown` receives `Content-Type: text/markdown; charset=utf-8`. All other clients receive the original HTML. The `Vary: Accept` header is added automatically so HTTP caches differentiate responses by content type.

### 2. Per-action MVC attribute

Use `[NegotiateMarkdown]` on a controller action or controller class when you want per-endpoint control without a global middleware.

```csharp
[HttpGet("article/{id}")]
[NegotiateMarkdown(ExtractionProfile.AgentNavigation)]
public IActionResult GetArticle(int id)
{
    var html = BuildArticleHtml(id);
    return Content(html, "text/html");
}
```

The attribute runs as an `IAsyncResultFilter`. It does not require the global middleware to be registered.

### 3. Minimal API

Use `.WithMarkdownNegotiation()` on a route builder to add an endpoint filter, or use `StyloExtractResults.HtmlOrMarkdown(...)` to produce the right result type in the handler itself.

```csharp
// Endpoint filter approach
app.MapGet("/article", () => Results.Content(BuildHtml(), "text/html"))
   .WithMarkdownNegotiation(ExtractionProfile.RagFull);

// Inline IResult approach
app.MapGet("/article", (IHttpContextAccessor acc) =>
    StyloExtractResults.HtmlOrMarkdown(BuildHtml()));
```

`StyloExtractResults.HtmlOrMarkdown` inspects `Accept` and calls `ILayoutExtractor` before the response is written, making it the simplest approach for Minimal API when you control the handler body.

### Profile selection

The profile used for extraction is resolved in this order:

1. `X-Stylo-Profile` request header (e.g. `AgentNavigation`)
2. `stylo_profile` query string parameter (e.g. `?stylo_profile=RagFull`)
3. `MarkdownNegotiationOptions.DefaultProfile` (default: `RagFull`)

The header and query names are configurable via `MarkdownNegotiationOptions.ProfileHeaderName` and `ProfileQueryName`.

### Query-string Accept override (v1.1.0+)

Browser clients cannot easily set custom `Accept` headers. The `AcceptOverrideQueryName` option (default: `"format"`) maps a query-string value to a virtual `Accept` header, so `?format=markdown` behaves identically to `Accept: text/markdown` for any browser.

```csharp
builder.Services.AddStyloExtractMarkdownNegotiation(o =>
{
    o.AcceptOverrideQueryName = "format"; // null to disable
    // Default mappings: markdown/md => text/markdown, html => text/html,
    //                   json => application/json, text => text/plain
});
```

When the override fires, the response carries `X-Stylo-Accept-Override: text/markdown` so consumers can see it was applied.

### Caching (v1.1.0+)

Enable `Cache.Enabled` to avoid re-extracting the same URL + profile combination on repeated requests. The implementation uses `IDistributedCache` (in-memory by default; inject a real distributed cache before calling `AddStyloExtractMarkdownNegotiation` to upgrade).

```csharp
builder.Services.AddStyloExtractMarkdownNegotiation(o =>
{
    o.Cache.Enabled = true;
    o.Cache.AbsoluteExpiration = TimeSpan.FromMinutes(5);
    o.Cache.SlidingExpiration = TimeSpan.FromMinutes(2);
    o.Cache.EnableEtag = true;               // honors If-None-Match; returns 304
    o.Cache.EmitCacheControlHeader = false;  // set true for CDN-friendly Cache-Control: public
});
```

Cache key shape: `sha256(method + "|" + scheme + "|" + host + "|" + path + "|" + sortedQuery(minus override key) + "|" + profile)`. The Accept override query parameter is excluded from the key so `?format=markdown` and a bare `Accept: text/markdown` request share the same cache slot.

Response headers on Markdown responses:

| Header | Value |
|---|---|
| `X-Stylo-Cache` | `miss` or `hit` |
| `ETag` | SHA-256 digest of the Markdown bytes (when `EnableEtag = true`) |
| `Cache-Control` | `public, max-age=N` (when `EmitCacheControlHeader = true`) |

## AOT

This package is `IsAotCompatible=true`. The negotiation middleware and attribute use no reflection-based JSON serialization; Markdown output is plain text. `IDistributedCache` and `MemoryDistributedCache` are both AOT-safe.

---

[Full documentation and package family](https://github.com/scottgal/styloextract)
