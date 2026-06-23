# StyloExtract ML Block Classifier (v2) — Design

**Status:** design, not yet implemented.
**Scope:** the arbitrary-site coverage gap surfaced in
[`realworld-fixtures.md`](realworld-fixtures.md): pages where the heuristic
classifier finds zero `MainContent` because the host's framework /
theme / templating layer doesn't use `<main>` / `<article>` and uses
class names absent from `framework-content-class-hints.json`. Operator
templates (`operator-templates-design.md`) give a manual escape hatch;
this design is the automated story for everything else.

## Why ML, not more heuristics

The current `HeuristicBlockClassifier` reads tag, class/id tokens, link
density, text length, depth, and a fixed list of class-name hints. It
works on news, Wikipedia, GitHub, Ghost, HN — anywhere semantic tags
are present OR the class names match known CMS frameworks. It **fails**
on:

* Shopify / BigCommerce / WooCommerce themes with custom class names.
* Headless-CMS marketing pages (Sanity, Contentful, Strapi outputs).
* Single-page-app sites with server-rendered initial HTML that uses
  utility-class systems (Tailwind, UnoCSS) where class names carry no
  semantic meaning.

Adding more entries to `framework-content-class-hints.json` is whack-
a-mole: every theme shop ships new class names every release. A trained
model can learn the *shape* of content-bearing blocks (text mass + link
density + sibling positioning + ancestor depth + heading-presence)
without knowing the specific class names a given site uses.

## Architectural constraints

Hard requirements derived from how StyloExtract ships:

1. **AOT-compatible.** The gateway ships AOT. The ML runtime must work
   under `PublishAot=true` — that rules out anything that requires
   runtime codegen or reflection over arbitrary types.
2. **Sub-ms on the hot path.** Inference budget is ≤500µs per page
   (typical 30-80 candidate elements per page; ≤10µs per element if
   batched, ≤50µs per element if sequential).
3. **No Python at runtime.** Training can use Python; inference must
   be pure .NET (or a .NET-callable native runtime).
4. **Optional, not load-bearing.** Heuristics must continue to work
   when the ML package isn't referenced. The gateway must remain
   buildable without ML.
5. **Operator overrides still take precedence.** ML runs only when
   `IOperatorTemplateStore.TryGet(host)` misses.

## Runtime composition

```
ExtractAsync(html, sourceUri)
  └─ parse + clean
  └─ if operator-template store has host  → apply override, return     [v1]
  └─ fingerprint
  └─ if fast-path / slow-path hit         → apply cached extractor      [v1]
  └─ novel-ephemeral path:
     └─ segment
     └─ classify
        ├─ HeuristicBlockClassifier (unchanged, runs always)            [v1]
        └─ MlBlockClassifier (new, optional, runs after heuristics)     [v2]
           ├─ for each candidate the heuristic emitted, plus any
           │  segmented element heuristics skipped, compute features
           │  and score via ONNX model
           └─ late-fusion: per-element learned score adds a bonus
              to the heuristic score, OR overrides it when the
              heuristic produced "Unknown" / "Boilerplate"
     └─ induce + register template
  └─ render
```

The ML classifier doesn't replace the heuristic — it augments it. The
heuristic is fast and confident on semantic markup; the ML adds a
"this looks like content" signal on the novel-CSS cases the heuristic
misses.

## Model architecture (MVP)

**Task:** per-element classification. Input = feature vector for one
candidate element. Output = probability distribution over `BlockRole`
(13 classes — see `BlockRole` enum).

**Model family:** gradient-boosted decision trees via **LightGBM**.
Reasoning:

* **Fast inference**: ≤10µs per element with ~500 trees.
* **Interpretable**: feature-importance plots are part of the operator
  UX. We tell operators "your custom site fails because text-length
  feature 0.3, link-density feature 0.5 don't pattern-match anything
  in training" — actionable feedback.
* **AOT-clean**: ships as ONNX, runs under `Microsoft.ML.OnnxRuntime`.
* **No GPU**: a 5MB model file, CPU-only inference.
* **Robust to feature scale**: no normalisation needed (unlike NNs).

Alternative rejected: tiny MLP. Faster training but less interpretable
and trickier to AOT through ONNX Runtime's reflection paths.

## Features (per-element vector, ~45 dimensions)

Grouped to match `HeuristicBlockClassifier`'s existing signals plus
new ones the heuristic doesn't use:

**Tag identity (one-hot, 12 dims):**
`<main>`, `<article>`, `<section>`, `<aside>`, `<nav>`, `<header>`,
`<footer>`, `<form>`, `<table>`, `<pre>`, `<div>`, `<other>`.

**Class-name signals (8 dims):**
Hash-bucketed presence of the class attribute (8 buckets via murmur3).
Avoids embedding string lookups while still letting the model learn
co-occurrence patterns.

