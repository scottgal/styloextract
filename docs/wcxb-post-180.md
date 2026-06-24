## StyloExtract heuristic v1.3 vs WCXB baselines (dev split, profile=MainContentOnly, mode=static-HTML, page-types=all)

Run: 2026-06-24 21:47 UTC | pages=1495 | errors=2 | wall-clock=00:24

| System            |     F1 | Precision | Recall | p50 latency | p99 latency |
|-------------------|-------:|----------:|-------:|------------:|------------:|
| StyloExtract v1.3 | 0.673  | 0.649     | 0.810  | 8 ms       | 88 ms       |
| rs-trafilatura    | 0.859  | 0.863     | 0.890  | -           | -           |
| Trafilatura       | 0.791  | 0.852     | 0.793  | -           | -           |
| Readability       | 0.675  | 0.685     | 0.713  | -           | -           |

With-recall: 56.7% | Without-reject: 84.5%

### F1 by page type

| Page type     | StyloExtract | rs-traf | Trafilatura | Readability |
|---------------|-------------:|--------:|------------:|------------:|
| Article (n=792) |        0.807 |   0.932 |       0.926 |       0.825 |
| Documentation (n=91) |        0.819 |   0.932 |       0.888 |       0.736 |
| Service (n=165) |        0.672 |   0.844 |       0.763 |       0.604 |
| Forum (n=112) |        0.420 |   0.808 |       0.585 |       0.466 |
| Collection (n=117) |        0.346 |   0.716 |       0.553 |       0.445 |
| Listing (n=99) |        0.437 |   0.707 |       0.589 |       0.496 |
| Product (n=119) |        0.424 |   0.641 |       0.567 |       0.407 |

### Latency detail

| Percentile | Latency |
|------------|--------:|
| p50        | 8 ms |
| p90        | 27 ms |
| p99        | 88 ms |
| max        | 946 ms |

Total pages processed: 1495 | Errors: 2 | Error rate: 0.1%
