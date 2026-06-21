namespace StyloExtract.Sample.AspNetCore;

/// <summary>Static HTML pages used by the sample app's demo endpoints.</summary>
public static class SamplePages
{
    public static string Index() => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>StyloExtract Sample</title>
          <style>
            body { font-family: system-ui, sans-serif; max-width: 800px; margin: 2rem auto; padding: 0 1rem; }
            h1 { color: #2c3e50; }
            table { border-collapse: collapse; width: 100%; }
            th, td { text-align: left; padding: 0.5rem 1rem; border: 1px solid #ddd; }
            th { background: #f4f4f4; }
            code { background: #f0f0f0; padding: 0.1em 0.4em; border-radius: 3px; font-size: 0.9em; }
            pre { background: #f0f0f0; padding: 1rem; overflow-x: auto; border-radius: 4px; }
          </style>
        </head>
        <body>
          <h1>StyloExtract.AspNetCore Demo</h1>
          <p>
            This sample demonstrates all three negotiation paths: global middleware, the
            <code>[NegotiateMarkdown]</code> MVC attribute, and the <code>.WithMarkdownNegotiation()</code>
            Minimal API extension. It also exercises query-string Accept override and IDistributedCache caching.
          </p>

          <h2>Endpoints</h2>
          <table>
            <thead>
              <tr><th>Path</th><th>Integration type</th><th>Notes</th></tr>
            </thead>
            <tbody>
              <tr><td><a href="/article">/article</a></td><td>Middleware (global)</td><td>HTML by default; add Accept or ?format=markdown</td></tr>
              <tr><td><a href="/product">/product</a></td><td>Middleware (global, no attribute)</td><td>Shows middleware catches everything</td></tr>
              <tr><td><a href="/product/featured">/product/featured</a></td><td>MVC attribute</td><td>[NegotiateMarkdown] pinned to RagFull</td></tr>
              <tr><td><a href="/spa-like">/spa-like</a></td><td>Minimal API filter</td><td>.WithMarkdownNegotiation()</td></tr>
              <tr><td><a href="/inline/1">/inline/1</a></td><td>Minimal API inline</td><td>StyloExtractResults.HtmlOrMarkdown</td></tr>
            </tbody>
          </table>

          <h2>Example curl commands</h2>
          <pre>
        # Plain HTML (any endpoint)
        curl http://localhost:5080/article

        # Markdown via Accept header
        curl -H "Accept: text/markdown" http://localhost:5080/article

        # Markdown via query override (browser-friendly)
        curl "http://localhost:5080/article?format=markdown"

        # Profile via header
        curl -H "Accept: text/markdown" -H "X-Stylo-Profile: AgentNavigation" http://localhost:5080/article

        # Profile via query
        curl "http://localhost:5080/article?format=markdown&amp;stylo_profile=AgentNavigation"

        # Cache demonstration (watch X-Stylo-Cache)
        curl -vI "http://localhost:5080/article?format=markdown" 2>&amp;1 | grep -i x-stylo
        curl -vI "http://localhost:5080/article?format=markdown" 2>&amp;1 | grep -i x-stylo

        # Attribute path
        curl "http://localhost:5080/product/featured?format=markdown"

        # Minimal API filter path
        curl "http://localhost:5080/spa-like?format=markdown"
          </pre>
        </body>
        </html>
        """;

    public static string Article() => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>Understanding Layout Fingerprinting</title>
        </head>
        <body>
          <header>
            <nav><a href="/">Home</a> | <a href="/article">Articles</a> | <a href="/product">Products</a></nav>
          </header>
          <main>
            <article>
              <h1>Understanding Layout Fingerprinting</h1>
              <p class="byline">Published 2024-01-15 by the StyloExtract team</p>
              <p>
                Layout fingerprinting is a technique for identifying recurring page structure patterns
                across a website without relying on per-page heuristics. Instead of analysing each HTML
                document in isolation, the system computes a structural hash of the DOM tree and uses it
                as a cache key for a learned extractor.
              </p>
              <h2>How it works</h2>
              <p>
                The first time StyloExtract encounters a layout, it induces an extractor: a set of CSS
                selectors and block roles derived from the DOM. Every subsequent page that matches the same
                layout fingerprint reuses that extractor. The extractor is a learned centroid that drifts
                and refits as the site evolves.
              </p>
              <ul>
                <li>Fast-path LSH match for known templates (sub-millisecond)</li>
                <li>Slow-path pq-gram cosine fallback for structurally similar layouts</li>
                <li>Novel template induction on first encounter</li>
                <li>Centroid refit when drift crosses a configurable threshold</li>
              </ul>
              <h2>Why it matters for RAG</h2>
              <p>
                Retrieval-augmented generation pipelines often need clean, structured text from the same
                sources repeatedly. Using layout fingerprinting means subsequent extractions from the same
                site skip the expensive heuristic analysis entirely and go straight to selector application.
                The result is consistent, high-quality Markdown output at much lower latency.
              </p>
              <blockquote>
                The extractor is a learned artefact. It improves over time as the centroid observes more
                page variants, producing more accurate extraction as the model matures.
              </blockquote>
            </article>
          </main>
          <footer>
            <p>StyloExtract Sample App</p>
          </footer>
        </body>
        </html>
        """;

    public static string SpaLike(HttpContext ctx) => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>SPA-Like Article</title>
        </head>
        <body>
          <header><nav><a href="/">Home</a></nav></header>
          <main>
            <article>
              <h1>Client-Side Rendered Content</h1>
              <p>
                This endpoint simulates a single-page application's API response that returns HTML fragments.
                The Minimal API <code>.WithMarkdownNegotiation()</code> filter intercepts the HTML result and
                converts it to Markdown when <code>Accept: text/markdown</code> is present.
              </p>
              <h2>Key characteristics</h2>
              <p>
                Unlike the global middleware approach, the endpoint filter is scoped to this specific route.
                No other routes are affected. The filter runs after routing resolves the endpoint but before
                the response is written to the wire.
              </p>
              <ul>
                <li>Route-scoped: only this endpoint is covered</li>
                <li>Supports query-string override: <code>?format=markdown</code></li>
                <li>Caching: repeated requests return <code>X-Stylo-Cache: hit</code></li>
              </ul>
            </article>
          </main>
          <footer><p>StyloExtract Sample</p></footer>
        </body>
        </html>
        """;

    public static string InlineArticle(int id) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>Inline Article {id}</title>
        </head>
        <body>
          <header><nav><a href="/">Home</a></nav></header>
          <main>
            <article>
              <h1>Inline Article {id}</h1>
              <p>
                This article (id={id}) is served via <code>StyloExtractResults.HtmlOrMarkdown</code>,
                the inline IResult factory. The handler itself decides whether to serve HTML or Markdown
                based on the effective Accept header, without requiring any filter or middleware wrapping.
              </p>
              <h2>Usage pattern</h2>
              <p>
                Call <code>StyloExtractResults.HtmlOrMarkdown(html, sourceUri)</code> and return the result
                directly. The result implementation performs the extraction internally when Markdown is
                requested, or passes through the original HTML otherwise.
              </p>
              <ul>
                <li>Simplest approach for Minimal API handlers</li>
                <li>Works with query-string Accept override</li>
                <li>Fully participates in the cache when <code>Cache.Enabled</code> is true</li>
              </ul>
            </article>
          </main>
          <footer><p>StyloExtract Sample</p></footer>
        </body>
        </html>
        """;

    public static string Product() => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>Product Catalog</title>
        </head>
        <body>
          <header><nav><a href="/">Home</a> | <a href="/product">Products</a></nav></header>
          <main>
            <article>
              <h1>Product Catalog</h1>
              <p>
                This page is served by a controller action without the <code>[NegotiateMarkdown]</code>
                attribute. The global middleware still intercepts it when the client prefers Markdown,
                demonstrating that the middleware covers all HTML responses, not just annotated endpoints.
              </p>
              <h2>Products</h2>
              <ul>
                <li>StyloExtract Core - Layout fingerprinting and extraction engine</li>
                <li>StyloExtract AspNetCore - DI extensions and negotiation middleware</li>
                <li>StyloExtract Playwright - JS-rendered page fetching (non-AOT)</li>
              </ul>
            </article>
          </main>
          <footer><p>StyloExtract Sample</p></footer>
        </body>
        </html>
        """;

    public static string ProductFeatured() => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>Featured Product</title>
        </head>
        <body>
          <header><nav><a href="/">Home</a> | <a href="/product">Products</a></nav></header>
          <main>
            <article>
              <h1>Featured: StyloExtract AspNetCore</h1>
              <p>
                This page is served by a controller action decorated with
                <code>[NegotiateMarkdown(ExtractionProfile.RagFull)]</code>. The attribute pins the
                extraction profile to RagFull regardless of any profile header or query parameter.
              </p>
              <h2>Package features</h2>
              <p>
                The AspNetCore package is the single entry point most applications need. It pulls in the
                full StyloExtract stack transitively and wires it into the DI container with a single
                <code>AddStyloExtract()</code> call.
              </p>
              <ul>
                <li>Zero-config startup: one method call registers everything</li>
                <li>In-memory SQLite fallback for testing (<code>o.StorePath = ":memory:"</code>)</li>
                <li>AOT-compatible: <code>IsAotCompatible=true</code></li>
                <li>Query-string Accept override for browser demos</li>
                <li>IDistributedCache caching with ETag and Cache-Control support</li>
              </ul>
            </article>
          </main>
          <footer><p>StyloExtract Sample</p></footer>
        </body>
        </html>
        """;
}
