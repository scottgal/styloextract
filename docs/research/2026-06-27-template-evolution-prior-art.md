# Template Evolution — Prior Art Dossier

Date: 2026-06-27
Author: Research pass for StyloExtract template-evolution design.
Status: Research only; informs but does not bind future implementation.

---

## 1. Executive summary

The plan to "store every template, mine the corpus for cross-template stability, evolve selectors emergently" sits in well-trodden academic ground but at an unusual operating point: most prior work is either (a) one-shot site-level wrapper induction run offline over a fetched page set (RoadRunner, EXALG, DEPTA, Hao et al.), or (b) per-page heuristic content extraction with no learning at all (Readability, Boilerpipe, Trafilatura). The "online, accumulating, self-evolving" point is occupied mostly by commercial tools (Diffbot, Kadoa, Scrapling, Nimble) with limited technical disclosure.

Five findings that should shape the design:

1. **Multi-page consensus is the dominant idea.** Every site-level system from RoadRunner (Crescenzi et al. 2001) through Vieira et al.'s alignment work (2006/2013) does the same thing: align N pages of the same site, retain what is invariant across pages as template, treat what varies as data. ~16 sample pages is the empirically cited point of diminishing returns (Vieira-cited in template-extraction benchmarking literature). StyloExtract already implicitly approximates this with the MinHash fingerprint store but does not yet do alignment-based selector mining over that store.
2. **Selector-strategy priority is converged.** Every "shortest unique selector" tool (finder.js, css-optimum-selector, SelectorGadget) and every per-domain extractor library (Mercury custom extractors, Trafilatura's XPath cascade) ranks selector specificity in the same order: `ID < class/data-attr < tag < nth-of-type < nth-child`. finder.js encodes this as integer penalty weights 0/1/2/5/10/50 — directly portable to a `BlockRule` cost function.
3. **Wrapper induction has had a stable formal frame for 25 years.** Kushmerick's HLRT/PAC framing (1997, expanded 2000) and its descendants (LR, OCLR, HOCLRT) are an old, well-understood family. The "store-and-evolve" plan is essentially online sample accumulation feeding offline induction — Kushmerick's PAC bound on sample complexity provides a principled stopping criterion for how many examples per host before a template is "learned."
4. **Stability scoring and concept drift have a clean primitive.** Ross et al.'s "EWMA charts for concept drift" (Pattern Recognition Letters 2012, arXiv:1212.6018) is O(1) per observation, no stored data points, controlled false-positive rate — a direct match for `DecayingReputationWindow` on per-selector hit/quality signal.
5. **Recent neural work is mostly orthogonal.** Web2Text (Vogels et al. ECIR 2018), SimpDOM (Zhou et al. 2021), MinerU-HTML/AICC (Nov 2025) treat boilerplate as a per-block classification or sequence-labeling problem; they don't produce reusable selectors. Useful as a quality oracle for ranking competing learned selectors, not as a replacement for selector induction.

The plan as scoped is sound, the academic basis is solid, and there is no obvious off-the-shelf system that already does the per-host-stored-corpus-with-cross-template-evolution combination — closest are commercial self-healing scrapers (Scrapling, Kadoa) whose internals are proprietary.

---

## 2. Per-question dossier

### 2.1 Multi-page template induction

The canonical algorithms:

- **RoadRunner** — Crescenzi, Mecca, Merialdo (Università Roma Tre / Basilicata), VLDB 2001. "RoadRunner: Towards Automatic Data Extraction from Large Web Sites." [PDF](https://www.vldb.org/conf/2001/P109.pdf). Compares HTML pages pairwise as tag-streams; learns a Union-Free Regular Expression that fits all observed pages; mismatches between current grammar and a new page trigger generalisation (optional/repeated structures inserted). Wrapper = the generalised regex.
- **EXALG** — Arasu, Garcia-Molina, SIGMOD 2003. "Extracting Structured Data from Web Pages." [Microsoft Research PDF](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/extract.pdf). Two-stage: (1) ECGM finds *equivalence classes* — sets of tokens that co-occur with the same frequency and adjacency across pages, taken to be template tokens; (2) Analysis uses differentiating roles within equivalence classes to deduce the nested template. Insight: any token, not just tags, can be template.
- **MDR / MDR-2 / DEPTA** — Liu, Grossman, Zhai (UIC), KDD 2003 ("Mining Data Records in Web Pages") and WWW 2005 ("Web Data Extraction Based on Partial Tree Alignment"). [MDR PDF](https://www.cs.uic.edu/~liub/publications/KDD-03-techReport.pdf), [DEPTA PDF](https://www.cs.uic.edu/~liub/publications/www05-p516.pdf). MDR uses string matching on DOM tag sequences to find data-record regions. DEPTA improves: uses MDR-2's bounding-box rendering for tree construction, then *partial tree alignment* — aligns only data items that can be aligned with high confidence, leaves the rest uncommitted.
- **From One Tree to a Forest** — Hao, Cai, Pang, Zhang, SIGIR 2011. Created the SWDE benchmark (124,291 pages, 80 sites, 8 verticals). Standard for cross-site generalisation evaluation since.
- **Vieira et al.'s multi-sequence alignment** — "A Fast Method for Web Template Extraction via a Multi-sequence Alignment Approach" (Springer 2013). Aligns HTML tag streams from pairs of pages; merges via binary-tree consensus. Cited as supporting "~16 pages is enough."

What is actually useful for StyloExtract:

- Partial alignment from DEPTA is the right primitive: don't try to align the entire DOM, align only confident matches and let the rest float. Maps onto a `BlockRule` confidence dimension already in `LearnedExtractor`.
- RoadRunner's "mismatch triggers generalisation" loop is the operational model that fits the EphemeralWorkCoordinator background-mining job: each new sample either confirms the current template (bump reputation) or triggers a generalisation attempt.
- EXALG's equivalence-class idea is the deeper version: don't just look at tag positions, look at any feature (class name, data-attr value) that co-occurs with the same frequency across pages — those are template features, not content features.

### 2.2 Selector minimization / generalization

Production-grade open-source minimizers:

- **finder.js** — antonmedv. [GitHub](https://github.com/antonmedv/finder). Greedy A\*-style search with integer penalty weights: id=0, class=1, attribute=2, tag=5, nth-of-type=10, nth-child=50. Seed minimum length 3, then yields sorted candidates; greedy path-reduction post-pass removes middle ancestors that aren't needed for uniqueness. 1s default timeout. The penalty schedule is the load-bearing artefact — every other tool converges on the same priority order.
- **css-selector-generator** (fczbkk) — similar approach, falls back to wildcard `*` or `:nth-child` only when no unique combination found.
- **css-optimum-selector** (indix) — explicitly named "optimum"; applies filters to id/class/attribute/tag.
- **SelectorGadget** — interactive: click highlighted/unhighlighted elements to refine, generates minimal selector covering the positive set and excluding the negative set. [Site](https://selectorgadget.com/).
- **CSS Minification via Constraint Solving** — Bird, Lin, et al., 2018, arXiv:1812.02989. Reduces CSS3 selector intersection to SMT over quantifier-free integer linear arithmetic. Overkill for runtime use but proves the formalisation is well-defined.

What is useful:

- The 0/1/2/5/10/50 penalty schedule is the de facto industry consensus. Drop-in replacement for the current "ancestor chain → strip indices" behaviour. Even just adopting this without any cross-template mining would close most of the current gap.
- finder.js's "seed length 3, then sort candidates" is good operational advice: cap chain depth, prefer breadth (more features per node) over depth (longer ancestor chain).

### 2.3 Selector strategy ranking

Hand-tuned heuristic systems are explicit about their priority. Documented from primary sources:

- **Mozilla Readability** — [DeepWiki](https://deepwiki.com/mozilla/readability). Scores top-5 candidate ancestors; propagates scores up the tree; penalises link density; merges siblings around the winner to recover content split by ads/widgets; has an "unlikely candidates" regex blacklist matching common class/id patterns (`sidebar`, `share`, `comment`, `nav`). Conditional cleanup removes tables/forms/empty tags post-extraction. Heuristic-only, no learning.
- **Mercury Parser / Postlight Parser** — [custom extractors README](https://github.com/postlight/parser/blob/main/src/extractors/custom/README.md). Per-domain custom extractors, each a CSS selector tuple per field (title, content, author, date, lead image). Hand-maintained per site. Cached/packaged extractors + an API-registered extractor model. The "library of templates" StyloExtract is building is a learned version of this.
- **Trafilatura** — Barbaresi, ACL 2021 demo paper. [GitHub](https://github.com/adbar/trafilatura). XPath-rule cascade for known patterns, falls back to jusText then Readability when its own rules fail. Bevendorff et al.'s SIGIR 2023 comparison ranks Trafilatura best in mean F1 (~0.883).
- **Goose / Newspaper3k** — heuristic algorithms, hand-crafted rules around line length and link density.
- **DOM Distiller** — [Chromium repo](https://github.com/chromium/dom-distiller). Loosely based on Boilerpipe; ships as Chrome Reader Mode. README is light on detail; algorithm is in the source.
- **GNE (GeneralNewsExtractor)** — Chinese news. Uses text-to-tag ratio as the primary signal, supports custom XPath override and a noise-node blacklist. [GitHub](https://github.com/GeneralNewsExtractor/GeneralNewsExtractor).

Empirical comparison: Bevendorff, Gupta, Kiesel, Stein, SIGIR 2023, "An Empirical Comparison of Web Content Extraction Algorithms." [PDF](https://downloads.webis.de/publications/papers/bevendorff_2023c.pdf). Reproducibility study; benchmark dataset and code at [chatnoir-eu/web-content-extraction-benchmark](https://github.com/chatnoir-eu/web-content-extraction-benchmark). Murrough Foley's [murrough-foley/web-content-extraction-benchmark](https://huggingface.co/datasets/murrough-foley/web-content-extraction-benchmark) is a later 2,008-page extension.

What is useful:

- Readability's link-density penalty and "unlikely candidates" regex blacklist are basically free wins — both are signal sources StyloExtract could feed into selector reputation scoring without changing the storage model.
- Trafilatura is the right benchmark baseline. If a self-evolving StyloExtract can't beat 0.883 mean F1 on the Bevendorff benchmark, the evolution isn't doing anything.
- Mercury's custom-extractor file format is a useful interchange target: a learned template, exported, should look more or less like a Mercury custom extractor.

### 2.4 Cross-host template transfer (the generalisation goal)

This is the thin part of the literature. What exists:

- **Hao et al. 2011 SIGIR "One Tree to a Forest"** — proposes a single seed-site annotation that generalises across the vertical (e.g. one labelled camera-spec site generalises to all camera-spec sites). Cross-site generalisation is the SWDE benchmark's whole point. Method: vision-style feature extraction + alignment.
- **SimpDOM** — Zhou, Sheng, Vo, Edmonds, Tata, 2021, arXiv:2101.02415. [PDF](https://arxiv.org/pdf/2101.02415). Few-shot DOM-node tagging with explicit cross-vertical out-of-domain transfer setting; +1.44% F1 on SWDE intra-vertical, +1.37% additional cross-vertical. Treats it as a node classification problem; transfer = pretrained representations across verticals.
- **CERES** — Lockard, Dong, Einolghozati, Shiralkar, 2018, arXiv:1804.04635. Distant supervision from a knowledge base; learns relation extractors that transfer across sites in the same domain.
- **Clustering Web pages based on structure** — Crescenzi, Mecca, Merialdo, DKE 2005 ([PDF](http://www.cs.man.ac.uk/~pmissier/docs/sdarticle.pdf)). Site-family detection: cluster pages by DOM-tree shape, every cluster shares a template.
- **Diffbot's Knowledge Graph** — commercial. Public claim ([Diffblog](https://blog.diffbot.com/how-computer-vision-helps-get-you-better-web-data/)): computer-vision-first classification into ~20 page types, then per-type ML extraction. Knowledge fusion across pages forms entities. Patent search via [patents.google.com](https://patents.google.com/?inventor=mike+tung,+diffbot) returns hits but most public-facing documentation is non-technical marketing.

What is useful:

- "Cluster hosts by structural similarity, mine the cluster for shared selectors" is the operational version of the generalisation goal. The MinHash fingerprint already gives StyloExtract the structural-similarity primitive; one LSH-band probe identifies the cluster. The missing piece is the mining step: given N hosts judged similar, which `BlockRole→selector` rules generalise (the same selector works on all) vs. specialise (different selector per host, same role).
- SimpDOM's intra-vertical / cross-vertical split is the right evaluation harness: hold out a host, train on the rest, see if induced template works.

### 2.5 Stability scoring + drift detection

- **Ross, Adams, Tasoulis, Hand (2012)** — "Exponentially weighted moving average charts for detecting concept drift." Pattern Recognition Letters. arXiv:1212.6018. EWMA on classifier misclassification rate, O(1) overhead, controlled false-positive rate, no stored history. Direct match for per-selector hit-rate tracking.
- **EWMA control charts in QA** — standard SPC tool, well-trodden in semiconductor manufacturing for small-drift detection. Tuning parameters are well understood (λ ≈ 0.1–0.3 for slow drift, higher for fast).
- **Self-healing-scraper vendors** — Scrapling, Kadoa, Nimble, Atherial. Public-facing claims: fingerprint pages, watch selectors across runs, alert on drift, regenerate selectors. No published algorithms; advertising language only.
- **DOM-fingerprint based monitoring** — the standard industry pattern: hash the relevant subtree, compare across runs, alert on hash change. The MinHash + LSH approach StyloExtract already uses is the principled version.

What is useful:

- EWMA on per-selector hit rate (success = selector matched and produced output; quality = downstream signal-loss check) is the right structure for `DecayingReputationWindow`. Cite Ross et al. 2012 in the design doc — it gives the false-positive-rate guarantee.
- The two-signal pattern (fingerprint drift + output quality drift) is what the industry converges on. StyloExtract already has both; the question is how to combine them into a single refit-trigger.

### 2.6 DOM tree alignment (foundational)

- **Zhang-Shasha** — Zhang & Shasha, 1989. "Simple fast algorithms for the editing distance between trees and related problems." Foundational TED algorithm, O(mn) space, O(m²n²) worst case time.
- **RTED** — Pawlik & Augsten. Tree-shape-independent; computes only as many subproblems as the best competitor must.
- **APTED** — Pawlik & Augsten, "A New Perspective on the Tree Edit Distance" (SISAP 2017). Improves RTED. Standard go-to for general TED. [Springer](https://link.springer.com/chapter/10.1007/978-3-319-68474-1_11). Toolkit at [tree-edit-distance.dbresearch.uni-salzburg.at](https://tree-edit-distance.dbresearch.uni-salzburg.at/).
- **pq-grams** — Augsten, Böhlen, Gamper, 2010. "The pq-Gram Distance between Ordered Labeled Trees." Approximate TED via fixed-size subtree shingles; lower time complexity than exact TED, parameterised by (p,q). Good fit for batch clustering of stored templates.
- **GumTree, ChangeDistiller** — source-code-tree-diff tools; produce human-readable edit scripts. Used in software-engineering literature; the algorithmic core is reusable for DOM diff.
- **Tutorial** — Paaßen, "Revisiting the tree edit distance and its backtracing: A tutorial," arXiv:1805.06869. Best modern reference.

What is useful:

- **At request time:** TED is too expensive on full DOMs. Keep using MinHash fingerprints for the fast path.
- **In the background miner:** pq-grams on stored templates is the right tool. Cheaper than exact APTED, gives clustering-quality similarity, parameter tuning is documented.
- **For selector mining:** when you do need to align two stored templates to find shared structural anchors, APTED on the *role-skeleton* (only the nodes carrying a BlockRule) — not the full DOM — keeps the alignment problem tractable.

### 2.7 Schema.org / microdata / OpenGraph / JSON-LD as priors

The literature is thin here because the engineering answer is obvious: if structured markup is present, use it. Sources:

- **WebDataCommons** ([webdatacommons.org/structureddata](https://webdatacommons.org/structureddata/)) — Common Crawl extractions of Microdata, RDFa, JSON-LD, Microformats. Long-running dataset; ~40% of Common Crawl domains carry some structured markup as of recent crawls.
- **Schema.org validator** — combines JSON-LD, Microdata, RDFa with heuristics for missing-value cases. No published precedence rules in the formal sense, but JSON-LD is the recommended primary format.
- **Trafilatura** extracts metadata from JSON-LD, Open Graph, Twitter Cards before falling back to heuristics.

What is useful:

- StyloExtract should have a JSON-LD/microdata pre-pass before learned-selector application. When structured markup is present and well-formed it's strictly better than any learned heuristic for title, author, datePublished, articleBody. Cite Trafilatura as the existence proof — they do this and it works.
- This is a one-day change with high payoff; should not be conflated with the harder cross-template-evolution work.

### 2.8 Commercial / open-source landscape

| System | Core approach | Per-host learning? | Selector quality |
|---|---|---|---|
| **Mozilla Readability** | Heuristic scoring + link-density penalty + unlikely-candidate regex | No | N/A (no selectors emitted) |
| **Mercury / Postlight Parser** | Per-domain custom extractors hand-maintained | Curated, not learned | Hand-tuned CSS |
| **Trafilatura** | XPath rule cascade + fallbacks | No | Best F1 in 2023 SIGIR benchmark |
| **Diffbot Article API** | CV page-type classification + per-type ML | Yes (claim, internal) | Opaque |
| **Newspaper3k / goose3** | Line-length + link-density heuristics | No | N/A |
| **DOM Distiller** | Boilerpipe-inspired shallow features in Chromium | No | N/A |
| **Boilerpipe** | Shallow text features (block-level classifier) | No | N/A |
| **GNE** | Text-to-tag ratio | No | XPath override only |
| **AutoScraper** | Multi-example pattern induction at the per-page level | Per-task, not per-host | Generated rules |
| **Scrapling / Kadoa / Nimble** | "Self-healing" — proprietary | Claimed | Proprietary |
| **MinerU-HTML / AICC (2025)** | 0.6B-param sequence-labeling LLM | No | Markdown output, no selectors |

What StyloExtract actually competes with:

- For the **per-page extraction quality** axis, the bar is Trafilatura (0.883 F1 on Bevendorff). Hard to beat with selectors alone; the win from per-host templates is consistency on the long-tail sites where Trafilatura's general heuristics underperform.
- For the **per-host learned template** axis, the only known competitors are Diffbot (closed) and the self-healing-scraper vendors (closed). StyloExtract is the open implementation; this is the actual differentiator.

### 2.9 Genetic / evolutionary selector evolution

Effectively a literature gap.

- Search for "genetic algorithm CSS selector" and "evolutionary wrapper" returns feature-selection-in-ML papers using "wrapper methods" in the unrelated SFS/SBS sense (DWFS by Soufan et al., PLOS ONE 2015; etc.) — not relevant.
- No surfaced paper applies population/mutation/crossover/fitness directly to CSS selectors.
- AutoScraper-related arXiv paper (arXiv:2404.12753) "AutoScraper: A Progressive Understanding Web Agent for Web Scraper Generation" is LLM-agent based, not GA.

What this means for StyloExtract: the "evolve selectors" framing is not a well-trodden algorithmic path. Two non-GA paths that are well-trodden and fit the storage model:

- **Beam search over selector candidates** (the finder.js approach generalised across N pages instead of one).
- **MDL / Minimum Description Length** template induction — pick the template whose code-length-plus-data-length is shortest. EXALG is a special case. Generalises naturally across stored templates.

A genuine GA framing is publishable-novel territory but should be treated as research, not engineering.

### 2.10 Web mining literature surveys

Three commonly-cited surveys (the "if you only read one" papers):

- **Chang, Kayed, Girgis, Shaalan (2006)** — "A Survey of Web Information Extraction Systems," IEEE TKDE 18(10):1411–1428. Taxonomic survey; introduces the supervised / semi-supervised / unsupervised / manual axes.
- **Ferrara, De Meo, Fiumara, Baumgartner (2014)** — "Web data extraction, applications and techniques: A survey," Knowledge-Based Systems. Practical taxonomy + application catalogue.
- **Bevendorff et al. (2023)** — "An Empirical Comparison of Web Content Extraction Algorithms." SIGIR 2023. Recent, reproducible benchmark. The definitive head-to-head as of 2026.

Plus the meta-analysis worth flagging:
- **Bar-Yossef et al. / Bevendorff et al. ancestors** — Webis group has run this series for over a decade. Their tooling and benchmarks are the standard.
- **"Web Content Extraction — a Meta-Analysis of its Past and Thoughts on its Future,"** Lautenschlager / Lauer et al., arXiv:1508.04066 (2015). Useful for the 1995–2015 historical arc.

---

## 3. Recommended influences

Concrete and actionable, each item bound to a StyloExtract component:

1. **Adopt finder.js's penalty schedule for `CssSelectorGeneralizer`.** Replace the current "strip indices off ancestor chain" with a weighted A*-style search using id=0, class=1, attr=2, tag=5, nth-of-type=10, nth-child=50. Single biggest quality lift available; one focused PR.
2. **Per-page JSON-LD / Open Graph / microdata pre-pass before learned-selector application.** Lift the relevant extraction logic structure from Trafilatura. Mark extracted fields as "from-markup" in `LearnedExtractor` so the corpus mining doesn't conflate them with learned selectors.
3. **Use Readability's `unlikely candidates` regex blacklist as a hard prefilter in the inducer.** Stops the inducer ever proposing a selector ending in `.sidebar`, `#share`, `.comments` etc. Free precision lift.
4. **EWMA on per-selector quality signal, parameters from Ross et al. 2012.** Wire it into `DecayingReputationWindow`. Per-rule λ ≈ 0.1–0.2; alarm threshold tuned to bound false-positive refit rate. Cite the paper in the design doc.
5. **Background miner over the WriteBehindLfuStore should do partial-alignment-style consensus, not full alignment.** DEPTA (Zhai-Liu WWW 2005) is the right model: align only the high-confidence subtrees across N templates per host, leave the rest uncommitted. Map "aligned subtree" → "rule that survived the corpus."
6. **Cluster hosts by MinHash similarity (already there), then mine the cluster.** For each cluster, find selectors that work on every host in the cluster — those are the generalisable rules. Selectors that work on only one host are the specialised rules. This is the "evolve from stable" goal made operational.
7. **pq-grams (Augsten et al.) for background clustering of stored templates.** Cheaper than APTED on full DOMs; parameters (p,q) are well documented.
8. **Treat the SWDE benchmark (Hao et al. 2011) and the Bevendorff 2023 benchmark as twin evaluation harnesses.** SWDE for structured-field extraction precision; Bevendorff for main-content F1. Without both, claims about improvement are unanchored.
9. **Use a neural quality oracle as a "judge" when ranking competing learned selectors, not as the extractor.** Trafilatura or MinerU-HTML output as ground truth for "did this selector pick the right thing"; the actual selector remains the extractor. Keeps the runtime path heuristic and the offline mining path learnable.
10. **Export learned templates in a Mercury-custom-extractor-shaped format.** Forward-compatible with a wide existing ecosystem and gives a debugging-friendly serialisation alongside the binary store.

---

## 4. What doesn't transfer

Honest list of approaches that look relevant but don't fit:

- **Supervised wrapper induction (Kushmerick HLRT, WIEN, Stalker)** — requires labelled training pages per host. StyloExtract doesn't have labels and isn't going to; the inducer must work from heuristic-rule output as pseudo-labels. The PAC-bound framing is still useful as a stopping rule for "how many samples is enough."
- **Visual / rendered-DOM approaches (DEPTA's MDR-2 step, Diffbot, MinerU-HTML)** — require a browser render at induction time. Playwright is already in StyloExtract's optional path; consider this only as an opt-in inducer mode, not the default. Cost is too high.
- **Full Tree Edit Distance at request time** — even APTED is too slow on a real-world DOM (thousands of nodes). Stay with MinHash for the fast path; reserve TED for background mining over already-distilled role-skeletons.
- **Boilerpipe / Web2Text per-block classifiers** — output is a content/boilerplate label per block, not a selector. Useful as a quality oracle, not as a selector source.
- **LLM-based extractors as the runtime path (MinerU-HTML, AutoScraper-LLM, ScrapeGraphAI)** — wrong cost profile for the per-request workload. Useful offline to bootstrap or verify learned templates.
- **CSS-selector SMT minimisation (Bird et al. 2018)** — theoretically attractive but SMT solving in the inner loop is the wrong tool. Greedy with the finder.js penalty schedule wins on engineering grounds.
- **Genetic algorithms on selectors** — sparse literature, no clear evidence over simpler beam-search / MDL approaches. Avoid unless this becomes an explicit research goal.
- **Diffbot's CV-first approach** — gorgeous demo, but requires the rendered page + a trained vision model + a large labelled corpus. Wrong building blocks for an open library.

---

## 5. Reading list (ranked by relevance)

1. **Crescenzi, Mecca, Merialdo (2001), "RoadRunner: Towards Automatic Data Extraction from Large Web Sites,"** VLDB. [PDF](https://www.vldb.org/conf/2001/P109.pdf). *Why:* The canonical paper. The mismatch-triggers-generalisation loop is the operating model StyloExtract should adopt for its background miner.
2. **Zhai & Liu (2005), "Web Data Extraction Based on Partial Tree Alignment,"** WWW. [PDF](https://www.cs.uic.edu/~liub/publications/www05-p516.pdf). *Why:* Partial alignment is the right primitive for cross-template mining — don't try to align everything, align what aligns confidently.
3. **Bevendorff, Gupta, Kiesel, Stein (2023), "An Empirical Comparison of Web Content Extraction Algorithms,"** SIGIR. [PDF](https://downloads.webis.de/publications/papers/bevendorff_2023c.pdf). *Why:* The current benchmark of record. Defines "good" for content extraction. Trafilatura's 0.883 F1 is the bar.
4. **Ross, Adams, Tasoulis, Hand (2012), "Exponentially Weighted Moving Average Charts for Detecting Concept Drift,"** PRL. [arXiv:1212.6018](https://arxiv.org/abs/1212.6018). *Why:* O(1) drift detection with controlled false-positive rate. Drop-in for `DecayingReputationWindow`.
5. **Hao, Cai, Pang, Zhang (2011), "From One Tree to a Forest: A Unified Solution for Structured Web Data Extraction,"** SIGIR. *Why:* Created the SWDE benchmark and demonstrated cross-site generalisation as a tractable problem. Plus SWDE is your evaluation harness.
6. **Arasu & Garcia-Molina (2003), "Extracting Structured Data from Web Pages,"** SIGMOD. [PDF](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/extract.pdf). *Why:* Equivalence-classes idea — template features include any high-frequency co-occurring token, not just tags. The generalisation hook for cross-host mining.
7. **Kohlschütter, Fankhauser, Nejdl (2010), "Boilerplate Detection Using Shallow Text Features,"** WSDM. [PDF](https://www.ccs.neu.edu/home/vip/teach/IRcourse/6_ML/other_notes/Boilerplate%20Detection%20using%20Shallow%20Text%20Features.pdf). *Why:* The features (link density, text density, average word length, anchor-text-to-text ratio) are cheap quality signals — useful as oracle inputs for selector-quality scoring.
8. **Zhou, Sheng, Vo, Edmonds, Tata (2021), "Simplified DOM Trees for Transferable Attribute Extraction from the Web,"** arXiv:2101.02415. *Why:* Modern formalisation of cross-vertical transfer. Their few-shot intra-vertical / cross-vertical evaluation split is the right one to copy.
9. **Augsten, Böhlen, Gamper (2010), "The pq-Gram Distance between Ordered Labeled Trees."** *Why:* The right tree-similarity tool for the background miner. Parameter (p,q) tuning is documented.
10. **Kushmerick (2000), "Wrapper Induction: Efficiency and Expressiveness,"** Artificial Intelligence 118:15–68. *Why:* The PAC-bound framing tells you, principledly, how many samples per host before a template is "learned." Stopping rule for the miner.

Optional but worth a skim:
- Paaßen (2018), "Revisiting the tree edit distance and its backtracing: A tutorial," [arXiv:1805.06869](https://arxiv.org/abs/1805.06869) — best modern TED reference.
- Vieira et al. (2013), multi-sequence-alignment template extraction ([Springer chapter](https://link.springer.com/chapter/10.1007/978-3-642-37186-8_11)) — the "~16 pages suffice" empirical anchor.

---

## 6. Open questions

What the literature didn't settle, and where StyloExtract will need to design from first principles:

1. **What's the right unit of "template" for cross-host mining?** RoadRunner / EXALG / DEPTA all operate on one host at a time. Hao et al. and SimpDOM show cross-host transfer is tractable for *attribute extraction* (find the price field across camera-spec sites) but not specifically for the "BlockRole + selector" tuple model StyloExtract uses. Open: does generalisation happen at the selector level (the same CSS string works on multiple hosts — unlikely except for schema.org-style markup), at the structural-pattern level (e.g. "the H1 inside the main inside the article element"), or at a learned-embedding level?
2. **How to combine fingerprint drift and output-quality drift into one refit trigger?** Both signals exist; the literature treats them separately. The product-of-two-EWMAs vs. the OR-of-two-thresholds vs. a single EWMA over a combined-signal scalar is undecided.
3. **What's the minimum cluster size for cross-host generalisation to be safe?** Vieira et al. cite "~16 pages per host." There's no equivalent number for "~N hosts per cluster" — needs empirical work on SWDE.
4. **How does the background miner avoid catastrophic forgetting?** When a new sample contradicts an established rule, when do you (a) update the rule, (b) keep both as alternatives ranked by reputation, (c) split the template? RoadRunner's grammar-mismatch trigger always generalises; this loses information. EXALG never generalises (one-shot batch). The online intermediate is undocumented.
5. **What's the right pseudo-label oracle?** Using Trafilatura or MinerU-HTML output as ground truth for selector quality is reasonable but circular if StyloExtract is also being evaluated against them. Alternative: per-host human verification of a small sample, then mine consistency relative to that small ground truth.
6. **Does the JSON-LD pre-pass interact badly with learned templates?** A host that publishes good JSON-LD will get JSON-LD extraction; the learned template never gets observations and atrophies. Need a policy: train templates anyway from JSON-LD-derived field positions, or accept the trade-off and only learn templates for markup-less hosts?
7. **Self-healing vendor benchmarks are not public.** Scrapling, Kadoa, Nimble all claim self-healing; none publish numbers comparable to Bevendorff. Without a head-to-head, the "open competitor to the closed self-healers" claim can't be substantiated.

---

## Appendix: search-trail notes

Sources consulted and worth re-finding:

- VLDB / SIGIR / WWW / WSDM digital libraries — all foundational wrapper-induction papers.
- arXiv cs.IR — modern transfer / neural extraction work.
- Webis group ([webis.de](https://webis.de/)) — current state-of-the-art benchmark maintainer.
- [tree-edit-distance.dbresearch.uni-salzburg.at](https://tree-edit-distance.dbresearch.uni-salzburg.at/) — Augsten group's TED toolkit and reference.
- [chatnoir-eu/web-content-extraction-benchmark](https://github.com/chatnoir-eu/web-content-extraction-benchmark) — Bevendorff 2023 data and code.
- [murrough-foley/web-content-extraction-benchmark](https://huggingface.co/datasets/murrough-foley/web-content-extraction-benchmark) — 2,008-page extended benchmark.
- [github.com/postlight/parser/tree/main/src/extractors/custom](https://github.com/postlight/parser/tree/main/src/extractors/custom) — Mercury custom-extractor zoo, useful as an interchange-format reference.
- [github.com/antonmedv/finder](https://github.com/antonmedv/finder) — penalty schedule reference implementation.

Items flagged as "uncited — secondary source claim":

- "~16 sample pages is enough for site template extraction with good accuracy" — paraphrased from search-result summary of Vieira et al.'s multi-sequence alignment work; the exact empirical anchor needs verification from the primary paper.
- Diffbot's "20 page types" classification number — Diffblog/SitePoint posts, not a peer-reviewed source.
- Mercury Parser is "deprecated" / Postlight handover details — search-result summary; check the current state of [postlight/parser](https://github.com/postlight/parser).
- Self-healing scraper vendor (Scrapling / Kadoa / Nimble / Atherial) capability claims are all marketing, no peer-reviewed verification.
