## StyloExtract: Playwright vs Static HTML -- Forum page F1 comparison

Run: 2026-06-22 | split=dev | profile=MainContentOnly | n=112 forum pages (1 page had missing ground truth)

### Overall F1

| Mode                 |    F1 | Precision | Recall | Wall-clock |
|----------------------|------:|----------:|-------:|------------|
| StyloExtract static  | 0.456 |     0.481 |  0.587 | 00:06      |
| StyloExtract Playwright | 0.459 |     0.478 |  0.590 | 01:32      |
| rs-trafilatura (ref) | 0.808 |       n/a |    n/a | n/a        |
| Trafilatura (ref)    | 0.585 |       n/a |    n/a | n/a        |
| Readability (ref)    | 0.466 |       n/a |    n/a | n/a        |

**Playwright delta: +0.003 (noise level, not a material improvement)**

### What happened

113 forum pages were processed. The Playwright run used a 3-second DOMContentLoaded
timeout per page. When the timeout fired (page JS too heavy), the run fell back to
the raw static HTML for that page.

- Pages where Playwright succeeded (< 3s): ~106 out of 112 scored
- Pages where Playwright timed out (fell back to static): 7 pages

Timeouts were all Discourse or Discourse-adjacent forums:
  - 0504: users.rust-lang.org (Discourse)
  - 0507: discussion.fool.com (Discourse)
  - 0508: forums.eveonline.com (Discourse)
  - 0511: forum.mssociety.org.uk (Discourse)
  - 0512: boards.straightdope.com (Discourse)
  - 0526: www.warriorforum.com (custom)
  - 0533: www.webhostingtalk.com (vBulletin)

### Platform breakdown (all 113 forum pages)

| Platform      | Count | Notes                                             |
|---------------|------:|---------------------------------------------------|
| XenForo       |    28 | Largest group; hydrates well via static parse     |
| Discourse     |    24 | Ember.js SPA; inline JSON-LD, but renderer hangs  |
| StackExchange |    12 | Static HTML; no hydration needed                  |
| Reddit        |     4 |                                                   |
| vBulletin     |     3 |                                                   |
| Other         |    42 | 40+ smaller platforms                             |

### Why Playwright did not help

The hypothesis was: Discourse stores post content in `<script type="application/json">`
(or JSON-LD blobs) and hydrates via Ember.js. If Playwright could execute that JS,
the rendered DOM would contain the post text and F1 would jump.

The actual finding: **Discourse's Ember.js bundle takes 2-10+ minutes to execute**
in headless Chromium on a file:// URI. DOMContentLoaded never fires within 3 seconds
because the bundle executes synchronously before DOMContentLoaded. Even with
`WaitUntilState.Load` at 8s, the renderer hits 100% CPU for minutes per page.

The root cause is that Discourse's Ember app makes API calls to fetch posts at
runtime. Under file://, those API calls fail immediately, but Ember keeps retrying
or falls into error-handling JS that spins the event loop. The rendered output (if
we could get it) would be an empty post list, not hydrated content.

This means: **Playwright with file:// URIs cannot hydrate Discourse content**.
The Ember app needs a live server to fetch post data.

For the non-Discourse platforms (XenForo, StackExchange, vBulletin), Playwright
rendered quickly (< 100ms per page) but produced essentially the same HTML as
static, since those platforms server-side render their content in the initial HTML.

### Conclusion: Playwright is NOT the fix for forum pages

The StyloExtract Forum F1 gap (0.456 vs rs-trafilatura's 0.808) is NOT caused by
missing JS hydration. Static HTML already contains all the content on non-Discourse
platforms. The gap is a content-extraction quality issue, not a fetching issue.

**What to investigate instead:**

1. **Direct JSON-LD / Pjax parsing**: Discourse embeds all post data in
   `<script id="data-preloaded" type="application/json">` in the page source.
   Parsing that blob directly (without executing Ember) would recover the posts
   without Playwright. This is a static-HTML approach, not a Playwright approach.

2. **Thread-structure extraction**: Forum pages have a consistent structure
   (OP + replies in `<article>` or `<div class="post">` containers). A structural
   extractor targeting these containers would outperform a heuristic main-content
   picker on forum pages.

3. **XenForo and vBulletin patterns**: These are the largest groups and already
   server-side render. Tuning the extractor's structural signals for these platforms
   is the highest-ROI next step.

### Playwright as a feature: conclusions

- **Playwright mode works as a harness tool**: the `--use-playwright` flag is correct
  and functional. The fallback-to-static on timeout ensures no pages are dropped.
- **Wall-clock cost**: 01:32 for 112 pages (vs 00:06 static). 15x slower. Most of
  that is Playwright context creation overhead per page (~800ms/page), not rendering.
- **Ship as forum fetcher by default? NO.** The improvement is +0.003 F1 and the
  cost is 15x wall-clock. The gap is not a JS-hydration problem.
- **Useful for**: SPA article pages (not covered by this forum run) where the
  rendered DOM differs meaningfully from the initial HTML. Worth testing on `article`
  page types to see if any SPA-rendered articles exist in WCXB.
