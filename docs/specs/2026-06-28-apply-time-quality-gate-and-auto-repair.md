# Apply-Time Quality Gate + Auto-Repair Loop

**Status:** spec; pending approval before implementation dispatch.

## Problem

Three concrete gaps confirmed by lucidVIEW FULL dogfood on Wikipedia + mostlylucid + (separately) BBC + Guardian:

1. **The heuristic picks the OUTERMOST semantic container even when a tighter id-anchored descendant exists.** `HeuristicBlockClassifier.cs:200-235` demotes div/section ancestors of `<main>`/`<article>` to Boilerplate, but never tightens to a descendant. Wikipedia's `<main>` contains the "33 languages" picker AND `<div id="mw-content-text">` which is the real article body. The classifier picks `<main>`, leaks the picker into MainContent.

2. **Apply-time quality signals are binary.** `IsApplicatorBroken` (LayoutExtractor.cs:212-232) only fires on three cases: combined text < 200, chrome-heavy total > 1000 with content < 100, or rule-miss-ratio > 70%. None catch the "produces ample text but mostly noise" case. Wikipedia's bad render passes all three with 66 KB of output that's half nav-link clutter.

3. **The LLM repair path is unreachable for the common case.** `MaybeEnqueueRepairAsync` (LayoutExtractor.cs:553-559) only fires when (a) markdown is below the FallbackMinTextLength threshold AND (b) a hand-authored operator template already exists. Bad-template hosts that produce above-threshold output AND don't have a hand-authored override — i.e. every host this could possibly help — get nothing. The LLM never gets a second look.

## Three moves, ordered by independence

Each move ships on its own and is testable in isolation. Order is rough impact-per-effort.

### Move 1 — Tighten-on-anchor in the heuristic picker

When a `<main>` or `<article>` wins MainContent under the current Step 1a rule, additionally look DOWN one level for a descendant div/section with:

- A stable id OR a stable class (filtered through `DefaultClassStabilityFilter`), AND
- Combined text covering ≥80% of the semantic element's prose text (text minus link-text descendants), AND
- Link density < 0.5

If exactly one such descendant exists, prefer it. If zero or many, keep the semantic element.

For Wikipedia: `<main>` qualifies; descendant `<div id="mw-content-text" class="mw-body-content">` has stable id + stable class + ≥80% of main's prose + low link density → picked. The language picker (sibling of mw-content-text) drops out of MainContent.

For BBC article pages: `<main>` qualifies; no single descendant carries ≥80% of main's prose (each section is balanced) → semantic element kept. No regression.

