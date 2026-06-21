# Mostlylucid.StyloExtract.AspNetCore

`AddStyloExtract()` DI extensions for ASP.NET Core and any `Microsoft.Extensions.DependencyInjection` host.

## What this package is

The canonical way to register StyloExtract in any .NET application that uses `IServiceCollection`. It depends on `Core`, `Html`, `Fingerprint`, `Templates`, `Heuristics`, and `Markdown` and wires them all up correctly.

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

## AOT

This package is `IsAotCompatible=true`.

---

[Full documentation and package family](https://github.com/mostlylucid/stylobot-extract)
