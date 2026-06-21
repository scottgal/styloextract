# StyloExtract.Sample.AspNetCore

A minimal ASP.NET Core demo app that exercises the response-policy framework (v1.2), all three Markdown content negotiation paths, the query-string Accept override, and the IDistributedCache caching layer.

## Running the sample

```bash
dotnet run --project samples/StyloExtract.Sample.AspNetCore --urls=http://localhost:5080
```

The app starts at `http://localhost:5080`. Visit the root path for an endpoint index with links and curl examples.

## Endpoints

| Path | Integration type | Notes |
|---|---|---|
| `/` | Index | HTML page listing all demo endpoints |
| `/api/policy-demo` | Response policy framework (v1.2) | Markdown + cache hints via `WithResponsePolicy` chain |
| `/api/cache-demo` | Cache hints only | ETag + Cache-Control via CacheHintPolicy |
| `/sample/policy-attr` | ResponsePolicy attribute (v1.2) | `[ResponsePolicy("md")]` on MVC action |
| `/sample/legacy-attr` | NegotiateMarkdown attribute (legacy) | `[NegotiateMarkdown]` still works in v1.2 |
| `/article` | Middleware (global) | ArticleController, no attribute |
| `/product` | Middleware (global) | ProductController, no attribute |
| `/product/featured` | MVC attribute | `[NegotiateMarkdown(ExtractionProfile.RagFull)]` |
| `/spa-like` | Minimal API filter | `.WithMarkdownNegotiation()` |
| `/inline/{id}` | Minimal API inline | `StyloExtractResults.HtmlOrMarkdown` |

## curl examples

```bash
# Response policy framework (v1.2): see ETag, Cache-Control, Content-Type
curl -sI -H "Accept: text/markdown" http://localhost:5080/api/policy-demo | grep -iE "etag|x-stylo|content-type|cache-control"

# ETag round-trip: second request returns 304 Not Modified
ETAG=$(curl -sI -H "Accept: text/markdown" http://localhost:5080/api/policy-demo | grep -i etag | awk '{print $2}' | tr -d '\r')
curl -sI -H "Accept: text/markdown" -H "If-None-Match: $ETAG" http://localhost:5080/api/policy-demo | head -1
# Expected: HTTP/1.1 304 Not Modified

# Cache hints only (no Markdown conversion)
curl -sI http://localhost:5080/api/cache-demo | grep -iE "etag|cache-control"

# New-style ResponsePolicy attribute on MVC action
curl -H "Accept: text/markdown" http://localhost:5080/sample/policy-attr

# Legacy NegotiateMarkdown attribute still works
curl -H "Accept: text/markdown" http://localhost:5080/sample/legacy-attr

# Plain HTML (browser default)
curl http://localhost:5080/article

# Markdown via Accept header
curl -H "Accept: text/markdown" http://localhost:5080/article

# Markdown via query-string override (browser-friendly, no header needed)
curl "http://localhost:5080/article?format=markdown"

# Profile selection via header
curl -H "Accept: text/markdown" -H "X-Stylo-Profile: AgentNavigation" http://localhost:5080/article

# Profile selection via query parameter
curl "http://localhost:5080/article?format=markdown&stylo_profile=AgentNavigation"

# Cache demonstration (watch X-Stylo-Cache: miss then hit)
curl -sI "http://localhost:5080/article?format=markdown" 2>&1 | grep -i x-stylo
curl -sI "http://localhost:5080/article?format=markdown" 2>&1 | grep -i x-stylo

# MVC attribute path
curl "http://localhost:5080/product/featured?format=markdown"

# Minimal API filter path
curl "http://localhost:5080/spa-like?format=markdown"

# Inline IResult path
curl "http://localhost:5080/inline/42?format=markdown"
```

## What to observe

- First request to any Markdown path returns `X-Stylo-Cache: miss`; subsequent identical requests return `X-Stylo-Cache: hit`
- The `X-Stylo-Accept-Override: text/markdown` response header appears when `?format=markdown` is used (showing the override fired)
- `If-None-Match` with the ETag value from a prior response returns `304 Not Modified`
- Different profiles (`?stylo_profile=RagFull` vs `?stylo_profile=AgentNavigation`) produce different cache entries
- The `?format=markdown` query parameter is excluded from the cache key: Accept-header and query-override requests share the same cache slot

## Configuration

Caching and query override are configured in `Program.cs`. Key options:

```csharp
builder.Services.AddStyloExtractMarkdownNegotiation(o =>
{
    o.AcceptOverrideQueryName = "format";   // null to disable
    o.Cache.Enabled = true;
    o.Cache.AbsoluteExpiration = TimeSpan.FromMinutes(5);
    o.Cache.EnableEtag = true;
    o.Cache.EmitCacheControlHeader = false; // set true for CDN-friendly responses
});
```
