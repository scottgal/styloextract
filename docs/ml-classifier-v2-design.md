# StyloExtract ML / LLM Template Induction (v2) — Design

**Status:** design, supersedes the previous "ONNX per-element classifier" draft.
**Scope:** the arbitrary-site coverage gap surfaced in
[`realworld-fixtures.md`](realworld-fixtures.md). Pages where the
heuristic classifier finds zero `MainContent` because the site's
framework / theme uses class names absent from
`framework-content-class-hints.json`.

## Why this design changed

The first ML design committed to per-element ONNX classification with a
LightGBM-on-WCXB training pipeline. Research against the 2026 SOTA
([`research notes`](#research-trail)) showed:

* **Wrapper / template induction is the actual problem we have**, not
  per-page content extraction. StyloExtract already caches per-host
  templates; the gap is "novel host comes in, heuristic can't induce a
  usable template." A per-element classifier helps marginally; an LLM
  that outputs a few reusable selectors helps a lot.
* **The 2026 SOTA for wrapper induction is LLM selector synthesis**
  (Co-Scraper, AXE, XPath Agent). Encoder-style models (MarkupLM,
  FreeDOM, DOM-LM) are now baselines those papers beat.
* **ONNX Runtime + Gemma 4 are blocked** in 2026 Q3
  ([microsoft/onnxruntime-genai #2062](https://github.com/microsoft/onnxruntime-genai/issues/2062)).
  The integration is real future work but not a path we can take today.
* **Induction is naturally slow-path.** It runs once per novel template
  and the output (a few CSS selectors) is cached forever. 10-30 seconds
  per induction call is fine; we never pay it on a cache hit.

The pivot: stop trying to load a per-element classifier on the hot
path. Instead, run the LLM **once** when a template is novel or known
to be weak, get back a small structured extractor, cache it, and never
call the LLM again for that template.

## Architecture

```
ExtractAsync(html, sourceUri)
  ├─ parse + clean
  ├─ if operator-template store has host
  │     → apply override, return                          [shipped v1.7]
  ├─ fingerprint
  ├─ if fast-path / slow-path hit
  │     → apply cached extractor, return                  [shipped v1.7]
  └─ novel-ephemeral path
        ├─ segment + classify (heuristic, runs always)    [shipped]
        ├─ induce a HeuristicLearnedExtractor             [shipped]
        ├─ cache it                                       [shipped]
        └─ enqueue a TemplateEnrichmentJob{host, fingerprint, skeleton}
                                                          [v2.0 new]

Background TemplateEnrichmentCoordinator (HostedService)
  ├─ drain the queue (oldest first; debounced per host)
  ├─ for each job:
  │     ├─ run LlmTemplateInducer over the skeleton
  │     ├─ parse YAML; validate selectors are non-empty
  │     ├─ if better than the heuristic (more blocks, higher
  │     │  classification confidence, runs cleanly against
  │     │  recent observations for this host):
  │     │     → replace the cached extractor; bump version
  │     └─ raise StyloExtractSignals.TemplateRefit signal
  └─ keep running; throttle per host + global QPS

Background TemplateVerificationScheduler (HostedService, v2.1)
  ├─ on a timer, sample N cached templates
  ├─ for each: ask the LLM "given these 3-5 recent sample
  │  pages, do your selectors still pick the right blocks?"
  └─ enqueue a refit if the LLM says no
```

The runtime hot path stays unchanged. The LLM never blocks a request.
Operators who don't wire an `ILlmTextProvider` see the v1.7 behaviour
exactly.

## The slim input representation

A 200 KB minified HTML page is ~50-80K tokens — too big for Gemma 4 E2B
(128K context with poor long-context recall) and uncomfortable for
12B (256K). It's also the wrong shape: the LLM is being asked "where
on a page LIKE THIS is the main content?", which is a question about
*structure*, not about the content itself. So we hand the LLM
structure + small exemplars, not raw HTML.

`DomSkeletonRenderer` composes existing primitives into a slim tree:

```
ROOT body
├─ header [hdr] children=4 — text="Home / Shop / Sale / Account" linkDensity=0.95
├─ section.hero [s1] children=2 — text="50% off everything – ends midnight"
├─ div.product-detail-root [s2] children=3 textLen=4280
│   ├─ h1.product__title — "Acme Widget v4"
│   ├─ div.product-description-body [s3] children=12 textLen=3982
│   │   ├─ p — "The Widget v4 is our flagship. After 3 years of …"
│   │   ├─ p — "Each unit is hand-finished in our Portland workshop…"
│   │   └─ … 10 more <p> siblings (omitted; same shape)
│   └─ div.reviews [s4] children=24 textLen=8401
│       └─ … 24 repeated .review children (3 exemplars below)
│           ├─ "★★★★★ This widget changed my life. Two months in…"
│           ├─ "★★★★☆ Solid, but the second hinge wiggles. Customer…"
│           └─ "★★★★★ Bought as a gift, recipient loved it. Will buy…"
├─ aside.related — children=5 linkDensity=0.91 — "You may also like"
└─ footer — "Privacy · Terms · Cookie Settings · Contact · © 2026 Acme"
```

What composes from existing pieces:

* `BlockSegmenter` → candidate elements (already drops what
  `DomCleaner` removed: `<script>`, `<style>`, etc.)
* `RepeatedItemDetector` → "this group has N siblings; emit 3
  exemplars and a count" instead of the full list
* `HeuristicBlockClassifier` per-candidate scoring → role hints in
  `[hdr]/[s1]/[s2]` brackets so the LLM sees what the heuristic
  thought
* `DomMarkdownWalker`'s text-extraction logic → 1-line excerpt per
  candidate (≤120 chars, whitespace-normalised)
* The 45-feature vector from
  [`StyloExtract.Ml.Features`](../src/StyloExtract.Ml/Features/)
  → `linkDensity` / `textLen` / `children` in the summary lines

What we drop: inline styles, `data-*` attributes, base64 image
payloads, comments, class tokens that look like CSS-modules /
Tailwind-JIT hashes, repeated children beyond ~3 exemplars per
group, anything inside `<script>`/`<style>`/`<svg>`/`<noscript>`.

Target output size: ≤4 KB / ~1.5K tokens for the median page;
≤12 KB / ~4K tokens for the worst case (forum threads, long
product pages). Comfortable inside any Gemma 4 variant's context.

## Output: an OperatorTemplate YAML

```yaml
host: weird-shopify-site.com
description: Induced by LlmTemplateInducer on 2026-06-24
version: 1
rules:
  - role: MainContent
    selectors:
      - div.product-detail-root
    confidence: 0.95
  - role: RepeatedItem
    selectors:
      - div.reviews > .review
    confidence: 0.9
  - role: PrimaryNavigation
    selectors:
      - header
    confidence: 0.85
  - role: Footer
    selectors:
      - footer
```

This is **exactly** the operator-template YAML shape from
[`operator-templates-design.md`](operator-templates-design.md). That
means:

* `YamlOperatorTemplateLoader.Parse` validates the LLM's output for
  free; a malformed response is rejected before it touches the cache.
* `OperatorTemplateAdapter.ToLearnedExtractor` turns it into a
  `LearnedExtractor` the existing `ExtractorApplicator` can run.
* The operator can hand-edit the LLM-induced template the same way
  they'd hand-write one from scratch.

The LLM is producing the same artefact the operator-template editor
shipped in v1.7. No new runtime types.

## Backend abstraction

`ILlmTextProvider` lives in `StyloExtract.Abstractions`. The name is
intentionally non-inducer-specific: this same client serves every
member of the [response-parser family](#response-parser-family-future)
below — template induction is one consumer, PII-redaction is another,
content-safety verification is a third.

```csharp
public interface ILlmTextProvider
{
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken);
}
```

Deliberately tiny. The provider returns raw text; each consumer is
responsible for parsing / validating / acting on it. `LlmTemplateInducer`
extracts the YAML block, parses via `YamlOperatorTemplateLoader`,
returns `OperatorTemplate`. A future `LlmPiiRedactor` would parse a
different response shape (e.g. a list of XPath-anchored redactions),
share zero of the parsing code, share all of the LLM-client wiring.

Implementations live elsewhere:

| Provider | Where | Notes |
|---|---|---|
| `OllamaTextProvider` | new `StyloExtract.Llm.Ollama` | Default. HTTP to local Ollama; configurable model. |
| `StyloBotLlmAdapter` | `Mostlylucid.BotDetection.StyloExtract` (in stylobot repo) | Bridges `ILlmProvider` (StyloBot's existing abstraction) to `ILlmTextProvider`. Operators in StyloBot deployments wire the same Ollama/Anthropic/OpenAI/Gemini config they already have. |
| Cloud providers | operator-supplied | Same shape; operators wire whatever fits their compliance posture. |

StyloExtract takes **no dependency** on `Mostlylucid.BotDetection.Llm`.
The adapter lives in the stylobot repo and only matters for stylobot
deployments.

## Model recommendation

Per [research notes](#research-trail) on Gemma 4:

* **Ollama default: `gemma4:e4b-it-qat`** (6.1 GB on disk, 128K
  context, Apache 2.0). Fits on a workstation CPU; 10-30 s per
  induction call. Pre-strip HTML to stay under context.
* **Higher recall: `gemma4:12b-it-qat`** (7.2 GB, 256K context).
  Stronger code/HTML output ([HF blog](https://huggingface.co/blog/gemma4));
  recommended on the slow-path coordinator if memory allows.
* **ONNX**: blocked for Gemma 4 today
  ([onnxruntime-genai #2062](https://github.com/microsoft/onnxruntime-genai/issues/2062)).
  Re-evaluate Q4 2026. If we need an in-process model right now,
  `Phi-4-mini-ONNX` is the fallback — at the cost of weaker HTML
  output.

Operator can pick any model that handles ~4K-token structured-output
prompts well. Co-Scraper used a Qwen 3 8B fine-tune; Gemma 4 12B is
the open-weights commercial-friendly equivalent.

## Background coordinators

Stylobot has the pattern: an `IEphemeralCoordinator` HostedService
drains a queue, runs an expensive operation, raises a signal when
done. We mirror it for template enrichment.

### TemplateEnrichmentCoordinator

* Subscribes to `StyloExtractSignals.TemplateNovel`.
* Enqueues `TemplateEnrichmentJob { Host, Fingerprint, Skeleton, Created }`.
* Background loop drains; per-host debounce so 100 concurrent visits
  to a novel host enqueue at most one job.
* For each job: call `LlmTemplateInducer.InduceAsync(skeleton, host)`;
  if the result parses and looks better than the heuristic
  (`HasBetterRecall(llmExtractor, heuristicExtractor)`), replace the
  cached extractor and bump the template version; raise
  `StyloExtractSignals.TemplateRefit`.
* Global QPS limit + per-host cooldown configurable. Default: 10 LLM
  calls per minute, 1 hour per-host cooldown so noisy hosts don't
  starve the queue.

### TemplateVerificationScheduler (v2.1)

* Timer (default 24 h). Picks N cached templates by sampling, biased
  toward templates that haven't been verified recently and templates
  whose drift score is elevated.
* For each: re-render skeleton on 3-5 recent observed pages for that
  host (read from a per-host observation log); ask the LLM "do these
  selectors still pick the right blocks?" Output: `keep` / `refit` /
  `archive`.
* `refit` enqueues a fresh `TemplateEnrichmentJob`; `archive` evicts
  the cached extractor so the next request goes through novel-ephemeral
  again.

### Learning crawl (v2.2+, deferred)

* Operator points the CLI at a sitemap or a small URL list for a host
  they want to onboard. The crawler fetches N pages, runs each through
  parse/segment/classify, builds a combined skeleton, and induces a
  template before any user request hits the host.
* Output: an `OperatorTemplate` written to the file root (so the
  hard-override path picks it up) OR the cached template index.
* Out of scope for v2.0; sketched here so the primitive design
  accommodates it (single-page skeleton extends naturally to a
  multi-page bundle).

## Package layout

```
src/
  StyloExtract.Abstractions/
    ILlmTextProvider.cs                              (new)
    TemplateEnrichment/
      TemplateEnrichmentJob.cs                          (new)
      ITemplateEnrichmentQueue.cs                       (new)
  StyloExtract.Core/
    Skeleton/
      DomSkeletonRenderer.cs                            (new)
    Llm/
      LlmTemplateInducer.cs                             (new)
      LlmInducerPrompts.cs                              (new; system + user templates)
    TemplateEnrichment/
      InMemoryTemplateEnrichmentQueue.cs                (new)
      TemplateEnrichmentCoordinator.cs                  (new HostedService)
  StyloExtract.Llm.Ollama/                              (new package, opt-in)
    OllamaInducerProvider.cs
  StyloExtract.AspNetCore/
    StyloExtractLlmInducerExtensions.cs                 (AddStyloExtractLlmInducer)

# in the stylobot repo:
src/Mostlylucid.BotDetection.StyloExtract.Llm/          (new pack)
  StyloBotLlmAdapter.cs                                 (bridges ILlmProvider → ILlmTextProvider)
```

The StyloExtract.Ml package stays — it owns the AOT-clean feature
extractor that the skeleton renderer uses for numeric summaries.
ONNX runtime is **removed from the dependency**; we'll re-add it
when Gemma 4 ONNX-GenAI support lands.

## Operator UX

```csharp
services.AddStyloExtract();
// Opt-in: wire any LLM provider; the default Ollama provider expects
// a local Ollama at http://localhost:11434.
services.AddStyloExtractLlmInducer(o =>
{
    o.Provider = LlmProviderKind.Ollama;
    o.OllamaUrl = "http://localhost:11434";
    o.Model = "gemma4:e4b-it-qat";
    o.MaxConcurrentInductions = 1;
    o.PerHostCooldown = TimeSpan.FromHours(1);
});
```

CLI:

```bash
# Ad-hoc: induce a template for a specific URL, print the YAML.
stylo-extract template induce --url https://weird-shopify-site.com/product/x

# Dump the skeleton that would be sent to the LLM (debug aid).
stylo-extract template dump-skeleton --url https://weird-shopify-site.com/product/x

# Existing template surface (operator-edited YAML) still works unchanged.
stylo-extract template add ... / list / show / remove / test
```

REST:

```
POST /api/styloextract/templates/{host}/induce
       body: { url } or { html }
       → returns the induced OperatorTemplate as YAML (operator can
         hand-edit before committing)
```

The induce endpoint sits next to the existing operator-template
endpoints. Operator workflow: try the LLM induction, eyeball the
output, optionally PUT it back as a hand-authored template (so the
hard-override path takes over).

## Quality bar (release-blocking for v2.0)

1. **Heuristic baseline doesn't regress.** Smoke runner against the
   `realworld/` fixtures must produce the same markdown for hosts the
   heuristic already handles. The LLM coordinator must never weaken a
   working template.
2. **Shopify-shape pages get a template.** Curate a held-out set of
   ~30 e-commerce / marketing landing pages. Pre-LLM baseline: 0
   `MainContent` on Shopify-shape (per realworld-fixtures.md). Target:
   ≥80% of the held-out set produces a cached template that yields
   non-empty `MainContent` on subsequent requests.
3. **Coordinator doesn't break the heuristic SLA.** Background work
   runs at most 10 LLM QPS; per-host cooldown 1 h; total background
   thread pool capped. Heuristic hot-path p99 stays unchanged.
4. **LLM output safety.** The YAML parser is the only path from LLM
   text to the cache. Selector strings get the same CSS-injection
   review the operator-template REST endpoints already got.

## Response-parser family (future)

`LlmTemplateInducer` is the first member of a wider family: focused
transformations over a parsed response, sharing the LLM client
(`ILlmTextProvider`), the slim-input representation
(`DomSkeletonRenderer`), the background coordinator pattern
(`TemplateEnrichmentCoordinator` is the model), and the
operator-template YAML output shape (for the ones that produce
templates) — but each implementing a different business transform.

Concrete members we expect once v2.0 ships:

| Parser | What it does | LLM? | Output |
|---|---|---|---|
| `LlmTemplateInducer` (v2.0, this design) | Novel-host template synthesis. | Yes | `OperatorTemplate` cached per host. |
| `LlmPiiRedactor` (future) | Walks the rendered markdown / blocks, emits a redaction policy (e.g. "anything matching this XPath, replace with `[redacted]`"). Triggered when an operator policy requires no-PII responses. | Yes | A `RedactionPolicy` applied at render time. |
| `LlmContentSafetyVerifier` (future) | Asks the model to flag pages whose body contains content the operator wants to refuse to serve (CSAM, malware indicators, etc.). | Yes | A boolean verdict + reason; raises a signal. |
| `RegexPiiRedactor` (future) | Same output shape as `LlmPiiRedactor` but rule-based for the cheap, common patterns (emails, phone numbers, SSNs). | No | Same `RedactionPolicy` shape. |
| `LlmDisclosureInjector` (future) | Adds a regulatory disclosure (cookie notice equivalent for content licensing, AI training opt-outs) when the operator's compliance posture requires one. | Optional | A markdown postfix or HTML wrapper applied at render time. |

What's common to all of them:

* **Slow-path-only.** Background coordinator drains a queue; the
  hot path never blocks.
* **Per-host cache.** Output is cached against the same template
  fingerprint the heuristic + LLM inducer use today. Re-render is
  pure `LearnedExtractor`-applicator territory.
* **Operator-overridable.** Same as templates: an operator-written
  YAML beats anything a parser produced.
* **Validates before caching.** YAML through the existing
  `YamlOperatorTemplateLoader`; redaction policies through a
  similar validator. A malformed model response is rejected, not
  cached.

The "response parser" abstraction itself is **not** in v2.0 scope.
Phase 3 ships `ILlmTextProvider` and `LlmTemplateInducer` as concrete
types; the family lives in the design doc as the next-after-this
direction. When the second parser lands (most likely
`RegexPiiRedactor` because it doesn't need the LLM), the common
interface gets extracted then.

## Out of scope for v2.0

* Crawl-time learning (v2.2+).
* Verification scheduler (v2.1).
* Per-tenant fine-tuned models — multi-tenant SaaS concern; not
  relevant for the FOSS edition.
* In-process ONNX inducer — blocked by Gemma 4 ONNX-GenAI support.

## Implementation plan (when approved)

Phase 3a — the primitive:
  * `DomSkeletonRenderer` (Core) — pruned tree + exemplars from
    segmenter + classifier + repeated-item-detector + feature
    vector. Pure C#, AOT-clean.
  * `ILlmTextProvider` (Abstractions) — minimal interface. Named
    generically (not "inducer") because the response-parser family
    above will reuse it.
  * `LlmTemplateInducer` (Core) — composes skeleton + prompt +
    provider call + YAML parse + validation.
  * `LlmInducerPrompts` (Core) — system + user templates with the
    Co-Scraper-style "produce a small set of selectors" instruction.
  * Stub `ILlmTextProvider` in tests so the primitive is fully
    covered without needing a live LLM.

Phase 3b — background enrichment:
  * `ITemplateEnrichmentQueue` (Abstractions) +
    `InMemoryTemplateEnrichmentQueue` (Core).
  * `TemplateEnrichmentCoordinator` (Core, HostedService) drains
    the queue, calls the inducer, replaces cached extractors when
    the result is better.
  * `LayoutExtractor` enqueues when emitting
    `StyloExtractSignals.TemplateNovel` AND no operator template
    exists for the host.
  * Per-host cooldown + global QPS in options.

Phase 3c — Ollama provider + DI wiring:
  * `StyloExtract.Llm.Ollama` package + `OllamaInducerProvider`.
  * `AddStyloExtractLlmInducer` in `StyloExtract.AspNetCore`.
  * StyloBot adapter pack in the stylobot repo
    (`Mostlylucid.BotDetection.StyloExtract.Llm`).

Phase 3d — CLI + REST:
  * `stylo-extract template induce` / `dump-skeleton`.
  * `POST /api/styloextract/templates/{host}/induce`.

Phase 4+ — verification scheduler, learning crawl, ONNX path
(if/when Gemma 4 GenAI support lands).

Each phase ends with a commit and a green suite.

## Research trail

Two research passes informed the pivot:

1. **First pass** (content extractors): turned up rs-trafilatura,
   MarkupLM, Trafilatura. Wrong framing — those are per-page
   extractors, not template inducers.
2. **Second pass** (wrapper inducers): turned up AXE (Feb 2026),
   Co-Scraper (Jun 2026), XPath Agent (Feb 2025). LLM-based
   selector synthesis is the 2026 SOTA. Reader-LM-2 disqualified
   (CC-BY-NC-4.0 license). Pre-LLM encoders (MarkupLM, DOM-LM,
   FreeDOM, SimpDOM) are now baselines those papers beat.
3. **Gemma 4 specifics**: 5 variants (E2B / E4B / 12B / 26B-A4B /
   31B), Apache 2.0 (commercial OK), Ollama day-one. ONNX
   integration blocked on
   [onnxruntime-genai #2062](https://github.com/microsoft/onnxruntime-genai/issues/2062).
   E4B fits on CPU; 12B preferred on the slow-path coordinator.
