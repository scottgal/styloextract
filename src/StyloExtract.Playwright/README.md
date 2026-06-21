# Mostlylucid.StyloExtract.Playwright

Optional rendered-DOM fetcher for StyloExtract. Fetches client-side-rendered HTML via Microsoft.Playwright before passing it to the extraction pipeline.

## What this package is

Provides:

- `PlaywrightHtmlFetcher` (implements `IRenderedHtmlFetcher`) - launches a headless Chromium instance via Microsoft.Playwright, navigates to a URL, waits for network idle, and returns the full rendered HTML and final URI (after redirects)
- `PlaywrightInstaller` - static helper to check for installed browser binaries and invoke the Playwright browser-install script

This package exists separately from the core stack so that the eight AOT-compatible packages remain AOT-publishable. Playwright uses reflection and is not AOT-compatible. Any host that needs `PlaywrightHtmlFetcher` accepts the AOT restriction scoped to this one package.

## When to add this package

Add `Mostlylucid.StyloExtract.Playwright` explicitly only when you need to fetch JS-rendered pages. This includes:
- Single-page applications where the article content is rendered client-side
- Sites that return skeleton HTML and populate content via fetch calls
- Any URL where a plain `HttpClient.GetStringAsync` returns empty content blocks

Do not add this package if your target sites serve fully-rendered HTML server-side.

## Usage

```bash
dotnet add package Mostlylucid.StyloExtract.Playwright
```

```csharp
// Install browsers once (CLI equivalent: stylo-extract install-browsers)
PlaywrightInstaller.EnsureBrowsersInstalled("chromium");

// Fetch rendered HTML
await using var fetcher = new PlaywrightHtmlFetcher();
var rendered = await fetcher.FetchAsync(new Uri("https://spa-example.com/page"), new RenderOptions());

// Pass to the extractor as normal
var result = await extractor.ExtractAsync(rendered.Html, rendered.FinalUri);
```

```csharp
// CLI usage - --rendered flag handles install automatically
stylo-extract extract https://spa-example.com/page --rendered
```

## Not AOT compatible

This package is explicitly `IsAotCompatible=false`. Do not include it in AOT-published projects that need a trim-safe binary. Keep it in a separate host process or sidecar.

---

[Full documentation and package family](https://github.com/scottgal/styloextract)
