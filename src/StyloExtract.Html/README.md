# Mostlylucid.StyloExtract.Html

AngleSharp-backed DOM parser and cleaner for StyloExtract.

## What this package is

Provides two implementations against `StyloExtract.Abstractions` interfaces:

- `AngleSharpHtmlDomParser` (implements `IHtmlDomParser`) - parses raw HTML into an AngleSharp `IDocument`, normalises encoding, handles fragment inputs
- `DomCleaner` (implements `IDomCleaner`) - strips scripts, style, noscript, SVG, and other non-content nodes; normalises whitespace; collapses empty containers

The cleaner is a prerequisite for fingerprinting: noise in the DOM inflates shingle variance and produces false-negative matches. The default cleaning rules follow the same boilerplate-removal logic used by block classifiers.

## When to depend on this directly

Most consumers get this package transitively via `Mostlylucid.StyloExtract.AspNetCore` or `Mostlylucid.StyloExtract.Core`. Take a direct dependency only if you need `AngleSharpHtmlDomParser` or `DomCleaner` outside of the full extraction pipeline, for example in a standalone DOM analysis tool.

## Usage

```csharp
// Registration (handled automatically by AddStyloExtract)
services.AddSingleton<IHtmlDomParser, AngleSharpHtmlDomParser>();
services.AddSingleton<IDomCleaner, DomCleaner>();
```

```csharp
// Direct usage (testing / standalone)
var parser = new AngleSharpHtmlDomParser();
var doc = parser.Parse(rawHtml);

var cleaner = new DomCleaner();
var cleaned = cleaner.Clean(doc);
```

## AOT

This package is `IsAotCompatible=true`. AngleSharp itself is AOT-safe on .NET 10.

---

[Full documentation and package family](https://github.com/mostlylucid/stylobot-extract)
