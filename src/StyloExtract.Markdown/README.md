# Mostlylucid.StyloExtract.Markdown

Profile-aware Markdown renderer for StyloExtract.

## What this package is

Provides `TypedMarkdownRenderer` (implements `IMarkdownRenderer`), which converts a list of classified `ExtractedBlock` records into Markdown according to the active extraction profile.

### Profiles

| Profile | Output |
|---|---|
| `RagFull` | All MainContent and secondary blocks with headings, paragraphs, lists, code blocks, and links preserved |
| `Title` | Title block only |
| `Minimal` | MainContent only, minimal formatting |

The renderer is profile-aware: the same block list produces different Markdown depending on which profile is requested. Profile selection happens at call time via `ExtractionOptions.Profile`, not at registration time.

## When to depend on this directly

Consumed transitively by `Mostlylucid.StyloExtract.AspNetCore`. Take a direct dependency only if you are building an alternative renderer (e.g. an HTML or plain-text output) that replaces `TypedMarkdownRenderer` in the DI container.

## Usage

```csharp
// Standard wiring (handled by AddStyloExtract)
services.AddSingleton<IMarkdownRenderer, TypedMarkdownRenderer>();
```

```csharp
// Standalone rendering (testing)
var renderer = new TypedMarkdownRenderer();
var markdown = renderer.Render(classifiedBlocks, ExtractionProfile.RagFull);
Console.WriteLine(markdown);
```

## AOT

This package is `IsAotCompatible=true`. Rendering uses only string operations; no reflection.

---

[Full documentation and package family](https://github.com/scottgal/styloextract)