**Density / text features (10 dims):**
Text length (log-scale), link density, image density (new), word
count (new), heading-descendant count (new), paragraph-descendant
count (new), list-item descendant count (new), p:h ratio (new),
input-element count, button-element count.

**Position features (5 dims):**
Depth in DOM, position-from-start (normalised), position-from-end
(normalised), parent's child count, sibling-text fraction.

**Sibling-shape features (5 dims):**
Number of repeated sibling elements with same tag, similarity score
to repeated-item shape (new — derived from `RepeatedItemDetector`),
sibling tag entropy.

**Ancestor features (5 dims):**
Has `<main>` ancestor, has `<article>` ancestor, has `<nav>` ancestor,
has `<form>` ancestor, has `<aside>` ancestor.

Feature extraction is **AOT-clean** — pure C# walking the AngleSharp
DOM. No string allocations on the hot path: write into a
pre-allocated `Span<float>`.

## Training data

**Cold start (v2.0):** the WCXB labeled corpus —
[Web Content Extraction Benchmark](https://github.com/scrapinghub/wcxb).
Already used by the existing F1 evaluation harness
(`tests/StyloExtract.Wcxb.Benchmark/`). Per page, the gold annotations
identify the MainContent boundary; we project that to per-element
labels by:

* Every descendant of the gold MainContent → `MainContent`.
* Every descendant of an inferred `<nav>` / `<header>` / `<footer>` →
  the corresponding role.
* Everything else → `Boilerplate`.

Yields ~200K labeled elements across the WCXB dev split.

**Warm start (v2.1, deferred):** operator-template observations.
Operators who define templates effectively label pages on their
hosts. Logged anonymously (host hash + per-element features + label),
those become additional training samples. Lets the model adapt to a
deployment's specific long tail of sites without anyone hand-labelling.

## Package layout

```
src/
  StyloExtract.Ml/                          (new, optional package)
    StyloExtract.Ml.csproj
    Features/
      ElementFeatureExtractor.cs            (pure C#, AOT-clean)
      FeatureNames.cs                       (45 const strings; debug only)
    Inference/
      OnnxBlockClassifier.cs                (Microsoft.ML.OnnxRuntime)
      MlBlockClassifier.cs                  (IBlockClassifier wrapper)
    Models/
      block-classifier-v2.onnx              (embedded resource, ~5MB)
  StyloExtract.Heuristics/                  (unchanged)
  StyloExtract.AspNetCore/
    StyloExtractMlExtensions.cs             (AddStyloExtractMl())
training/                                   (separate workflow, not in
  README.md                                  pack list, not in solution)
  pipeline.py                               (LightGBM training)
  wcxb_to_features.py                       (label projection)
  features_to_onnx.py                       (LightGBM → ONNX export)
```

The training subtree is **not** part of the .NET solution. It runs
out-of-band; commits to `block-classifier-v2.onnx` ship the trained
weights into the package.

## Inference flow

```csharp
// In MlBlockClassifier.Classify:
var heuristicResults = _heuristic.Classify(elements);    // existing
if (!_options.Enabled) return heuristicResults;          // ML disabled

// Batched scoring: one ORT session.Run per page, not per element.
var features = new float[elements.Count * FeatureDim];
for (int i = 0; i < elements.Count; i++)
    _featureExtractor.Extract(elements[i], features.AsSpan(i * FeatureDim, FeatureDim));

var probs = _onnx.Run(features);                          // [N x 13]

// Late fusion: for each candidate the heuristic produced, add a
// learned-confidence delta. For elements the heuristic dropped
// (Boilerplate) where the ML says high-confidence MainContent,
// promote to MainContent with the ML's confidence.
return Fuse(heuristicResults, probs, _options);
```

`Fuse` policy:

* **Heuristic confident (≥0.85)**: keep the heuristic label.
  Don't second-guess `<main>` / `<article>` semantic tags.
* **Heuristic + ML agree**: keep heuristic label, take
  `max(heuristic.Confidence, mlProbability)`.
* **Heuristic uncertain (Boilerplate at 0.3-0.5) AND ML says
  MainContent with p ≥ 0.8**: promote to MainContent at the ML's
  probability.
* **Heuristic + ML disagree on a content-bearing role**: take the
  higher-confidence label. Log a signal (`ml.disagreement`) so
  operators can see the rate.

## Operator UX

ML is **opt-in per deployment**. New service-registration helper:

```csharp
services.AddStyloExtract();
services.AddStyloExtractMl();                    // loads embedded model
// or
services.AddStyloExtractMl(modelPath: "/etc/styloextract/model-v2.onnx");
```

CLI flag for ad-hoc testing:

```bash
stylo-extract extract https://allbirds.com/ --ml
stylo-extract extract https://allbirds.com/ --ml --explain   # feature contributions
```

REST: no new endpoints. The ML augmentation is transparent — same
`ExtractedBlock` shape, same `Markdown` output. A new
`ExtractionStats.MlAugmented` field counts how many blocks the ML
changed vs. the heuristic baseline.

## Operator visibility

Two new dashboard surfaces (defer to v2.1):

1. **Per-host ML disagreement rate.** "On `weird-shopify.com`, the
   ML re-classified 3 of 7 blocks. Most-promoted role: MainContent.
   Most-demoted: Boilerplate." Tells operators when ML is doing
   real work for them and when it's noise.
2. **Feature-importance explainer.** For a specific extraction,
   show which features pushed the ML toward each role. Surfaces
   why a site succeeds or fails without the operator reverse-
   engineering CSS class names.

## Quality bar (release-blocking)

Train + ship v2.0 only when:

1. **WCXB F1 doesn't regress.** Heuristic-only baseline is the
   existing F1 ledger; heuristic+ML must be ≥ the baseline on
   every page type (article, documentation, service, forum,
   collection, listing, product).
2. **Shopify-shape pages improve.** Curate a held-out set of 30
   ecommerce / marketing landing pages (Allbirds, Notion, Linear,
   Stripe, etc.). Pre-ML baseline: 0 MainContent on Shopify-shape
   today. Target: ≥75% recall of the visible body content on
   this set.
3. **Inference budget met.** Per-page inference ≤ 500µs p99 on
   Apple M5 / .NET 10 (existing benchmark hardware).
4. **No new ALWAYS_FAIL failure mode.** Run the full test suite
   plus the smoke runner against `realworld/` fixtures; nothing
   that worked pre-ML may regress.

## Out of scope for v2.0

* **Operator-template self-training loop.** Logged operator labels
  → retraining pipeline. Defer to v2.1 once we know the model is
  earning its keep on the cold-start corpus.
* **LLM-based labelling for slow path.** Separate feature; the
  v1 spec already documents the deferred LLM track.
* **Per-tenant fine-tuned models.** Multi-tenant SaaS concern;
  not relevant for the FOSS edition.
* **Cross-host clustering as an inference signal.** Already
  excluded in the v1 spec.
* **Dynamic feature engineering.** All 45 features are fixed and
  ship with the model. A v3 would consider letting operators
  declare custom features for their tenant.

## Risks

1. **Model staleness on novel CMS releases.** Mitigation: the warm-
   start retraining loop (v2.1). Cold-start cadence: ship a refreshed
   model every minor release.
2. **ONNX Runtime native binaries inflate the package.** ~25MB per
   RID. Ship `StyloExtract.Ml` as a separate optional NuGet so the
   core packages stay small; consumers opt in.
3. **Class-hash collisions confuse the model.** 8-bucket murmur is
   coarse; bumping to 32 buckets is straightforward if recall
   suffers. Validate against held-out Shopify pages.
4. **Operator training loop leaks per-tenant content if mishandled.**
   v2.1 must hash features + role labels and never log raw text. The
   feature extractor by design produces a zero-PII vector; verify in
   tests before shipping the retraining endpoint.

## Open questions for review

* Should we ship a smaller (~1MB) "lite" model alongside the full
  ~5MB one for hosts that just need basic main-content detection?
* The 45-feature surface — is anything missing that
  `HeuristicBlockClassifier` reads but I haven't enumerated?
* Should `MlBlockClassifier` ALSO run for elements the heuristic
  emitted as `MainContent`/`Article` (to second-guess heuristic
  false positives), or only for elements it dropped? Initial
  design says "only for dropped" to preserve heuristic semantics on
  known-good cases; needs validation against the F1 bar.

## Implementation plan (when approved)

Phase 1 — feature extractor + model contract (no inference yet):
  * `ElementFeatureExtractor` with all 45 features.
  * `FeatureNames` debug surface.
  * Unit tests asserting feature values on synthetic HTML shapes.
  * Pure C#, no ONNX dep yet.

Phase 2 — training pipeline:
  * `training/wcxb_to_features.py`: walk WCXB labeled pages, project
    to per-element labels, write a TSV of `(features, label)`.
  * `training/pipeline.py`: LightGBM train, ROC-AUC report,
    feature-importance dump.
  * `training/features_to_onnx.py`: export to ONNX via `onnxmltools`.
  * Initial model artefact committed to the repo.

Phase 3 — ONNX runtime inference:
  * `OnnxBlockClassifier` wrapping `Microsoft.ML.OnnxRuntime`.
  * `MlBlockClassifier` (`IBlockClassifier`) composing the heuristic
    + ML with the late-fusion policy above.
  * `AddStyloExtractMl()` DI helper.
  * Bench: per-element inference time, per-page inference time,
    F1 vs. heuristic-only baseline.

Phase 4 — operator UX:
  * CLI `--ml` and `--ml --explain` flags.
  * `ExtractionStats.MlAugmented` counter.
  * Smoke-runner integration: every realworld fixture runs through
    both heuristic-only and heuristic+ML, deltas reported.

Each phase ends with a commit; suite stays green at every step.
