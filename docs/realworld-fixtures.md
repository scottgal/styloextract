# Real-world smoke fixtures

The benchmark project's `--realworld` mode reads HTML files from
`tests/StyloExtract.Performance.Benchmarks/Fixtures/realworld/`. Those
files are git-ignored because every one is third-party content fetched
live from a public site at a point in time and is not ours to redistribute.

To populate the directory for local smoke-running:

```bash
cd tests/StyloExtract.Performance.Benchmarks/Fixtures/realworld
UA='Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'

curl -sL -A "$UA" --max-time 15 https://www.bbc.co.uk/news                                                        > bbc-news.html
curl -sL -A "$UA" --max-time 15 https://www.theguardian.com/uk                                                    > guardian.html
curl -sL -A "$UA" --max-time 15 https://en.wikipedia.org/wiki/Markdown                                            > wikipedia.html
curl -sL -A "$UA" --max-time 15 https://github.com/scottgal/styloextract                                          > github-readme.html
curl -sL -A "$UA" --max-time 15 https://ghost.org/                                                                > ghost-home.html
curl -sL -A "$UA" --max-time 15 https://ghost.org/changelog/                                                      > ghost-changelog.html
curl -sL -A "$UA" --max-time 15 https://news.ycombinator.com/news                                                 > hn.html
curl -sL -A "$UA" --max-time 15 https://www.allbirds.com/                                                         > shopify-home.html
curl -sL -A "$UA" --max-time 15 https://stratechery.com/                                                          > stratechery.html
curl -sL -A "$UA" --max-time 15 https://medium.com/                                                               > medium.html
```

Then run:

```bash
dotnet run --project tests/StyloExtract.Performance.Benchmarks -c Release -- --realworld
```

## Baseline behaviour (heuristic classifier only, 2026-06-23)

What the current classifier produces against the live fixtures above:

| Site | Outcome | Notes |
|---|---|---|
| BBC News (home) | Works | PrimaryNavigation + MainContent + Footer; 14 KB markdown out. |
| Guardian (home) | Works | MainContent identified; 20 KB markdown out including the live blog. |
| Wikipedia article | Works | 28 KB clean markdown; headings, links, infobox table. |
| GitHub repo page | Works | README rendered as MainContent; repo sidebar as RepeatedItems. |
| Ghost home | Partial | MainContent identified; some marketing sections demoted to Boilerplate. |
| Ghost changelog | Works | Per-entry headings preserved. |
| HN front page | Works structurally | Whole layout becomes one GFM table (HN IS a table). |
| Allbirds (Shopify) | Fails | All 10 blocks classified as Boilerplate; no MainContent. |
| Medium homepage | Fails | 5.8 KB JS-only stub; needs Playwright path. |

## Where the gaps point

Two distinct failure modes. They want different treatments.

1. **Custom-framework e-commerce / SPA marketing pages** (Allbirds, etc.):
   the page has structure but its class names don't match any of the
   recognisers in `framework-content-class-hints.json`. Adding more entries
   to that JSON is whack-a-mole — every theme shop ships new class names.
   This is the **ML/centroid feature** scope: cluster page shapes, transfer
   labels from observed templates of the same shape.

2. **JS-required SPAs** (Medium with no SSR fallback): the HTML
   we curl has no content; rendering happens client-side. Needs the
   `StyloExtract.Playwright` headless-browser path.

Neither is a heuristic-classifier problem. Avoid patching
`HeuristicBlockClassifier` to chase these. Per-host operator template
overrides (see [`operator-templates-design.md`](operator-templates-design.md))
give operators a manual escape hatch in the interim.