For mostlylucid: no `<main>` qualifies (the blog post wrapper isn't a `<main>`). Move 1 doesn't fire. Move 2 catches it.

**Implementation footprint:** new step in `HeuristicBlockClassifier` between current Step 1a (semantic promotion) and Step 1b (repeated-item detection). ~50 lines. Uses existing `DefaultClassStabilityFilter`, existing `ComputeLinkDensity`. New test fixture: Wikipedia article snapshot, BBC article snapshot, plus a regression case for sites where the tighten heuristic would over-tighten.

### Move 2 — Noisy-output quality gate

Extend `IsApplicatorBroken` (or add a sibling `IsApplicatorNoisy` that maps to the same `applicatorBugOut` flag) with two new checks scoped to MainContent / Article / RepeatedItem roles only:

**Link-text-ratio gate.** Sum text length of `<a>` descendants inside content-role blocks. Divide by total content-role text. Threshold ≥ 0.5 ⇒ broken.

**Pre-content link cluster gate.** Walk the first 25% of content-role text (by char count). Compute its link-text ratio. Walk the remaining 75% and compute the same. If the front-25% ratio > 0.7 AND the rest-75% ratio < 0.3, the template is leaking a pre-content boilerplate region (language picker, filter strip, share bar). Threshold tuning will need real-corpus data; 0.7/0.3 is a starting point.

When either fires: set `applicatorBugOut = true`. The existing refit + Move 3's enrichment-enqueue handle the rest.

**Scoping:** content-role blocks only. HN front page has zero MainContent (it's all RepeatedItem), so the gate is silent there. Navigation pages without a MainContent role also pass through untouched.

**Implementation footprint:** ~30 lines in `LayoutExtractor.IsApplicatorBroken`. New helper to compute link-text recursively (overlap with existing `ComputeLinkDensity` — extract into shared helper). Two new tests against fixtures.

### Move 3 — Auto-enrich every novel host + relax repair preconditions

Drop the operator-template precondition from `MaybeEnqueueRepairAsync`. Add per-host cooldown so a bad-template host doesn't queue infinite repairs.

```
MaybeEnqueueRepairAsync:
    if status is FastPathHit | SlowPathMatch
    AND (markdown.Length < FallbackMinTextLength
         OR applicatorBugOut)
    AND host not in cooldown window (default: 60 min)
    -> enqueue
```

Novel hosts already enqueue via `MaybeEnqueueEnrichmentAsync`. This adds the "we already have a template but Move 2 just told us it's noisy" path.

**Implementation footprint:** ~20 lines, mostly the cooldown tracker (ConcurrentDictionary<string, DateTime>; check + update on enqueue). The existing `InMemoryTemplateEnrichmentQueue._lastEnqueuedByHost` already implements a similar dedupe; reuse or copy that pattern.

## Architectural rationale

Why three separate moves instead of one big rewrite:

- **Move 1 is local to the picker.** It improves first-induction quality on the largest class of well-structured sites. No control-flow change, no LLM dependency. Cheap to test, cheap to revert.
- **Move 2 is local to `IsApplicatorBroken`.** It surfaces template badness as a runtime signal without changing what that signal does. Wire-up to refit + enrichment is existing code.
- **Move 3 is the closing-the-loop piece.** With Moves 1+2 in place, the system finally has a way to (a) try harder on first induction, (b) recognise its own bad output, (c) ask the LLM to fix it. Without Move 3 the new signals from Move 2 have nowhere to escalate to.

## Cost analysis

- Move 1: O(descendants of semantic element). Bounded by DOM depth. Microseconds per induction, runs once per novel host.
- Move 2: one extra DOM walk per applied template to compute the link-text ratios. Cacheable against the block list the applicator already produced. Probably <50µs added to the hot path.
- Move 3: hot-path overhead is one ConcurrentDictionary lookup (+ optional write). Negligible. LLM enrichment work happens off the hot path in `TemplateEnrichmentCoordinator`.

## False-positive analysis

- **HN front page (link-heavy by design).** No MainContent role; Move 2 silent. Move 1 doesn't fire (no `<main>`/`<article>` with substantial prose). Move 3 doesn't queue. No regression.
- **Sitemap / nav-index pages.** Likely have a MainContent that IS mostly links by design. Move 2 would flag them as broken. Move 3's cooldown caps damage to ~24 LLM calls/day per such host. The LLM will produce a similarly-link-heavy template; the next visit's Move 2 still flags it; cooldown holds. Acceptable degradation, not catastrophic. Open question whether to add an explicit `BlockRole.LinkIndex` allowance.
- **Blog posts with link-heavy intro (lots of nuget badges, GitHub links, etc.).** Could trip the "pre-content link cluster" gate. Threshold tuning matters here. Best to validate against a corpus that includes mostlylucid-style intros.

## Open questions

1. **Move 1 — id-only or id+class.** Wikipedia's `#mw-content-text` has both. `#bodyContent` also exists as a sibling/ancestor. Pick the deeper or the one with both signals?
2. **Move 2 — should the thresholds be tunable per ExtractionProfile?** RagFull is stricter than Sitemap; the same gate firing on a Sitemap profile is wrong.
3. **Move 3 — should the cooldown be backed by the SQLite store (survives restarts) or in-memory only?** In-memory means a flap-y dogfood loop (start app → bad page → enqueue → restart → bad page → enqueue again) can spam the LLM. Suggest SQLite-backed.
4. **Re-induction after LLM repair — does Move 1 apply at the LLM-induction skeleton level?** When the LLM proposes a template, the inducer's apply-validator runs the selectors. Move 1 doesn't intercept this. Probably fine — the LLM's prompt is supposed to prefer tighter selectors anyway, and validation will catch unfilled selectors. Worth keeping eyes on.
5. **What about the streaming-side template?** Same architecture problem (a `StreamingTemplate` with bad tripwires will keep matching forever with no quality gate). Out of scope for this spec but the same shape (apply-time gate + cooldown-backed repair) would apply.

## Test plan

Per move, against the dogfood corpus:

| Site | Move 1 expected | Move 2 expected | Move 3 expected |
|---|---|---|---|
| en.wikipedia.org/wiki/Markdown | tighten to `#mw-content-text` | silent (Move 1 fixed it) | enrichment fires on cooldown reset |
| www.mostlylucid.net/blog/* | no `<main>`, no fire | gates fire (lang-flag picker) | enrichment fires |
| www.bbc.co.uk/news | (need real fixture, currently crashes) | likely silent on article pages | only on bad templates |
| en.wikipedia.org/wiki/Special:Search | no `<main>` qualifying | maybe-fire | enrichment fires |
| news.ycombinator.com | no `<main>` qualifying | silent (no MainContent role) | no enrichment (no repair signal) |
| www.iana.org/help/example-domains | semantic `<body>`, no descendant | silent | silent |

## Cross-cutting: where this lives in the codebase

- `src/StyloExtract.Heuristics/HeuristicBlockClassifier.cs` — Move 1
- `src/StyloExtract.Core/LayoutExtractor.cs` — Move 2 (`IsApplicatorBroken` extension) + Move 3 (`MaybeEnqueueRepairAsync` precondition relaxation + cooldown)
- `src/StyloExtract.Core/TemplateEnrichment/InMemoryTemplateEnrichmentQueue.cs` — Move 3 cooldown (or new sibling SQLite-backed store)
- `tests/StyloExtract.Heuristics.Tests/` — Move 1 fixtures
- `tests/StyloExtract.Core.Tests/` — Move 2 + Move 3 fixtures
- `tests/StyloExtract.IntegrationTests/` — end-to-end: apply bad template → bug-out → enqueue → drain → write `<host>.yaml`

## What this is NOT

- Not a streaming-side change. Streaming templates have the same shape of problem but their quality gate would be different (byte-level, not block-level). Separate spec when needed.
- Not a new inducer mode. Move 1 changes how the existing heuristic picks; Move 2 changes how the system measures its own quality; Move 3 changes when the existing LLM inducer fires. No new inducer class.
- Not a corpus-mining change. Phase 2's `EvolvedSelectorCandidate` path is orthogonal and stays as-is.
- Not a per-host hand-rule. The whole point is automatic.

## Rollout

Land in order: Move 1 first (immediate quality improvement on well-structured sites, no LLM cost), Move 2 second (apply-time signal, no LLM cost), Move 3 third (closes the loop, depends on Move 2 to actually trigger).

Each move ships as a separate commit. Each gets bench numbers against the dogfood corpus before the next move lands.

## Decisions needed before implementation dispatch

1. Approve the three-move split and the order.
2. Resolve Open Questions 1-5 above (or accept the suggested answers and revisit later).
3. Pick a starting threshold pair for Move 2 (0.5 link-text-ratio, 0.7/0.3 pre-content cluster) or defer to bench-driven tuning.
4. Decide whether Move 3's cooldown is in-memory (simpler) or SQLite-backed (survives restarts, harder to test).